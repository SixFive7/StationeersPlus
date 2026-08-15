// =============================================================================
// Spray Paint Plus: the entitlement outlives the owner
// =============================================================================
// From Mods/SprayPaintPlus/PLAYTEST.md, Session B: "After the owning client
// disconnects, metallic must stay available to everyone still connected until the
// world unloads. Needs a second player to remain connected and observe."
//
// The player who remains is the HOST, for the same reason as in check 07: only a
// joining client can put an entitlement into the pool of a freshly created world,
// so the owner has to be the joiner and the observer has to be the host. The host
// is also the authority, which is where every reading below is taken.
//
// The behaviour under test is a property of the pool, and the pool only ever
// GROWS during a session: nothing subtracts on disconnect, and ClearAll() runs on
// world teardown rather than on a player leaving. So a non-owner who could reach
// metallic while the owner was connected must still reach it afterwards.
//
// WHY THE SECOND HALF IS A SCROLL AND NOT A SECOND PAINT
// Entitlement is consulted on the mod's cycling path and on the eyedropper, and
// nowhere on the paint-application path (Research/GameSystems/DLCGating.md: there
// is no check in Thing.SetCustomColor, OnServer.SetCustomColor or ISprayer.
// DoSpray). Painting again with a can that is ALREADY metallic would therefore
// prove nothing about entitlement at all. The check arms a fresh base can after
// the owner has gone and scrolls it up from swatch 0, which is the only action
// that has to ask the gate.
//
// SEQUENCING, AND WHY BOTH INSTANCES ARE Role=client WITH NO HOST IN THE LIST
// Identical to check 07 and for the same reason: POST /dlc/remove has to run at
// the MENU and before POST /host, and the harness's bring-up leaves no window
// between "reached the menu" and "hosts or connects". The joiner is declared
// first so the guaranteed teardown stops it before the instance holding the
// world. scope=owned, never shared, because shared is the pool this check is
// about.
//
// WHAT THIS CHECK DEPENDS ON THAT IS NOT YET PROVEN LIVE
// POST /dlc/remove, exactly as in check 07. Everything else is the ordinary
// connect, disconnect and paint path.
//
// WHAT WOULD MAKE THIS FAIL
//   - the pool being cleared when its contributor leaves, which would take the
//     entitlement away from every remaining player mid-session;
//   - DlcPaintGate consulting local ownership rather than the pool, which would
//     make the host lose access the moment the owner disconnected even though
//     the pool still carried the bit.
//
// THIS CHECK IS A SUPERSET OF CHECK 07 BY CONSTRUCTION: it has to establish that
// the non-owner could reach metallic WHILE the owner was connected before "still"
// means anything. Check 07 remains worth running on its own, because when both
// fail together the pair says which half broke.
//
// THIS CHECK WOULD ALSO PASS AGAINST THE PRE-v1.11.0 BUILD, like check 07. The
// shared-DLC path predates the settings split.
//
// PREREQUISITES
//   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
//   copy bin/Release/SprayPaintPlus.dll into the SprayPaintPlus folder under
//     TestRig/ClientRig/data/hostie/userdata/mods/ and .../joiner/userdata/mods/
//   the Steam session must own Metallic Paints; the check declines otherwise
// =============================================================================

using TestRig.Contracts;
using TestRig.Playtest;
using TestRig.Playtest.Model;
using TestRig.Playtest.Values;
using static SprayPaintPlus.Playtests.Spp;

namespace SprayPaintPlus.Playtests;

internal sealed class DlcEntitlementOutlivesTheOwner : IPlaytestCheck
{
    public CheckSpec Spec { get; } = new(
        name: "the entitlement outlives the owner",
        summary: "a non-owning host that could reach metallic while the owner was connected must still reach it after the owner disconnects",
        instances:
        [
            new InstanceSpec("joiner", InstanceRole.Client),
            new InstanceSpec("hostie", InstanceRole.Client),
        ]);

    public void Run(IPlaytestContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        const string guid = ModGuid;
        const int firstMetallic = FirstMetallic;
        var spawned = new List<long>();
        var stripped = false;

        try
        {
            // ---- 1. Menu, entitlement, strip. Same three steps as check 07 and
            // in the same order, because the order is the mechanism.
            foreach (var name in new[] { "hostie", "joiner" })
            {
                var phase = ctx.Read(name, Reader.Status, "phase");
                if (phase.Text != "menu")
                {
                    ctx.SetInconclusive(
                        $"'{name}' reports phase={phase.Text}, and entitlement can only be removed between GameManager.IsInitialized and world entry. Nothing was measured about the mod.",
                        "not-at-menu");
                }

                var owned = ctx.Read(name, Reader.Dlc, "state.owned");
                if (!owned.Text.Contains("MetallicPaints", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.SetInconclusive(
                        $"'{name}' reports owning [{owned.Text}], which does not include MetallicPaints, so this session has no owner to lose and nothing to strip",
                        "dlc-not-owned");
                }
            }

            ctx.Act("hostie", Endpoints.DlcRemove, new DlcRemoveRequest { Dlc = "MetallicPaints", Scope = "owned" });
            stripped = true;

            ctx.AssertValue("hostie", Reader.Dlc, ValueMatcher.Matches("MetallicPaints"),
                because: "the observer in this check has to be a genuine non-owner, or the reading after the owner leaves is just a process consulting its own entitlement",
                select: "state.removedOwned");

            // ---- 2. Host, then bring the owner in.
            ctx.Act("hostie", Endpoints.Host, new HostRequest { World = "Lunar" }, blocking: true);
            ctx.WaitStage("hostie", Stage.InWorld, 600);

            var hosting = ctx.Read("hostie", Reader.Status, "hosting");
            var role = ctx.Read("hostie", Reader.Status, "role");
            if (!ValueText.AsBoolean(hosting.Value) || role.Text != "listenHost")
            {
                ctx.SetInconclusive(
                    $"the host reports hosting={hosting.Text} role={role.Text} after POST /host, so there is no session for the owner to join",
                    "host-not-hosting");
            }

            ctx.AssertValue("hostie", Reader.Dlc, ValueMatcher.Matches("MetallicPaints"),
                because: "a strip that did not survive world entry would leave the host owning the DLC outright, and the whole check would be measuring a process that never lost anything",
                select: "state.removedOwned");

            // The harness's own bring-up path, not a copy of it: it reads the port
            // off the host, polls the HOST roster rather than reading it once, and
            // retries from the menu. The copy this replaced reported
            // joiner-not-in-roster on 2026-08-11 on a rig that was joining fine.
            ctx.ConnectJoiner("joiner", "hostie");
            ctx.Wait(5);

            var poolWithOwner = ctx.Read("hostie", Reader.Dlc, "state.shared");
            if (!poolWithOwner.Text.Contains("MetallicPaints", StringComparison.OrdinalIgnoreCase))
            {
                ctx.SetInconclusive(
                    $"the host's pool reads [{poolWithOwner.Text}] with the owner connected, so nothing was ever shared and 'it outlives the owner' has no starting point",
                    "entitlement-not-in-pool");
            }

            // ---- 3. Cycling has to be able to leave the base family, and the
            // wheel has to run forwards.
            foreach (var pair in new[]
            {
                ("Client - Color Cycling", "Color Cycling", "AllColors"),
                ("Server - Color Cycling", "Color Cycling", "AllColors"),
                ("Client - Preferences", "Invert Color Scroll Direction", "false"),
            })
            {
                ctx.Act("hostie", Endpoints.ConfigSet, new ConfigSetRequest
                {
                    Guid = guid, Section = pair.Item1, Key = pair.Item2, Value = pair.Item3, Save = false,
                });
            }

            // ---- 4. The starting point: the non-owner reaches metallic WHILE
            // the owner is connected. Without this, "still" below means nothing.
            for (var i = 0; i < 2; i++)
            {
                var spawn = ctx.Act("hostie", Endpoints.SpawnStructure, new SpawnStructureRequest
                {
                    Prefab = "StructureCableStraight", Distance = 3, Offset = [i * 6, 0, 0],
                });

                var id = spawn.As<SpawnStructureResponse>()?.ReferenceId;
                if (id is null or 0)
                {
                    ctx.SetInconclusive(
                        "a structure to paint did not come back with a reference id, so a scroll has nothing to prove itself against",
                        "scene-not-staged");
                }

                spawned.Add(id.Value);
            }

            ctx.Act("hostie", Endpoints.InventoryArm, new InventoryArmRequest
            {
                Prefab = "ItemSprayCanBlue", Hand = "activeHand", Replace = true,
            });
            ctx.Wait(2);
            ctx.Act("hostie", Endpoints.InputScroll, new InputScrollRequest { Notches = 1, Repeat = 12, GapFrames = 3 });
            ctx.Wait(2);
            ctx.Act("hostie", Endpoints.PlayerUse, new PlayerUseRequest { TargetId = spawned[0] });
            ctx.Wait(2);

            ctx.AssertValue("hostie", Reader.Thing, ValueMatcher.AtLeast(firstMetallic),
                because: "this is the starting point the rest of the check depends on: a non-owner reaching the metallic band while an owner is connected. If this already fails, the pool is not being consulted at all and nothing can be said about what happens after the owner leaves",
                select: "value", of: $"{Id(spawned[0])}/CustomColor.Index",
                readerArgs: new ThingRequest { RefIds = Id(spawned[0]), Fields = "CustomColor.Index" });

            // ---- 5. The owner leaves.
            ctx.Act("joiner", Endpoints.Disconnect, new DisconnectRequest(), blocking: true);
            ctx.WaitStage("joiner", Stage.Menu, 180);

            var rosterAfter = ctx.Read("hostie", Reader.Roster, "count");
            if (ValueText.TryAsNumber(rosterAfter.Value, out var count) && count > 1)
            {
                ctx.SetInconclusive(
                    $"the host roster still carries {rosterAfter.Text} entries after the owner was told to disconnect, so the owner has not actually left and 'after the owner leaves' has not happened yet",
                    "owner-still-connected");
            }

            ctx.Wait(5);

            ctx.AssertValue("hostie", Reader.Dlc, ValueMatcher.Matches("MetallicPaints"),
                because: "the pool only ever grows during a session and is cleared on world teardown, not on a player leaving; losing the bit here would take metallic paint away from everyone still in the world the moment its owner logged off",
                select: "state.shared");

            // ---- 6. And the behaviour, not just the bookkeeping: a fresh base
            // can, scrolled from swatch 0, with no owner in the session.
            ctx.Act("hostie", Endpoints.InventoryArm, new InventoryArmRequest
            {
                Prefab = "ItemSprayCanBlue", Hand = "activeHand", Replace = true,
            });
            ctx.Wait(2);
            ctx.Act("hostie", Endpoints.InputScroll, new InputScrollRequest { Notches = 1, Repeat = 12, GapFrames = 3 });
            ctx.Wait(2);
            ctx.Act("hostie", Endpoints.PlayerUse, new PlayerUseRequest { TargetId = spawned[1] });
            ctx.Wait(2);

            ctx.AssertValue("hostie", Reader.Thing, ValueMatcher.AtLeast(firstMetallic),
                because: "the entitlement has to outlive the player who brought it, all the way to world teardown: a base swatch here means the gate started refusing the moment the owner left, which is the mid-session capability loss this check exists to catch",
                select: "value", of: $"{Id(spawned[1])}/CustomColor.Index",
                readerArgs: new ThingRequest { RefIds = Id(spawned[1]), Fields = "CustomColor.Index" });
        }
        finally
        {
            foreach (var id in spawned)
            {
                Quietly(() => ctx.Act("hostie", Endpoints.ConsoleExec, new ConsoleExecRequest { Command = $"thing delete {Id(id)}" }, noRetry: true));
            }

            if (stripped)
            {
                Quietly(() => ctx.Act("hostie", Endpoints.DlcRestore, new DlcRestoreRequest(), noRetry: true));
            }

            // Belt and braces on the teardown ordering: if this check ended
            // between the connect and the disconnect, the world holder would be
            // stopped underneath a live joiner. The guaranteed teardown already
            // stops the joiner first because of the order these instances are
            // declared in, and this makes it true regardless of that ordering.
            Quietly(() => ctx.Act("joiner", Endpoints.Disconnect, new DisconnectRequest(), blocking: true, noRetry: true));
        }
    }
}
