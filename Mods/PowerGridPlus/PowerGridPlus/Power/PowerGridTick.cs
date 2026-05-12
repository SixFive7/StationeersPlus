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

        // NEW-3: true iff the network contains at least one cable and every cable in it is heavy.
        // Used to decide whether generators on this network are allowed to produce power.
        private bool _hasAnyCable;
        private bool _allHeavy;
        // NEW-1: true iff the weakest cable in this network is super-heavy (so the whole network is super-heavy).
        private bool _weakestCableIsSuperHeavy;
        // NEW-3: a mixed-tier network has been detected; we have asked for a cable burn to split it. Guards
        // against requesting another burn before the first one takes effect (the split replaces this tick).
        private bool _tierResolutionPending;

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

            _hasAnyCable = false;
            _allHeavy = true;
            bool mixedTier = false;
            Cable.Type? firstCableType = null;
            lock (CableNetwork.CableList)
            {
                foreach (var cable in CableNetwork.CableList.Where(x => x != null))
                {
                    if (!_allCables.ContainsKey(cable.MaxVoltage))
                        _allCables.Add(cable.MaxVoltage, new List<Cable>(CableNetwork.CableList.Count) { cable });
                    else
                        _allCables[cable.MaxVoltage].Add(cable);

                    _hasAnyCable = true;
                    if (cable.CableType != Cable.Type.heavy)
                        _allHeavy = false;

                    if (firstCableType == null)
                        firstCableType = cable.CableType;
                    else if (cable.CableType != firstCableType.Value)
                        mixedTier = true;
                }
            }

            if (!_hasAnyCable)
                _allHeavy = false;

            _weakestCableIsSuperHeavy = _allCables.Count > 0
                                        && _allCables.Values[0].Count > 0
                                        && _allCables.Values[0][0].CableType == Cable.Type.superHeavy;

            if (_powerData == null || _powerData.Length != Devices.Count)
                _powerData = Devices.Select(x => new PowerUsage { Device = x }).ToArray();

            // NEW-3 backstop: a network that ended up holding more than one cable tier (an old save with a
            // pre-existing illegal junction, or anything the build-time check missed). Burn one lowest-tier
            // cable; the re-flood splits the network and the next rebuild re-checks until it is single-tier.
            if (mixedTier && Settings.EnableVoltageTiers.Value && !_tierResolutionPending)
            {
                _tierResolutionPending = true;
                VoltageTier.ResolveMixedTierNetwork(CableNetwork);
            }
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
            int idx = Devices.Count;
            while (idx-- > 0)
            {
                var currentDevice = Devices[idx];
                if (currentDevice == null)
                    continue;

                _powerData[idx].PowerUsed = SanitizePower(currentDevice.GetUsedPower(CableNetwork));
                _powerData[idx].PowerProvided = SanitizePower(currentDevice.GetGeneratedPower(CableNetwork));

                // NEW-3: a generator only contributes power if every cable on its network is heavy.
                if (_powerData[idx].PowerProvided != 0.0f
                    && Settings.EnableVoltageTiers.Value
                    && Settings.EnableGeneratorHeavyCableRequirement.Value
                    && !_allHeavy
                    && VoltageTier.IsGenerator(currentDevice))
                {
                    _powerData[idx].PowerProvided = 0.0f;
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
                burnFuse.Break();
            else if (burnCable != null)
                burnCable.Break();
            else
                power = true;

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
