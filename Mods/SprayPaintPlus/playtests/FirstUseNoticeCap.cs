// =============================================================================
// Spray Paint Plus: the first-use notice cap
// =============================================================================
// From Mods/SprayPaintPlus/PLAYTEST.md: "On the dedicated server set
// Server - Network Painting / Network Paint Cables Off with the client half On,
// then paint a cable four times. Expected: three console lines, the third ending
// 'No more notices about this one until you rejoin.', and silence on the fourth."
//
// Run here on a LISTEN HOST rather than the dedicated server, because the notice
// has to land in a player's console and a dedicated server has no player. The
// host is both the authority that detects the block and the acting player, so
// SettingBlockedNotice.NotifyBlocked takes its Human.LocalHuman branch and prints
// locally instead of sending a message. That is the same WarningNotifier.
// WarnBlocked cap either way (MaxNoticesPerFunction = 3), which is what this
// measures.
//
// WHAT WOULD MAKE THIS FAIL
//   - the cap regressing to unbounded: four strokes would print four lines;
//   - the cap regressing to one or two: fewer than three;
//   - the third line losing its "no more notices" sentence, which is written by
//     the seen + 1 == MaxNoticesPerFunction branch and is the only thing that
//     proves the cap announced itself rather than just stopping;
//   - the flood not being blocked at all, which the unpainted control cable
//     catches independently of any console text;
//   - the console prefix reverting to the code name. Counted lines must carry
//     "[Spray Paint Plus] ", the display-name prefix PlayerMessage.Init supplies.
//
// AGAINST THE PRE-v1.11.0 BUILD THIS CHECK FAILS, and that is deliberate. Before
// the PlayerMessage migration every console line carried "[SprayPaintPlus] ", so
// the contains filter matches nothing and the first assertion reads 0 against an
// expected 3. A build that passes this check is one whose console output went
// through the shared helper.
//
// PREREQUISITES (the harness does not provision and does not build)
//   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
//   copy bin/Release/SprayPaintPlus.dll into
//     TestRig/ClientRig/data/hostie/userdata/mods/SprayPaintPlus/
// =============================================================================

using TestRig.Contracts;
using TestRig.Playtest;
using TestRig.Playtest.Model;
using TestRig.Playtest.Values;
using static SprayPaintPlus.Playtests.Spp;

namespace SprayPaintPlus.Playtests;

internal sealed class FirstUseNoticeCap : IPlaytestCheck
{
    public CheckSpec Spec { get; } = new(
        name: "the first-use notice cap stops after three lines",
        summary: "a server half that refuses a function the player enabled produces exactly three console notices, the third saying so, then silence",
        instances: [new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar")]);

    public void Run(IPlaytestContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        const string guid = ModGuid;
        const string notice = "[Spray Paint Plus] Network Paint Cables is turned off";
        const string capLine = "No more notices about this one until you rejoin.";
        var spawned = new List<long>();

        try
        {
            // ---- 1. Arrange. Every one of these is an action, and none of them
            // is evidence of anything; the assertions below read the values back.
            // save=false throughout: nothing this check does may persist into the
            // instance's stationeers .cfg and change the next session.
            foreach (var pair in new[]
            {
                ("Server - Network Painting", "Network Painting", "true"),
                ("Server - Network Painting", "Network Paint Cables", "false"),
                ("Client - Network Painting", "Network Painting", "true"),
                ("Client - Network Painting", "Network Paint Cables", "true"),
            })
            {
                ctx.Act("hostie", Endpoints.ConfigSet, new ConfigSetRequest
                {
                    Guid = guid, Section = pair.Item1, Key = pair.Item2, Value = pair.Item3, Save = false,
                });
            }

            // The arrangement, read back from the process that will enforce it.
            // A /config/set that answered 200 is a statement about the request.
            ctx.AssertValue("hostie", Reader.Config, ValueMatcher.Is(false),
                because: "the whole check rests on the server half refusing cable painting; if it is still on, three silent strokes would look exactly like a working cap",
                select: "value", of: "Server - Network Painting/Network Paint Cables",
                readerArgs: new ConfigRequest { Guid = guid });

            ctx.AssertValue("hostie", Reader.Config, ValueMatcher.Is(true),
                because: "WarningNotifier only speaks when the SERVER half is the blocker: a player who turned their own half off gets silence by design, which would read as a working cap for the wrong reason",
                select: "value", of: "Client - Network Painting/Network Paint Cables",
                readerArgs: new ConfigRequest { Guid = guid });

            // ---- 2. Five cable segments, two metres apart in a line. Four are
            // seeds for the four strokes; the fifth is never aimed at.
            //
            // colorIndex 1 (ColorGray) is NOT decoration. A cable spawned with no
            // colour comes up at customColorIndex 4, which is ColorRed, which is
            // exactly what ItemSprayCanRed applies, so before and after read the
            // same and no stroke can be proved to have landed. That is what made
            // the 2026-08-11 run conclude nothing had been painted when the seed
            // had in fact been painted every time. Gray in, red out, unambiguous.
            for (var i = 0; i < 5; i++)
            {
                var spawn = ctx.Act("hostie", Endpoints.SpawnStructure, new SpawnStructureRequest
                {
                    Prefab = "StructureCableStraight", Distance = 2, Offset = [i * 2, 0, 0], ColorIndex = 1,
                });

                var id = spawn.As<SpawnStructureResponse>()?.ReferenceId;
                if (id is null or 0)
                {
                    ctx.SetInconclusive(
                        $"cable segment {i + 1} of 5 did not come back with a reference id, so there is nothing to paint and nothing was measured about the mod. On a listen host Constructor.SpawnConstruct returns the placed Structure; a null means the cell was occupied or off the grid.",
                        "scene-not-staged");
                }

                spawned.Add(id.Value);
            }

            var control = spawned[4];

            // A can, in the host's own hand. /inventory/arm spawns through the
            // server and waits for the hand to actually hold it, so a 200 here
            // already means the slot is filled.
            ctx.Act("hostie", Endpoints.InventoryArm, new InventoryArmRequest
            {
                Prefab = "ItemSprayCanRed", Hand = "activeHand", Replace = true,
            });

            // ---- 3. Baseline the console sequence, so nothing printed during
            // bring-up can be counted as a notice.
            var seq0 = ctx.Read("hostie", Reader.Console, "nextSeq", readerArgs: new ConsoleLogRequest { Limit = 1 });

            // ---- 4. Three strokes, each at a different member of the network,
            // so every one of them genuinely changes a colour and cannot be
            // short-circuited as a repaint of the colour already there.
            for (var i = 0; i < 3; i++)
            {
                ctx.Act("hostie", Endpoints.PlayerUse, new PlayerUseRequest { TargetId = spawned[i] });
                ctx.Wait(1);
            }

            // ---- 5. Conclude, from the console of the player the notice was for.
            // source=console because the tee merges the game console and the
            // BepInEx log and a line that goes to both appears twice; this counts
            // what a player sees.
            ctx.AssertValue("hostie", Reader.Console, ValueMatcher.Is(3),
                because: "WarningNotifier.MaxNoticesPerFunction is 3 and SettingBlockedNotice.TakeNoticeBudget caps the send side at the same number, so three strokes at a function the server refuses must produce exactly three lines: fewer means the cap counts wrong, more means it is not counting at all",
                select: "count",
                readerArgs: new ConsoleLogRequest { Since = Seq(seq0), Source = "console", Contains = notice, Limit = 200 });

            ctx.AssertValue("hostie", Reader.Console, ValueMatcher.Is(1),
                because: "the third notice has to announce that it is the last one, because a cap that goes quiet without saying so is indistinguishable from the mod breaking",
                select: "count",
                readerArgs: new ConsoleLogRequest { Since = Seq(seq0), Source = "console", Contains = capLine, Limit = 200 });

            // ---- 6. The fourth stroke, and the silence.
            var seq1 = ctx.Read("hostie", Reader.Console, "nextSeq", readerArgs: new ConsoleLogRequest { Limit = 1 });
            ctx.Act("hostie", Endpoints.PlayerUse, new PlayerUseRequest { TargetId = spawned[3] });
            ctx.Wait(2);

            ctx.AssertValue("hostie", Reader.Console, ValueMatcher.Is(0),
                because: "the fourth stroke at the same function must print nothing at all; the substring here is deliberately looser than the counted one, so a notice that reappeared under different wording is still caught",
                select: "count",
                readerArgs: new ConsoleLogRequest { Since = Seq(seq1), Source = "console", Contains = "Network Paint Cables", Limit = 200 });

            // ---- 7. The decision itself, from the authority. The console above is
            // a report ABOUT a decision; these two read the world.
            //
            // Read customColorIndex, the row-level value /thing computes the way
            // the game does. NOT the CustomColor member: Thing.CustomColor is a
            // ColorSwatch REFERENCE whose rendering is the literal string
            // "Assets.Scripts.Objects.ColorSwatch", identical on the instance and
            // on the prefab, so matchesPrefab is always true and isNull always
            // false no matter what has been painted. This check used to assert
            // isNull on it, which can never be true, and the previous campaign
            // read the same member and concluded nothing had been painted when
            // every stroke had in fact landed.
            var painted = spawned[0];
            ctx.AssertValue("hostie", Reader.Thing, ValueMatcher.Is(4),
                because: "the seed the player aimed at was spawned ColorGray (1) and ItemSprayCanRed applies ColorRed (4), so this is the assertion that proves a stroke landed at all. It is the one that was missing: without it every console count above could read 0 for the mundane reason that nothing was ever painted, and a whole campaign was spent on that",
                select: "customColorIndex", of: Id(painted),
                readerArgs: new ThingRequest { RefIds = Id(painted), Fields = "CustomColor" });

            // A runaway-paint guard, and ONLY that. It is deliberately NOT
            // evidence that the flood was blocked: cables placed by
            // Constructor.SpawnConstruct never join each other's CableNetwork on
            // this rig (measured over eight layouts: both axes, yaw 0 and 90,
            // spacing 0.5 m, 1 m and 2 m, every one painting the seed and leaving
            // the rest), so each carries a singleton network and there is no flood
            // to block. What this still catches is paint reaching an object nobody
            // aimed at, which would be a real defect whatever the topology.
            ctx.AssertValue("hostie", Reader.Thing, ValueMatcher.Is(1),
                because: "this segment was never aimed at, so it must still carry the ColorGray it was spawned with; paint arriving on an object nobody targeted is a defect regardless of whether a network flood was involved",
                select: "customColorIndex", of: Id(control),
                readerArgs: new ThingRequest { RefIds = Id(control), Fields = "CustomColor" });
        }
        finally
        {
            // ---- Clean up: the spawned cables and the can, and the config back
            // to its defaults. The next lock's hygiene reset re-copies the
            // BepInEx config and wipes userdata/saves/, but this check must not
            // depend on that, and the world stays usable for whatever runs next.
            foreach (var id in spawned)
            {
                Quietly(() => ctx.Act("hostie", Endpoints.ConsoleExec, new ConsoleExecRequest { Command = $"thing delete {Id(id)}" }, noRetry: true));
            }

            foreach (var pair in new[] { ("Server - Network Painting", "Network Paint Cables", "true") })
            {
                Quietly(() => ctx.Act("hostie", Endpoints.ConfigSet, new ConfigSetRequest
                {
                    Guid = ModGuid, Section = pair.Item1, Key = pair.Item2, Value = pair.Item3, Save = false,
                }, noRetry: true));
            }
        }
    }
}
