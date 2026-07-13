using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Pipes;

namespace PowerGridPlus
{
    /// <summary>
    ///     One-shot census of third-party ReceivePower overrides (the delivery-shim compatibility
    ///     lane). The atomic write-back retired vanilla ConsumePower, so a modded device whose
    ///     gameplay effect runs inside <c>ReceivePower(CableNetwork, float)</c> silently loses
    ///     that effect unless the delivery shim covers it (Core/WriteBack +
    ///     DeliveryEffectClassifier). This census makes the gap visible instead of silent: it
    ///     walks every loaded assembly for Device subclasses whose two-arg ReceivePower is
    ///     declared outside the vanilla assembly and names each declaring type once, pointing the
    ///     operator at the Extra Delivery Devices setting. Types the built-in shim classes already
    ///     cover (subclasses of the five) are not named: they inherit shim membership through the
    ///     classifier's type checks. It also names the built-in five, so one load's log carries
    ///     the complete delivery picture.
    ///
    ///     <para>Lifecycle and threading mirror <see cref="UnknownBridgeCensus"/>: armed at plugin
    ///     load (covers the session's first world), re-armed by FaultRegistryLoadPatches on every
    ///     world load, run once on the power worker in the atomic tick's housekeeping step.
    ///     Reflection-only, one pass per load; partially loadable assemblies are censused through
    ///     ReflectionTypeLoadException.Types rather than skipped wholesale.</para>
    /// </summary>
    internal static class ReceivePowerOverrideCensus
    {
        private static readonly Type[] ReceivePowerSignature = { typeof(CableNetwork), typeof(float) };

        private static bool _pending = true;

        /// <summary>Arm the census to run on the next atomic tick (world-load hook).</summary>
        internal static void Arm() => _pending = true;

        /// <summary>Run the census once if armed; otherwise a single flag check.</summary>
        internal static void RunIfPending()
        {
            if (!_pending) return;
            _pending = false;
            try
            {
                Run();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    $"ReceivePower override census failed (diagnostic only, sim unaffected): {e.Message}");
            }
        }

        private static void Run()
        {
            // Self-describing baseline: the shim's built-in set, so the census log reads whole.
            Plugin.Log?.LogInfo(
                "[PowerGridPlus] Delivery shim built-in classes: PowerTransmitterOmni, SuitStorage, "
                + "BatteryCellCharger, Bench, WallLightBattery (subclasses included; extend by prefab "
                + "name via the Extra Delivery Devices setting).");

            var vanillaAssembly = typeof(Device).Assembly;
            var declarers = new HashSet<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                // A type inside the vanilla assembly can only declare ReceivePower in the vanilla
                // assembly (its whole hierarchy is vanilla), so the game assembly is skipped as
                // the known set.
                if (assembly == vanillaAssembly) continue;
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types;   // partially loadable: census whatever resolved
                }
                catch
                {
                    continue;          // unreflectable assembly (dynamic, mis-targeted): skip
                }
                if (types == null) continue;
                foreach (var type in types)
                {
                    try
                    {
                        if (type == null || !typeof(Device).IsAssignableFrom(type)) continue;
                        var method = type.GetMethod("ReceivePower",
                            BindingFlags.Instance | BindingFlags.Public, null, ReceivePowerSignature, null);
                        var declarer = method?.DeclaringType;
                        if (declarer == null || declarer.Assembly == vanillaAssembly) continue;
                        if (DeliveryEffectClassifier.IsBuiltInDeliveryType(declarer)) continue;
                        declarers.Add(declarer);
                    }
                    catch
                    {
                        // A type whose member resolution throws is skipped; this pass is census only.
                    }
                }
            }
            if (declarers.Count == 0) return;

            var sorted = new List<Type>(declarers);
            sorted.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            foreach (var type in sorted)
            {
                Plugin.Log?.LogInfo(
                    $"[PowerGridPlus] Third-party ReceivePower override: {type.FullName} "
                    + $"({type.Assembly.GetName().Name}). Not in the delivery shim; if this device "
                    + "charges or forwards power, add its prefab name to Extra Delivery Devices.");
            }
        }
    }
}
