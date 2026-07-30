using System.Collections.Concurrent;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Util;
using StationeersPlus.Shared;

namespace PowerGridPlus
{
    /// <summary>
    ///     Sanitizes non-finite (NaN / Infinity) power values reported by devices, so one broken device
    ///     cannot poison a whole network's sums and cascade into the allocator's grants or stored
    ///     battery charge (POWER.md §P3). <see cref="Sanitize"/> is called from two places:
    ///
    ///     <list type="bullet">
    ///       <item>From the <c>GetGeneratedPower</c> / <c>GetUsedPower</c> postfixes on the device
    ///       classes PowerGridPlus already patches (battery, APC, umbilical, producers). Clamping a
    ///       non-finite return to 0 AT THE SOURCE keeps the value clean for every reader, including
    ///       main-thread callers outside the tick.</item>
    ///       <item>From the snapshot boundary read (<c>Core/GridSnapshot</c>), which samples every
    ///       power device's demand and output once per tick before the allocator consumes them. This
    ///       covers devices PowerGridPlus does NOT patch (an unknown or modded class), which is the
    ///       case a player most needs to hear about.</item>
    ///     </list>
    ///
    ///     <para>Reporting: every occurrence goes to the BepInEx file log (developer detail). The
    ///     in-game console (player-visible) is named ONCE PER DEVICE per world session -- a device that
    ///     breaks every tick would otherwise flood the console unusably. The console write is marshalled
    ///     to the main thread and then handed to the shared <see cref="PlayerMessage"/> helper, which
    ///     drops the console leg outright when it is called from a worker; the boundary read runs on
    ///     the power worker, so without the marshal the player would never be told at all. Host-only
    ///     (the tick runs on the simulating peer); cleared on world load.</para>
    /// </summary>
    internal static class DeviceOutputSanitizer
    {
        // ReferenceIds already handed to the main-thread queue this session. This is NOT the print
        // gate any more (PlayerMessage's Throttle.Once, keyed on the same ReferenceId, is); it is the
        // ENQUEUE gate, and the helper cannot replace it: the throttle is only consulted once the
        // marshalled action runs on the main thread, so without this set a permanently broken device
        // would build a discarded message string and allocate a discarded closure on every boundary
        // read, forever, on the power tick's hot path. Concurrent: written from the power worker.
        // Cleared on world load, in the same drain that calls PlayerMessage.ResetSession.
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
        ///     Does not clamp; split from <see cref="Sanitize"/> so a caller that already replaced the
        ///     value can still name the culprit.
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

            // No "[Power Grid Plus] " here: PlayerMessage prepends the display-name prefix itself, so
            // the rendered line is byte-for-byte what it always was.
            if (refId != 0L && _consoleNamed.TryAdd(refId, 0))
                EnqueueConsole(refId,
                    $"Broken device: \"{name}\" (ref {refId}) reported an invalid power " +
                    $"value ({value}) via {method} and is being treated as 0 W. A mod is likely shipping a " +
                    "device with broken power math; check your mod list.");
        }

        private static void EnqueueConsole(long refId, string message)
        {
            try
            {
                if (!UnityMainThreadDispatcher.Exists()) return;   // no UI (e.g. mid-teardown): file log still recorded it
                // The marshal stays. PlayerMessage.Error suppresses its console leg on a worker thread
                // rather than racing the renderer, and Report's dominant caller is the snapshot
                // boundary read, which IS the power worker: calling straight through would keep the
                // log lines but silently delete the player-visible half of this class. Marshalling
                // does not move the log line either way -- the every-occurrence LogError above already
                // ran synchronously on the reporting thread, exactly as before.
                //
                // Throttle.Once keyed on the ReferenceId is the print gate, replacing the dedupe the
                // local set used to own: one line per broken device per world, cleared by
                // PlayerMessage.ResetSession at the same load boundary that clears the set.
                // The error level gives the same red, stack-trace-suppressed PrintError as before.
                UnityMainThreadDispatcher.Instance().Enqueue(
                    () => PlayerMessage.Error("broken-device-" + refId, Throttle.Once, message));
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
