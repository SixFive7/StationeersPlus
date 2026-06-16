using System.Collections.Concurrent;
using Assets.Scripts;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;

namespace PowerGridPlus
{
    /// <summary>
    ///     Sanitizes non-finite (NaN / Infinity) power values reported by devices, so one broken device
    ///     cannot poison a whole network's sums and cascade into <c>_powerRatio</c> / stored battery
    ///     charge (POWER.md §P3). Two entry points:
    ///
    ///     <list type="bullet">
    ///       <item><see cref="Sanitize"/> -- called from the <c>GetGeneratedPower</c> / <c>GetUsedPower</c>
    ///       postfixes on the device classes PowerGridPlus already patches (battery, APC, umbilical,
    ///       producers). It clamps a non-finite return to 0 AT THE SOURCE, so the value is clean for
    ///       every reader (vanilla CalculateState, the §5.7 average, the allocator) and the network never
    ///       darkens.</item>
    ///       <item><see cref="Report"/> -- called from the per-network backstop
    ///       (<c>PowerTickPatches.CalculateState_NanGuard</c>) when a poisoned sum is detected: it scans
    ///       the network for the culprit and names it. This covers devices PowerGridPlus does NOT patch
    ///       (an unknown or modded class), which is the case a player most needs to hear about.</item>
    ///     </list>
    ///
    ///     <para>Reporting: every occurrence goes to the BepInEx file log (developer detail). The
    ///     in-game console (player-visible) is named ONCE PER DEVICE per world session -- a device that
    ///     breaks every tick would otherwise flood the console unusably. The console write is marshalled
    ///     to the main thread (<see cref="ConsoleWindow"/> is UI; the power tick runs on the worker).
    ///     Host-only (the tick runs on the simulating peer); cleared on world load.</para>
    /// </summary>
    internal static class DeviceOutputSanitizer
    {
        // ReferenceIds already named in the in-game console this session (each broken device once, not
        // every tick). Concurrent: written from the power worker. Cleared on world load.
        private static readonly ConcurrentDictionary<long, byte> _consoleNamed =
            new ConcurrentDictionary<long, byte>();

        /// <summary>
        ///     Clamp a non-finite device power value to 0 and report the broken device. Returns the
        ///     value unchanged when finite (the common path). Called from device-method postfixes.
        /// </summary>
        internal static float Sanitize(float value, Device device, bool generated)
        {
            if (!float.IsNaN(value) && !float.IsInfinity(value)) return value;
            Report(device, generated, value);
            return 0f;
        }

        /// <summary>
        ///     Report a broken device (file log every time; in-game console once per device per session).
        ///     Does not clamp -- used by the per-network backstop where the value is already in a poisoned
        ///     sum and the goal is only to name the culprit.
        /// </summary>
        internal static void Report(Device device, bool generated, float value)
        {
            long refId = device?.ReferenceId ?? 0L;
            string name = device == null
                ? "<null>"
                : (string.IsNullOrEmpty(device.DisplayName) ? device.PrefabName : device.DisplayName);
            string method = generated ? "GetGeneratedPower" : "GetUsedPower";

            Plugin.Log?.LogError(
                $"Non-finite power value ({value}) from {name} (ref {refId}) via {method}; treated as 0 W. " +
                "A mod is likely shipping a device with broken power math.");

            if (refId != 0L && _consoleNamed.TryAdd(refId, 0))
                EnqueueConsole(
                    $"[Power Grid Plus] Broken device: \"{name}\" (ref {refId}) reported an invalid power " +
                    $"value ({value}) via {method} and is being treated as 0 W. A mod is likely shipping a " +
                    "device with broken power math; check your mod list.");
        }

        private static void EnqueueConsole(string message)
        {
            try
            {
                if (!UnityMainThreadDispatcher.Exists()) return;   // no UI (e.g. mid-teardown): file log still recorded it
                UnityMainThreadDispatcher.Instance().Enqueue(() => ConsoleWindow.PrintError(message));
            }
            catch
            {
                // Dispatcher unavailable; the BepInEx file log already captured the occurrence.
            }
        }

        /// <summary>Clear the per-device console-named set. Called on world load.</summary>
        internal static void ClearReported() => _consoleNamed.Clear();
    }
}
