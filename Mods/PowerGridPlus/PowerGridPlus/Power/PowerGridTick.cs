// Portions of this file are derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt),
// used under the MIT License (Copyright (c) 2025 Sukasa). See the repository NOTICE file.
//
// The simulation rewrite below (proportional load sharing, sliding-window probabilistic cable
// burnout, NaN-power guards, provider bookkeeping) follows Re-Volt's RevoltTick. Power Grid Plus
// removes Re-Volt's circuit-breaker / load-center machinery and adds:
//   * a super-heavy cable carve-out (super-heavy cables never burn), and
//   * a hook into the three-tier voltage gating that suppresses generator output on a non-heavy
//     network.

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

        // Voltage tiers: the cable tier of this network (it is single-tier once the burn-on-join
        // backstop has run; during the brief mixed-tier window this is whichever tier was seen
        // first). Null if cableless.
        private Cable.Type? _networkTier;
        // Super-heavy carve-out: true iff the weakest cable in this network is super-heavy (so the
        // whole network is super-heavy).
        private bool _weakestCableIsSuperHeavy;
        // Voltage tiers: a mixed-tier network has been detected; we have asked for a cable burn to
        // split it. Guards against requesting another burn before the first one takes effect (the
        // split replaces this tick).
        private bool _tierResolutionPending;
        // Voltage tiers: cached during Initialize_New (cheap, only on dirty rebuild). The actual
        // burn fires from ApplyState_New gated on the network having real power flow (_actual > 0).
        private bool _mixedTierDetected;
        // Voltage tiers: recorded during CalculateState_New (once per tick). Reset to null at the
        // top of each tick. First device this tick that's on a cable tier it isn't allowed on;
        // ApplyState_New burns the cable adjacent to it (if there's power flow). The device itself
        // is never destroyed.
        private Device _misplacedDeviceForBurn;
        // Voltage tiers (per-port, APC): the lower-tier port's adjacent cable on an APC whose two
        // sides are on different tiers. Single-cable burn, gated on local network powerFlow > 0.
        // _portMismatchOwner is the APC, only used for the log line and the tooltip reason.
        private Cable _portMismatchCableForBurn;
        private Device _portMismatchOwner;

        // Voltage tiers (per-port, transformer): a transformer whose two cable ports violate its
        // variant's unordered tier-pair rule AND that is actively bridging power (Transformer.OnOff
        // and _powerProvided > 0). When recorded, ApplyState_New burns BOTH adjacent cables (input
        // AND output) -- the transformer itself is never destroyed. An off / half-powered
        // transformer sits harmlessly even with a violation.
        private Transformer _portMismatchTransformerForBurn;

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

            // Voltage tiers: a network that ended up holding more than one cable tier (an old save
            // with a pre-existing illegal junction, or anything the build-time check missed).
            // Record the fact here (cheap, only on dirty rebuild); ApplyState_New fires the actual
            // burn IF the network has real power flow, so an idle / off network never destroys
            // cables.
            _mixedTierDetected = mixedTier;
        }

        public Cable TestBurnCable(float powerUsed, float slidingWindow)
        {
            // Power transmitters create a "cable network" with no cables.
            if (_allCables.Keys.Count < 1)
                return null;

            // Super-heavy carve-out: a network whose weakest cable is super-heavy never burns
            // (it's the long-haul backbone).
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
            // Voltage tiers: record at most one misplaced device + at most one port-mismatched
            // cable per tick; ApplyState_New uses both AFTER the power-flow gate.
            _misplacedDeviceForBurn = null;
            _portMismatchCableForBurn = null;
            _portMismatchOwner = null;
            _portMismatchTransformerForBurn = null;
            int idx = Devices.Count;
            while (idx-- > 0)
            {
                var currentDevice = Devices[idx];
                if (currentDevice == null)
                    continue;

                _powerData[idx].PowerUsed = SanitizePower(currentDevice.GetUsedPower(CableNetwork));
                _powerData[idx].PowerProvided = SanitizePower(currentDevice.GetGeneratedPower(CableNetwork));

                // Voltage tiers: don't suppress power here. Just record the first device that's on
                // a tier it isn't allowed on; ApplyState_New will burn its adjacent cable IF actual
                // power is flowing.
                if (Settings.EnableVoltageTiers.Value
                    && _networkTier.HasValue
                    && _misplacedDeviceForBurn == null
                    && !VoltageTier.IsAllowedOnTier(currentDevice, _networkTier.Value))
                {
                    _misplacedDeviceForBurn = currentDevice;
                }

                // Voltage tiers (per-port, transformer): a transformer whose two cable ports violate
                // the variant's unordered tier-pair rule fires the burn-both-cables rule ONLY when
                // the transformer is turned on AND actively bridging power (_powerProvided > 0 from
                // the previous tick). An off or half-powered transformer with a violation waits.
                if (Settings.EnableVoltageTiers.Value
                    && _portMismatchTransformerForBurn == null
                    && currentDevice is Transformer transformer
                    && VoltageTier.IsTransformerTierViolated(transformer)
                    && VoltageTier.IsTransformerActivelyConducting(transformer))
                {
                    _portMismatchTransformerForBurn = transformer;
                }

                // Voltage tiers (per-port, APC): an APC with mismatched-tier ports fires the
                // existing single-cable burn whenever the relevant network has power flow. No
                // "actively bridging" gate.
                if (Settings.EnableVoltageTiers.Value
                    && _portMismatchCableForBurn == null
                    && currentDevice is AreaPowerControl apc)
                {
                    var mismatch = VoltageTier.FindMismatchedApcCable(apc);
                    if (mismatch != null)
                    {
                        _portMismatchCableForBurn = mismatch;
                        _portMismatchOwner = apc;
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

                // Voltage tiers: tier burns are gated on real power flow this tick. An idle / off
                // network never destroys cables. Priority order: mixed-tier-in-this-network (root
                // cause) -> actively-bridging transformer with a violated pair (burn BOTH adjacent
                // cables) -> APC with mismatched sides (single cable) -> misplaced single-port
                // device (single cable). The transformer rule's "actively bridging" gate implies
                // powerFlow > 0 on the local network anyway. Resolving the higher-priority case
                // usually fixes the lower-priority symptom on the next tick's fresh network.
                if (powerFlow > 0f && Settings.EnableVoltageTiers.Value && !_tierResolutionPending)
                {
                    if (_mixedTierDetected)
                    {
                        _tierResolutionPending = true;
                        VoltageTier.ResolveMixedTierNetwork(CableNetwork);
                    }
                    else if (_portMismatchTransformerForBurn != null)
                    {
                        _tierResolutionPending = true;
                        VoltageTier.BurnTransformerBothCables(_portMismatchTransformerForBurn);
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
