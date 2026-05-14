// Portions of this file are derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt),
// used under the MIT License (Copyright (c) 2025 Sukasa). See the repository NOTICE file.
//
// The simulation rewrite below (proportional load sharing, sliding-window probabilistic cable
// burnout, NaN-power guards, provider bookkeeping) follows Re-Volt's RevoltTick. Power Grid Plus
// removes Re-Volt's circuit-breaker / load-center machinery and adds:
//   * an unlimited super-heavy cable carve-out (NEW-1), and
//   * a hook into the three-tier voltage gating that suppresses generator output on a non-heavy
//     network (NEW-3).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using Cysharp.Threading.Tasks;
using PowerGridPlus.Patches;
using UnityEngine;

namespace PowerGridPlus.Power
{
    /// <summary>
    ///     Replacement for the vanilla <see cref="PowerTick"/>. One instance is injected into every
    ///     <see cref="CableNetwork"/> by <see cref="CableNetworkPatches"/>; the per-tick methods are
    ///     routed here from <see cref="PowerTickPatches"/>.
    /// </summary>
    public class PowerGridTick : PowerTick
    {
        private struct PowerUsage
        {
            public Device Device;
            public float PowerUsed;
            public float PowerProvided;
        }

        /// <summary>Set true whenever the network's device/cable membership changes; forces a rebuild on the next tick.</summary>
        public bool IsDirty = true;

        private readonly SortedList<float, List<Cable>> _allCables = new SortedList<float, List<Cable>>();
        private readonly SortedList<float, List<CableFuse>> _allFuses = new SortedList<float, List<CableFuse>>();

        private static readonly PropertyInfo _providerSetter;
        private static readonly PropertyInfo _ioDevSetter;

        private PowerUsage[] _powerData;

        // Cannot use UnityEngine.Random in a thread, so each tick gets its own deterministic RNG.
        private System.Random _rng;

        private float _powerRatio;
        private bool _isPowerMet;
        private float _powerUsageWindow;
        private List<PowerProvider> _powerProviders;

        // NEW-3: the cable tier of this network (it is single-tier once the burn-on-join backstop has run;
        // during the brief mixed-tier window this is whichever tier was seen first). Null if cableless.
        private Cable.Type? _networkTier;
        // NEW-1: true iff the weakest cable in this network is super-heavy (so the whole network is super-heavy).
        private bool _weakestCableIsSuperHeavy;
        // NEW-3: a mixed-tier network has been detected; we have asked for a cable burn to split it. Guards
        // against requesting another burn before the first one takes effect (the split replaces this tick).
        private bool _tierResolutionPending;
        // NEW-3: cached during Initialize_New (cheap, only on dirty rebuild). The actual burn fires from
        // ApplyState_New gated on the network having real power flow (_actual > 0).
        private bool _mixedTierDetected;
        // NEW-3: recorded during CalculateState_New (once per tick). Reset to null at the top of each tick.
        // First device this tick that's on a cable tier it isn't allowed on; ApplyState_New burns the cable
        // adjacent to it (if there's power flow). The device itself is never destroyed.
        private Device _misplacedDeviceForBurn;
        // NEW-3 (per-port): the wrong-tier cable directly at a two-port device's offending port (a
        // transformer with a cable that doesn't match its per-variant input / output tier requirement,
        // or an APC whose two sides are on different tiers). _portMismatchOwner is the device whose
        // port the cable sits on; only used for the log line and the tooltip reason.
        private Cable _portMismatchCableForBurn;
        private Device _portMismatchOwner;

        static PowerGridTick()
        {
            _providerSetter = typeof(PowerTick).GetProperty(nameof(Providers));
            _ioDevSetter = typeof(PowerTick).GetProperty(nameof(InputOutputDevices));
        }

        public PowerGridTick()
        {
            _rng = new System.Random();
            _powerUsageWindow = 0.0f;
        }

        private static float SanitizePower(float value)
        {
            // Vanilla bug: a device can report NaN (or, rarely, infinite) watts, which then poisons every
            // sum on the network. Treat anything that isn't a finite number as zero.
            if (float.IsNaN(value) || float.IsInfinity(value))
                return 0.0f;
            return value;
        }

        public void Initialize_New(CableNetwork from)
        {
            if (CableNetwork != from)
                IsDirty = true;

            CableNetwork = from;
            Potential = 0.0f;
            Required = 0.0f;
            Consumed = 0.0f;

            if (!IsDirty)
                return;
            IsDirty = false;

            _powerProviders = new List<PowerProvider>();
            _rng = new System.Random((int)from.ReferenceId);

            Devices.Clear();
            Fuses.Clear();
            _allCables.Clear();
            _allFuses.Clear();

            _providerSetter.SetValue(this, _powerProviders.ToArray());
            _ioDevSetter.SetValue(this, _powerProviders.ToArray());

            lock (CableNetwork.PowerDeviceList)
                Devices.AddRange(CableNetwork.PowerDeviceList);

            lock (CableNetwork.FuseList)
            {
                foreach (var fuse in CableNetwork.FuseList.Where(x => x != null))
                {
                    if (!_allFuses.ContainsKey(fuse.PowerBreak))
                        _allFuses.Add(fuse.PowerBreak, new List<CableFuse>(CableNetwork.FuseList.Count) { fuse });
                    else
                        _allFuses[fuse.PowerBreak].Add(fuse);
                }
            }

            _networkTier = null;
            bool mixedTier = false;
            lock (CableNetwork.CableList)
            {
                foreach (var cable in CableNetwork.CableList.Where(x => x != null))
                {
                    if (!_allCables.ContainsKey(cable.MaxVoltage))
                        _allCables.Add(cable.MaxVoltage, new List<Cable>(CableNetwork.CableList.Count) { cable });
                    else
                        _allCables[cable.MaxVoltage].Add(cable);

                    if (_networkTier == null)
                        _networkTier = cable.CableType;
                    else if (cable.CableType != _networkTier.Value)
                        mixedTier = true;
                }
            }

            _weakestCableIsSuperHeavy = _allCables.Count > 0
                                        && _allCables.Values[0].Count > 0
                                        && _allCables.Values[0][0].CableType == Cable.Type.superHeavy;

            if (_powerData == null || _powerData.Length != Devices.Count)
                _powerData = Devices.Select(x => new PowerUsage { Device = x }).ToArray();

            // NEW-3 detection: a network that ended up holding more than one cable tier (an old save with a
            // pre-existing illegal junction, or anything the build-time check missed). Record the fact here
            // (cheap, only on dirty rebuild); ApplyState_New fires the actual burn IF the network has real
            // power flow, so an idle / off network never destroys cables.
            _mixedTierDetected = mixedTier;
        }

        public Cable TestBurnCable(float powerUsed, float slidingWindow)
        {
            // Power transmitters create a "cable network" with no cables.
            if (_allCables.Keys.Count < 1)
                return null;

            // NEW-1: a network whose weakest cable is super-heavy never burns (it's the long-haul backbone).
            if (Settings.EnableUnlimitedSuperHeavyCables.Value && _weakestCableIsSuperHeavy)
                return null;

            // Within the rating of the weakest cable: no burn.
            if (Mathf.Min(slidingWindow, powerUsed) <= _allCables.Keys[0])
                return null;

            // Cheap estimate of the burn chance for this tick.
            var burnChance = (powerUsed / _allCables.Keys[0]) - 1.0f;

            if ((float)_rng.NextDouble() > burnChance * Settings.CableBurnFactor.Value)
                return null;

            // Burn one of the weakest cables.
            return _allCables.Values[0].Pick();
        }

        public CableFuse TestBlowFuse(float powerUsed)
        {
            if (_allFuses.Keys.Count == 0 || powerUsed <= _allFuses.Keys[0])
                return null;

            // A fuse blows immediately, no rolling-average delay.
            return _allFuses.Values[0].Pick();
        }

        public void CalculateState_New()
        {
            bool dirtyProviderList = false;
            int provIdx = 0;
            // NEW-3: record at most one misplaced device + at most one port-mismatched cable per tick;
            // ApplyState_New uses both AFTER the power-flow gate.
            _misplacedDeviceForBurn = null;
            _portMismatchCableForBurn = null;
            _portMismatchOwner = null;
            int idx = Devices.Count;
            while (idx-- > 0)
            {
                var currentDevice = Devices[idx];
                if (currentDevice == null)
                    continue;

                _powerData[idx].PowerUsed = SanitizePower(currentDevice.GetUsedPower(CableNetwork));
                _powerData[idx].PowerProvided = SanitizePower(currentDevice.GetGeneratedPower(CableNetwork));

                // NEW-3: don't suppress power here. Just record the first device that's on a tier it isn't
                // allowed on; ApplyState_New will burn its adjacent cable IF actual power is flowing.
                if (Settings.EnableVoltageTiers.Value
                    && _networkTier.HasValue
                    && _misplacedDeviceForBurn == null
                    && !VoltageTier.IsAllowedOnTier(currentDevice, _networkTier.Value))
                {
                    _misplacedDeviceForBurn = currentDevice;
                }

                // NEW-3 (per-port): two-port devices (transformer / APC) have port-specific tier rules.
                // Transformer has per-variant required tiers (Small heavy<->normal, Medium heavy<->heavy,
                // Large superHeavy<->heavy). APC requires both ports to be on the same tier. Either way,
                // a wrong-tier cable directly at the offending port is the burn victim.
                if (Settings.EnableVoltageTiers.Value && _portMismatchCableForBurn == null)
                {
                    Cable mismatch = null;
                    if (currentDevice is Transformer transformer)
                        mismatch = VoltageTier.FindMismatchedTransformerCable(transformer);
                    else if (currentDevice is AreaPowerControl apc)
                        mismatch = VoltageTier.FindMismatchedApcCable(apc);
                    if (mismatch != null)
                    {
                        _portMismatchCableForBurn = mismatch;
                        _portMismatchOwner = currentDevice;
                    }
                }

                Required += _powerData[idx].PowerUsed;

                if (_powerData[idx].PowerProvided != 0.0f)
                {
                    // Keep the Providers/InputOutputDevices arrays (used by the Network Analyzer cartridge) in sync.
                    if (_powerProviders.Count > provIdx && _powerProviders[provIdx].Device != currentDevice)
                    {
                        dirtyProviderList = true;
                        _powerProviders[provIdx] = new PowerProvider(currentDevice, CableNetwork);
                    }

                    Potential += _powerData[idx].PowerProvided;

                    if (_powerProviders.Count <= provIdx)
                    {
                        dirtyProviderList = true;
                        _powerProviders.Add(new PowerProvider(currentDevice, CableNetwork));
                    }

                    provIdx++;
                }
            }

            if (_powerProviders.Count > provIdx)
                _powerProviders.RemoveRange(provIdx, _powerProviders.Count - provIdx);

            if (dirtyProviderList)
            {
                _providerSetter.SetValue(this, _powerProviders.ToArray());
                _ioDevSetter.SetValue(this, _powerProviders.Where(x => x.Device.IsPowerInputOutput).ToArray());
            }

            if (Settings.EnableRecursiveNetworkLimits.Value)
                PowerTickPatches.CheckForRecursiveProviders(this);
        }

        public void ApplyState_New()
        {
            Potential = Mathf.Max(Potential, 0.0f);
            Required = Mathf.Max(Required, 0.0f);
            Consumed = Mathf.Min(Potential, Required);

            PowerTickPatches.CacheState(this);

            _powerRatio = Required == 0.0f ? 1.0f : Mathf.Clamp(Potential / Required, 0.0f, 1.0f);
            _isPowerMet = _powerRatio >= 0.99f;

            var demandRatio = Potential == 0.0f ? 0.0f : Mathf.Clamp(Required / Potential, 0.0f, 1.0f);
            var powerFlow = Mathf.Min(Required, Potential);

            // Sliding-window average of network throughput over roughly the last 10-20 seconds. A cable
            // only burns when the *current* flow is over its rating AND the rolling average has stayed
            // high enough to call the cable overheated. Short surges never burn a cable; fuses and the
            // (now-removed) breakers still react to instantaneous flow.
            _powerUsageWindow = Mathf.Lerp(_powerUsageWindow, powerFlow, 0.1f);

            var burnCable = TestBurnCable(powerFlow, _powerUsageWindow);
            var burnFuse = TestBlowFuse(powerFlow);

            bool power = false;

            if (burnFuse != null)
            {
                burnFuse.Break();
            }
            else if (burnCable != null)
            {
                BurnReasonRegistry.RegisterPending(burnCable,
                    $"Overloaded -- sustained network throughput exceeded this cable's rating ({burnCable.MaxVoltage:0} W)");
                burnCable.Break();
            }
            else
            {
                power = true;

                // NEW-3: tier burns are gated on real power flow this tick. An idle / off network never
                // destroys cables. Priority order: mixed-tier-in-this-network (root cause) -> port
                // mismatch on a two-port device (transformer / APC) -> misplaced single-port device.
                // Resolving the higher-priority case usually fixes the lower-priority symptom on the
                // next tick's fresh network.
                if (powerFlow > 0f && Settings.EnableVoltageTiers.Value && !_tierResolutionPending)
                {
                    if (_mixedTierDetected)
                    {
                        _tierResolutionPending = true;
                        VoltageTier.ResolveMixedTierNetwork(CableNetwork);
                    }
                    else if (_portMismatchCableForBurn != null)
                    {
                        _tierResolutionPending = true;
                        VoltageTier.BurnPortMismatchCable(_portMismatchCableForBurn, _portMismatchOwner);
                    }
                    else if (_misplacedDeviceForBurn != null)
                    {
                        _tierResolutionPending = true;
                        VoltageTier.BurnCableForMisplacedDevice(_misplacedDeviceForBurn, CableNetwork);
                    }
                }
            }

            if (!power)
            {
                // Whatever was flowing this tick dissipated into the failure; nothing reaches the loads.
                _powerRatio = 0;
                _isPowerMet = false;
            }

            int idx = Devices.Count;
            while (idx-- > 0)
            {
                var currentDevice = Devices[idx];
                if (currentDevice == null)
                    continue;

                if (_powerData[idx].PowerUsed >= 0.0f)
                {
                    float powerAvailable = _powerData[idx].PowerUsed * _powerRatio;

                    currentDevice.ReceivePower(CableNetwork, powerAvailable);

                    if (powerAvailable > 0.0f
                        && (_isPowerMet || (currentDevice.IsPowerProvider && _powerData[idx].PowerProvided > 0.0f)))
                    {
                        if (!currentDevice.Powered)
                            currentDevice.SetPowerFromThread(CableNetwork, true).Forget();
                    }
                    else if (currentDevice.Powered && currentDevice.AllowSetPower(CableNetwork))
                    {
                        currentDevice.SetPowerFromThread(CableNetwork, false).Forget();
                    }
                }

                if (_powerData[idx].PowerProvided >= 0.0f)
                    currentDevice.UsePower(CableNetwork, demandRatio * _powerData[idx].PowerProvided);
            }
        }
    }
}
