using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Mono.Cecil;

namespace ForceFieldDoorModFix
{
    // The fix for ForceFieldDoorMod (Steam Workshop 3328065049).
    //
    // The mod's forcefielddoormod.ForceFieldDoor overrides OnAtmosphericTick and calls the
    // one-argument GridController.CanContainAtmos(WorldGrid) at two sites. Stationeers 0.2.6403
    // removed that overload (only CanContainAtmos(WorldGrid, bool allowCrewModules = true) remains),
    // so Mono throws MissingMethodException the moment it JIT-compiles the override, which happens on
    // the first atmospheric tick of any world containing a force field door. The exception is
    // attributed to the vanilla caller frame and takes the whole GameTick simulation section down
    // every tick.
    //
    // The mod is loaded through StationeersLaunchPad, so by the time this plugin's entrypoint runs the
    // broken method is already in a loaded assembly. It cannot be rewritten (the CLR has it) and it
    // cannot be Harmony-patched (HarmonyX pins/JIT-compiles the target, which throws the same
    // exception). What we CAN do is make sure the broken method is never called, so it never JITs:
    // the game dispatches OnAtmosphericTick per-thing through a single delegate
    // (AtmosphericsManager.ThingAtmosphereTickAction). We prefix that delegate, and for a ForceFieldDoor
    // we run a faithful reimplementation (using the surviving two-argument CanContainAtmos) and skip the
    // vanilla call. Every method we patch is vanilla game code, so nothing we touch trips the stale
    // reference.
    //
    // Idempotent and self-retiring: before doing anything it scans the mod's OnAtmosphericTick IL (via
    // Cecil, off the on-disk DLL, without compiling it). If the stale one-argument call is gone (the
    // author updated the mod) or the mod is absent, this shim stands down and changes nothing.
    internal static class ForceFieldDoorPatch
    {
        private const string DoorTypeName = "forcefielddoormod.ForceFieldDoor";

        // ForceFieldDoor.OnAtmosphericTick tuning constants, verified from the shipped 0.2.4767 build
        // decompile. They are compile-time-fixed in the mod, so a version that changed them would also
        // have to recompile (which drops the stale reference and trips the stand-down path above).
        private const float PowerUsageBase = 100f;
        private const float PowerUsageMax = 100000f;
        private const float PowerUsageRate = 10f;
        private const bool Fluctuates = true;

        private static Harmony _harmony;
        private static Type _doorType;
        private static FieldInfo _openField;
        private static FieldInfo _facingField;
        private static FieldInfo _rearField;
        private static MethodInfo _isBeingDestroyedGetter;
        private static Action<Device> _deviceBaseTick;
        private static bool _setupDone;
        private static readonly object _setupLock = new object();

        public static void Install(Harmony harmony)
        {
            _harmony = harmony;

            // Normal path: loaded through StationeersLaunchPad after every mod assembly is already in
            // the AppDomain, so ForceFieldDoorMod (if present) can be resolved right now.
            if (TrySetup())
                return;

            // Fallback: this plugin ran before ForceFieldDoorMod was loaded (e.g. dropped straight into
            // BepInEx/plugins and started by the chainloader). Defer the one-time setup to the first
            // atmospheric tick, by which point every mod is loaded and the world has not ticked yet.
            var tick = AccessTools.Method(typeof(AtmosphericsManager), "ThingAtmosphereTick");
            if (tick == null)
            {
                Plugin.Log.LogError(
                    "Force Field Door Mod Fix: could not find AtmosphericsManager.ThingAtmosphereTick; " +
                    "the fix cannot install. ForceFieldDoorMod will crash the simulation if present.");
                return;
            }
            _harmony.Patch(tick, prefix: new HarmonyMethod(typeof(ForceFieldDoorPatch), nameof(DeferredSetupPrefix)));
        }

        // Runs once, before the first atmospheric ForEach, for the deferred (early-load) case only.
        private static void DeferredSetupPrefix()
        {
            if (_setupDone)
                return;
            TrySetup();
        }

        // Returns true when ForceFieldDoorMod was found (whether or not it needed patching), so the
        // caller knows the decision has been made and no deferral is required.
        private static bool TrySetup()
        {
            lock (_setupLock)
            {
                if (_setupDone)
                    return true;

                var doorType = FindType(DoorTypeName);
                if (doorType == null)
                    return false; // mod not loaded (yet)

                _setupDone = true;

                try
                {
                    if (!IsOnAtmosphericTickBroken(doorType))
                    {
                        Plugin.Log.LogInfo(
                            "Force Field Door Mod Fix: ForceFieldDoorMod has no stale " +
                            "GridController.CanContainAtmos(WorldGrid) reference (already updated). " +
                            "Standing down; no changes made.");
                        return true;
                    }

                    _doorType = doorType;
                    _openField = AccessTools.Field(doorType, "_open");
                    _facingField = AccessTools.Field(doorType, "_facingGrid");
                    _rearField = AccessTools.Field(doorType, "_rearGrid");
                    if (_openField == null || _facingField == null || _rearField == null)
                        throw new Exception(
                            "missing expected private field(s) on " + DoorTypeName +
                            " (_open / _facingGrid / _rearGrid)");

                    _isBeingDestroyedGetter = AccessTools.PropertyGetter(typeof(Thing), "IsBeingDestroyed");
                    _deviceBaseTick = BuildDeviceBaseTick();

                    // Intercept the per-thing atmospheric dispatch delegate. Resolve the compiler-generated
                    // target through the named field so we never hardcode a mangled lambda name.
                    var field = AccessTools.Field(typeof(AtmosphericsManager), "ThingAtmosphereTickAction");
                    var del = field?.GetValue(null) as Delegate;
                    if (del == null)
                        throw new Exception("could not resolve AtmosphericsManager.ThingAtmosphereTickAction delegate");

                    _harmony.Patch(del.Method, prefix: new HarmonyMethod(typeof(ForceFieldDoorPatch), nameof(DispatchPrefix)));

                    Plugin.Log.LogInfo(
                        "Force Field Door Mod Fix active: intercepting ForceFieldDoor atmospheric ticks in " +
                        doorType.Assembly.GetName().Name +
                        " (2 stale GridController.CanContainAtmos(WorldGrid) call sites bypassed by reimplementation).");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError(
                        "Force Field Door Mod Fix FAILED to apply; ForceFieldDoorMod will crash the " +
                        "simulation on any world containing a force field door. Reason: " + e);
                }

                return true;
            }
        }

        // Harmony prefix on the per-thing atmospheric dispatch delegate: void (Thing thing).
        private static bool DispatchPrefix(Thing __0)
        {
            var thing = __0;
            if (thing == null || thing.GetType() != _doorType)
                return true; // not our door; let the vanilla dispatch run unchanged.

            bool destroyed = _isBeingDestroyedGetter != null && (bool)_isBeingDestroyedGetter.Invoke(thing, null);
            if (!destroyed)
            {
                try
                {
                    TickForceFieldDoor(thing);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Force Field Door Mod Fix: error during reimplemented atmospheric tick: " + e);
                }
            }

            return false; // skip the vanilla thing.OnAtmosphericTick() so the broken override never JITs.
        }

        // Faithful reimplementation of forcefielddoormod.ForceFieldDoor.OnAtmosphericTick, with the two
        // removed one-argument CanContainAtmos(WorldGrid) calls replaced by the surviving two-argument
        // CanContainAtmos(WorldGrid, allowCrewModules: true) overload (its default matches the old call).
        private static void TickForceFieldDoor(Thing door)
        {
            _deviceBaseTick((Device)door); // base Device.OnAtmosphericTick (non-virtual)

            var device = (Device)door;
            bool open = (bool)_openField.GetValue(door);
            if (open)
            {
                device.UsedPower = 10f;
                return;
            }

            float diff = 0f;
            var grid = door.GridController;
            var facing = (WorldGrid)_facingField.GetValue(door);
            var rear = (WorldGrid)_rearField.GetValue(door);
            if (grid.CanContainAtmos(facing, true) && grid.CanContainAtmos(rear, true))
            {
                var atmos = grid.AtmosphericsController;
                var front = atmos.SampleGlobalAtmosphere(facing);
                var back = atmos.SampleGlobalAtmosphere(rear);
                float frontKpa = front.PressureGassesAndLiquidsInPa / 1000f;
                float backKpa = back.PressureGassesAndLiquidsInPa / 1000f;
                diff = Mathf.Abs(frontKpa - backKpa);
            }
            diff = Mathf.Floor(diff);

            if (Fluctuates && diff >= 5f)
            {
                float bump = diff * 0.2f;
                bump = Mathf.Floor((float)AtmosphereHelper._random.NextDouble() * bump);
                bump = Mathf.Min(bump, 5f);
                diff += bump;
            }

            float power = PowerUsageBase + diff * PowerUsageRate;
            power = Mathf.Max(power, PowerUsageBase);
            power = Mathf.Min(power, PowerUsageMax);
            device.UsedPower = power;
        }

        // Builds a delegate that calls Device.OnAtmosphericTick NON-virtually on a given instance, so it
        // runs the base Device body instead of dispatching back into the broken override.
        private static Action<Device> BuildDeviceBaseTick()
        {
            var mi = AccessTools.Method(typeof(Device), "OnAtmosphericTick", Type.EmptyTypes);
            if (mi == null)
                throw new Exception("could not find Device.OnAtmosphericTick");
            var dm = new DynamicMethod("FFDMF_DeviceBaseTick", null, new[] { typeof(Device) }, typeof(Device), true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, mi); // non-virtual call to Device.OnAtmosphericTick
            il.Emit(OpCodes.Ret);
            return (Action<Device>)dm.CreateDelegate(typeof(Action<Device>));
        }

        // Reads the mod's OnAtmosphericTick IL via Cecil (off the on-disk DLL, WITHOUT compiling it) and
        // reports whether it still calls the removed one-argument GridController.CanContainAtmos(WorldGrid).
        private static bool IsOnAtmosphericTickBroken(Type doorType)
        {
            string path = doorType.Assembly.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Plugin.Log.LogWarning(
                    "Force Field Door Mod Fix: could not locate ForceFieldDoorMod on disk to verify the " +
                    "stale reference; applying the fix defensively.");
                return true;
            }

            byte[] bytes = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(bytes))
            using (var module = ModuleDefinition.ReadModule(ms, new ReaderParameters(ReadingMode.Immediate)))
            {
                var td = module.GetType(DoorTypeName);
                var md = td?.Methods.FirstOrDefault(m =>
                    m.Name == "OnAtmosphericTick" && m.Parameters.Count == 0 && m.HasBody);
                if (md == null)
                    return false; // method shape changed; do not intercept.

                foreach (var ins in md.Body.Instructions)
                {
                    if ((ins.OpCode.Code == Mono.Cecil.Cil.Code.Call || ins.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt)
                        && ins.Operand is MethodReference mr
                        && mr.Name == "CanContainAtmos"
                        && mr.Parameters.Count == 1
                        && mr.DeclaringType != null
                        && mr.DeclaringType.Name == "GridController")
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName, false);
                    if (t != null)
                        return t;
                }
                catch
                {
                    // Some dynamic assemblies throw on GetType; ignore and keep scanning.
                }
            }
            return null;
        }
    }
}
