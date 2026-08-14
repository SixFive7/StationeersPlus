using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ConsoleWindow = Assets.Scripts.ConsoleWindow;

namespace TestRig
{
    /// <summary>
    ///     Console routes: the tee, the game's own ring, and command submission.
    /// </summary>
    internal static partial class Router
    {
        private static HttpResponse ConsoleLog(IDictionary body)
        {
            long since = Json.GetLong(body, "since", 0);
            int limit = Json.GetInt(body, "limit", 200);
            string contains = Json.GetStr(body, "contains");
            string source = Json.GetStr(body, "source");
            var lines = ConsoleTap.Snapshot(since, limit, contains, source);
            return HttpResponse.Json(ConsoleTap.ToJson(lines, ConsoleTap.NextSeq));
        }

        private static HttpResponse ConsolePrint(IDictionary body)
        {
            string text = Json.GetStr(body, "text", "");
            string level = (Json.GetStr(body, "level", "action") ?? "action").ToLowerInvariant();
            return Main(() =>
            {
                switch (level)
                {
                    case "error": ConsoleWindow.PrintError(text, true); break;
                    case "info": ConsoleWindow.Print(text); break;
                    default: ConsoleWindow.PrintAction(text); break;
                }
                return OkJson();
            });
        }

        /// <summary>
        ///     Submits a line to the in-game console and returns every console line the command
        ///     produced. <c>ConsoleWindow.Submit</c> prints the echo then hands off to
        ///     <c>CommandLine.Process</c>, so capturing from the sequence number taken before the
        ///     call gets the echo plus all output.
        /// </summary>
        private static HttpResponse ConsoleExec(IDictionary body)
        {
            string command = Json.GetStr(body, "command");
            if (string.IsNullOrEmpty(command)) return HttpResponse.Error("missing 'command'", 400);
            int waitFrames = Json.GetInt(body, "waitFrames", 2);
            int waitMs = Json.GetInt(body, "waitMs", 0);

            long before = ConsoleTap.NextSeq;
            int endFrame = 0;
            var scheduled = Main(() =>
            {
                ConsoleWindow.Submit(command);
                endFrame = Time.frameCount + Math.Max(0, waitFrames);
                return OkJson();
            });
            if (scheduled.Status != 200) return scheduled;

            if (waitFrames > 0) MainThreadPump.WaitForFrame(endFrame + 1, 10000);
            if (waitMs > 0) System.Threading.Thread.Sleep(waitMs);

            var lines = ConsoleTap.Snapshot(before, 500, null, null);
            var payload = ConsoleTap.ToJson(lines, ConsoleTap.NextSeq);
            // Splice the command in after the payload's own leading {"ok":true,
            const string head = "{\"ok\":true,";
            var spliced = payload.StartsWith(head, StringComparison.Ordinal)
                ? head + "\"command\":" + Json.Escape(command) + "," + payload.Substring(head.Length)
                : payload;
            return HttpResponse.Json(spliced);
        }

        private static string ConsoleCommands(string contains)
        {
            var names = new List<string>();
            try
            {
                var mapProp = AccessTools.Property(typeof(global::Util.Commands.CommandLine), "CommandsMap");
                var map = mapProp?.GetValue(null, null) as IEnumerable;
                if (map != null)
                {
                    foreach (var entry in map)
                    {
                        var keyProp = entry.GetType().GetProperty("Key");
                        var valProp = entry.GetType().GetProperty("Value");
                        var key = keyProp?.GetValue(entry, null) as string;
                        var val = valProp?.GetValue(entry, null);
                        if (key == null) continue;
                        if (!string.IsNullOrEmpty(contains) &&
                            key.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        names.Add(key + " (" + (val == null ? "?" : val.GetType().Name) + ")");
                    }
                }
            }
            catch (Exception ex)
            {
                return new Json.Obj().Bit("ok", false).Str("error", ex.Message).ToString();
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return new Json.Obj().Bit("ok", true).Int("count", names.Count)
                .StrArray("commands", names).ToString();
        }
    }
}
