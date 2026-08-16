// =============================================================================
// Spray Paint Plus: the effective-settings line stays in the log, alone
// =============================================================================
// THE HEADLESS REGRESSION IN Mods/SprayPaintPlus/PLAYTEST.md IS NOT EXPRESSIBLE
// IN THIS HARNESS, AND THIS FILE IS NOT A SUBSTITUTE FOR IT. Read this before
// treating a pass here as the regression guard being green.
//
// That entry says: re-run Scenario = spp-settings-merge-verify and assert its own
// pass tally. Three things stop this harness from doing it, and none of them is
// a missing verb that a check could invent:
//
//   1. The scenario runs inside ScenarioRunner, which is deployed to the
//      DEDICATED SERVER only. The harness's launcher seam drives the CLIENT
//      half, so no check can drive the dedicated server through it.
//   2. Every reader resolves an instance NAME to a client-rig control-plane
//      port. The dedicated server has no control plane and is not in the client
//      rig registry, so there is nothing for the instance name to name.
//   3. The tally is a line in the server's BepInEx log:
//        [ScenarioRunner] spp-settings-merge | RESULT ALL PASS pass=N fail=0 total=N
//      No reader answers "what is in that log", and the assert verbs take a
//      reader and nothing else. Reaching around them to compare a string a check
//      read for itself would be the bare-boolean assert the harness exists to
//      prevent.
//
// Run it by hand, under the same session lock, and read the tally yourself:
//   set Scenario = spp-settings-merge-verify in
//     TestRig/DedicatedServer/install/BepInEx/config/net.scenariorunner.cfg
//   testrig start -Target server -As <id> -New <Map>
//   testrig logs -Target server -Grep 'spp-settings-merge \| RESULT'
//
// WHAT THIS CHECK DOES COVER
// PLAYTEST.md names ONE assertion inside that scenario as the reason to re-run it
// for this change: "P6 asserts LogEffectiveSettings emits exactly one Info line
// on the mod's log source; nothing in this change touches that method, but it is
// the assertion that would catch a stray PlayerMessage.Info slipping onto the
// same source." That property is observable on a client instance, and this check
// pins it plus the two things around it:
//
//   - the support line goes to the BepInEx log EXACTLY ONCE per join;
//   - it never reaches the player's console, because it is long and is for
//     whoever reads a bug report rather than for the player mid-game;
//   - a joiner with nothing blocked is told nothing at all, which is the
//     if (blocked.Count == 0) return in OnJoinPayloadReceived and the other half
//     of the join-summary check next to this one.
//
// WHY THE LINE IS COUNTED ON A JOINER AND NOT ON A LONE HOST
// On a host the only emission comes from OnAllModsLoaded, during boot, and the
// console tee keeps 2000 lines per source while mod loading produces thousands
// in a couple of seconds. A boot-time line is routinely evicted before anything
// can read it, and a check built on that would fail for a reason that has
// nothing to do with the mod. OnJoinPayloadReceived emits it again at join time,
// in a quiet window the check controls, which is measurable.
//
// WHAT WOULD MAKE THIS FAIL
//   - a stray PlayerMessage.Info on the mod's log source inside the join window:
//     the count would exceed one;
//   - LogEffectiveSettings being emitted twice per join, or not at all;
//   - the support line reaching the console, where it does not belong;
//   - a join summary appearing when this server refuses nothing.
//
// THIS CHECK WOULD ALSO PASS AGAINST THE PRE-v1.11.0 BUILD. It deliberately
// carries no display-name prefix in any filter, because the line it counts is a
// log line and never had one. It is a regression guard, not a migration
// discriminator: checks 01, 02 and 03 are what tell the two builds apart.
//
// PREREQUISITES
//   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
//   copy bin/Release/SprayPaintPlus.dll into the SprayPaintPlus folder under
//     TestRig/ClientRig/data/hostie/userdata/mods/ and .../joiner/userdata/mods/
// =============================================================================

using TestRig.Contracts;
using TestRig.Playtest;
using TestRig.Playtest.Model;
using TestRig.Playtest.Values;
using static SprayPaintPlus.Playtests.Spp;

namespace SprayPaintPlus.Playtests;

internal sealed class EffectiveSettingsLogLine : IPlaytestCheck
{
    public CheckSpec Spec { get; } = new(
        name: "the effective-settings line is one log line and never reaches the console",
        summary: "the support dump lands in the BepInEx log exactly once per join, stays out of the player console, and a server that blocks nothing says nothing",
        instances:
        [
            new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar"),
            new InstanceSpec("joiner", InstanceRole.Client, ConnectTo: "hostie"),
        ]);

    public void Run(IPlaytestContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        const string guid = ModGuid;
        const string supportLine = "Effective settings (client/server";

        // Every paired boolean, by group. The instance's BepInEx config is
        // re-copied from the developer's own install when a lock is taken, so a
        // value the developer left switched off would otherwise put a function in
        // the blocked list and make the last assertion here fail for a reason
        // that is nothing to do with the code under test. Both halves are pinned
        // to permissive so "nothing is blocked" is a fact rather than a hope.
        string[] networkKeys =
        [
            "Network Painting", "Network Paint Pipes", "Network Paint Cables",
            "Network Paint Chutes", "Network Paint Walls", "Network Paint Rails",
            "Network Paint Large Structures", "Network Paint Elevators",
            "Network Paint Ladders", "Network Paint Stairs", "Network Paint Stairwells",
        ];

        var permissive = new List<(string On, string Section, string Key, string Value)>();
        foreach (var key in networkKeys)
        {
            permissive.Add(("hostie", "Server - Network Painting", key, "true"));
            permissive.Add(("joiner", "Client - Network Painting", key, "true"));
        }

        permissive.Add(("hostie", "Server - Consumables", "Unlimited Spray Paint Uses", "true"));
        permissive.Add(("joiner", "Client - Consumables", "Unlimited Spray Paint Uses", "true"));
        permissive.Add(("hostie", "Server - Glow Paint", "Glow Paint", "true"));
        permissive.Add(("joiner", "Client - Glow Paint", "Glow Paint", "true"));
        permissive.Add(("hostie", "Server - Color Cycling", "Color Picking", "true"));
        permissive.Add(("joiner", "Client - Color Cycling", "Color Picking", "true"));
        permissive.Add(("hostie", "Server - Color Cycling", "Color Cycling", "AllColors"));
        permissive.Add(("joiner", "Client - Color Cycling", "Color Cycling", "AllColors"));

        foreach (var entry in permissive)
        {
            ctx.Act(entry.On, Endpoints.ConfigSet, new ConfigSetRequest
            {
                Guid = guid, Section = entry.Section, Key = entry.Key, Value = entry.Value, Save = false,
            });
        }

        // Two spot reads from the two authorities, one per half. Reading all 30
        // back would say nothing the first two do not.
        ctx.AssertValue("hostie", Reader.Config, ValueMatcher.Is(true),
            because: "the last assertion in this check is that a server refusing nothing says nothing, and a single server half left off from a previous session would produce a summary line and turn a correct build into a failure",
            select: "value", of: "Server - Network Painting/Network Paint Cables",
            readerArgs: new ConfigRequest { Guid = guid });

        ctx.AssertValue("joiner", Reader.Config, ValueMatcher.Is(true),
            because: "AddIfBlocked only reports a function the player has enabled, so a client half left off would hide a genuine mismatch instead of reporting it",
            select: "value", of: "Client - Glow Paint/Glow Paint",
            readerArgs: new ConfigRequest { Guid = guid });

        // ---- Baseline the joiner's console, then bounce it so the join payload
        // is rebuilt and LogEffectiveSettings runs inside a window this check
        // controls. The tee is process-local and survives leaving a world.
        var seq0 = Seq(ctx.Read("joiner", Reader.Console, "nextSeq", readerArgs: new ConsoleLogRequest { Limit = 1 }));

        ctx.Act("joiner", Endpoints.Disconnect, new DisconnectRequest(), blocking: true);
        ctx.WaitStage("joiner", Stage.Menu, 180);

        // The harness's own bring-up path, reused rather than copied. The copy
        // this replaced connected once and read the roster once, which is the
        // 2026-08-11 joiner-not-in-roster inconclusive on a rig that was joining
        // fine.
        var join = ctx.ConnectJoiner("joiner", "hostie");

        // Re-baseline from the join that actually LANDED. LogEffectiveSettings
        // runs once per join, so a window opened before a retried join holds one
        // line per attempt and the "exactly one Info line" assertion would fail a
        // correct mod.
        if (join.SeqBeforeConnect is { } landed) seq0 = landed;
        ctx.Wait(5);

        // ---- Conclude on the joiner, which is the authority for its own log
        // and its own console.
        ctx.AssertValue("joiner", Reader.Console, ValueMatcher.Is(1),
            because: "SettingsConfigSync calls LogEffectiveSettings once when the host values land, and exactly one line is the property the headless P6 assertion protects: two would mean something calls it twice, none would mean a bug report arrives with no settings dump in it",
            select: "count",
            readerArgs: new ConsoleLogRequest { Since = seq0, Source = "bepinex", Contains = supportLine, Limit = 500 });

        ctx.AssertValue("joiner", Reader.Console, ValueMatcher.Is(0),
            because: "the support line is long and is for whoever reads the log after a bug report, never for the player mid-game; a PlayerMessage call replacing the plain log call would put it on screen and this is what would catch it",
            select: "count",
            readerArgs: new ConsoleLogRequest { Since = seq0, Source = "console", Contains = "Effective settings", Limit = 500 });

        // WHERE THIS LITERAL IS PROVED LIVE, AND WHY IT IS NOT PROVED HERE.
        // Is(0) over a Contains filter passes whether the literal is right or wrong,
        // so a count of zero is only evidence when something else establishes that
        // the mod still prints those words. The two assertions above are
        // self-guarding: "Effective settings" is a prefix of supportLine, which the
        // Is(1) immediately before them requires to appear in the log.
        //
        // This one is guarded by a SIBLING check instead. "the join summary lists
        // every blocked function once" (JoinSummary.cs) arranges the opposite server
        // and asserts Is(1) on "[Spray Paint Plus] This server does not allow" from
        // the same instance, the same reader and the same source, so a drift in the
        // wording fails there, loudly, on any run of the suite.
        //
        // The residual risk is named rather than fixed: running this check alone
        // (--only) would not catch a drifted literal, and neither would deleting
        // JoinSummary. Proving it here instead would mean flipping a server half off,
        // bouncing the joiner through a second full disconnect and rejoin, and
        // arriving at exactly the arrangement JoinSummary exists to test, which is a
        // minute of bring-up spent duplicating a check that already runs.
        ctx.AssertValue("joiner", Reader.Console, ValueMatcher.Is(0),
            because: "this server refuses nothing the joiner asked for, so OnJoinPayloadReceived must return without printing; a summary listing nothing, or listing a function that is not actually blocked, is noise a player cannot act on. The literal is proved live by the JoinSummary check, which asserts it PRESENT on this same reader with a server that does refuse something",
            select: "count",
            readerArgs: new ConsoleLogRequest { Since = seq0, Source = "console", Contains = "This server does not allow", Limit = 500 });
    }
}
