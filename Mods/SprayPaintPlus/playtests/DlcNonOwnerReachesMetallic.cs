// =============================================================================
// Spray Paint Plus: a non-owner reaches metallic while an owner is connected
// =============================================================================
// From Mods/SprayPaintPlus/PLAYTEST.md, Session B: "The non-owning player must be
// able to scroll to and paint with metallic colors while the owner is connected.
// That is vanilla shared-DLC behavior, not a bypass."
//
// THE NON-OWNER HERE IS THE HOST, AND THAT IS FORCED BY THE GAME, not a
// preference. DlcPaintGate.IsColorAllowed delegates to
// SharedDLCManager.CheckSharedAccess, which reads the session POOL and nothing
// else. Only two things ever fill that pool: HostFinishedLoad, on the world LOAD
// path, and ClientFinishedLoad, at the very end of each client's join. The
// new-world path never seeds it at all. So on a freshly created world the only
// way an entitlement can be in the pool is for a JOINING CLIENT to contribute it,
// which makes the joiner the owner and the host the non-owner.
// Research/GameSystems/DLCGating.md, "Single player: new world versus loaded
// world" and "Dedicated server behavior".
//
// SEQUENCING IS LOAD BEARING AND EASY TO GET WRONG
// POST /dlc/remove is removal-only and it refuses outright before
// GameManager.IsInitialized. It has to run at the MENU and before POST /host:
// SharedDLCManager.HostFinishedLoad re-seeds the pool from
// DLCManager.GetOwnedDLC() at the end of the load path, so a host stripped after
// its world is up would already have been seeded, and the removal would look
// exactly like one that worked. scope=owned, never shared: shared is the pool
// this check needs the joiner to fill. The endpoint returns the full ordering in
// the sequence array of every /dlc response.
//
// THAT SEQUENCING IS WHY BOTH INSTANCES ARE DECLARED Role=client WITH NO HOST
// IN THE LIST. The harness brings hosts all the way into their world and connects
// joiners before a check body runs, and the window this check needs is exactly
// between "reached the menu" and "hosts or connects". Declared this way, bring-up
// stops at the menu and the body drives /dlc/remove, /host and /connect in the
// order above. The joiner is declared FIRST so the guaranteed teardown stops it
// before the instance that ends up holding the world.
//
// WHAT THIS CHECK DEPENDS ON THAT IS NOT YET PROVEN LIVE
// POST /dlc/remove itself. It is new, and the whole arrangement rests on it: if
// it refuses, or if the strip does not survive world entry, this check declines
// at the guard rather than accusing the mod. The paint-and-read half is the same
// shape as checks that have already run live.
//
// WHAT WOULD MAKE THIS FAIL
//   - DlcPaintGate refusing a shared entitlement, so the non-owner's scroll skips
//     every metallic swatch and the can never leaves the base family: the painted
//     structure would read a base swatch index;
//   - the gate being bypassed in the other direction is NOT what this check
//     measures. Check 08 covers the pool outliving its owner; a session that owns
//     nothing at all is what the headless spp-dlc-gate-verify scenario covers.
//
// THIS CHECK WOULD ALSO PASS AGAINST THE PRE-v1.11.0 BUILD. The shared-DLC path
// it exercises predates the settings split; it is here because Session B has
// never been run with a real non-owner, not because the migration touched it.
//
// LIVE-RUN RISK WORTH KNOWING: the assertion reads ColorSwatch.Index off the
// painted structure and compares it against the 12-to-15 metallic band from
// Research/GameClasses/ColorSwatch.md. GET /colors reports index and swatchIndex
// as separate numbers, so if a swatch ever carries an Index that is not its
// position in GameManager.CustomColors, this comparison is against the wrong
// numbering and would need /colors consulted first.
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

internal sealed class DlcNonOwnerReachesMetallic : IPlaytestCheck
{
    public CheckSpec Spec { get; } = new(
        name: "a non-owner reaches metallic while the owner is connected",
        summary: "a host stripped of Metallic Paints can still scroll a base can onto a metallic swatch, because a connected owner put the entitlement in the session pool",
        instances:
        [
            new InstanceSpec("joiner", InstanceRole.Client),
            new InstanceSpec("hostie", InstanceRole.Client),
        ]);

    public void Run(IPlaytestContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        const string guid = ModGuid;
        const int firstMetallic = FirstMetallic;       // ColorObsidian; 12 to 15 are the Metallic Paints band
        long cable = 0;
        var stripped = false;

        try
        {
            // ---- 1. Both instances have to be AT THE MENU, because that is the
            // only window in which entitlement can be removed at all.
            foreach (var name in new[] { "hostie", "joiner" })
            {
                var phase = ctx.Read(name, Reader.Status, "phase");
                if (phase.Text != "menu")
                {
                    ctx.SetInconclusive(
                        $"'{name}' reports phase={phase.Text} and this check has to strip entitlement before world entry: POST /dlc/remove refuses before GameManager.IsInitialized, and a removal after world entry is silently undone by the game's own re-seeding. Nothing was measured about the mod.",
                        "not-at-menu");
                }
            }

            // ---- 2. The entitlement precondition, from each process's own view.
            foreach (var name in new[] { "hostie", "joiner" })
            {
                var owned = ctx.Read(name, Reader.Dlc, "state.owned");
                if (!owned.Text.Contains("MetallicPaints", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.SetInconclusive(
                        $"'{name}' reports owning [{owned.Text}], which does not include MetallicPaints, so there is no owner to share it and no entitlement to strip. This Steam session does not own the DLC; nothing was measured about the mod.",
                        "dlc-not-owned");
                }
            }

            // ---- 3. Strip the HOST, at the menu, owned scope only. shared is
            // the pool the joiner is about to fill and must be left alone.
            ctx.Act("hostie", Endpoints.DlcRemove, new DlcRemoveRequest { Dlc = "MetallicPaints", Scope = "owned" });
            stripped = true;

            ctx.AssertValue("hostie", Reader.Dlc, ValueMatcher.Matches("MetallicPaints"),
                because: "the whole arrangement is a host that does not own the DLC, and the endpoint reports what it actually cleared: an empty removedOwned means the process still owns it and the non-owner in this check is not one",
                select: "state.removedOwned");

            // ---- 4. Host the world, THEN bring the owner in. The joiner is the
            // only thing that can put MetallicPaints in the pool of a created
            // world.
            ctx.Act("hostie", Endpoints.Host, new HostRequest { World = "Lunar" }, blocking: true);
            ctx.WaitStage("hostie", Stage.InWorld, 600);

            var hosting = ctx.Read("hostie", Reader.Status, "hosting");
            var role = ctx.Read("hostie", Reader.Status, "role");
            if (!ValueText.AsBoolean(hosting.Value) || role.Text != "listenHost")
            {
                ctx.SetInconclusive(
                    $"the host answered POST /host but reports hosting={hosting.Text} role={role.Text}. NetworkServer.Host() gives up quietly after three failed binds, so the call returning proves nothing and there is nothing for the owner to join.",
                    "host-not-hosting");
            }

            // The strip has to have survived world entry. This is the assertion
            // that catches the sequencing mistake the endpoint warns about.
            ctx.AssertValue("hostie", Reader.Dlc, ValueMatcher.Matches("MetallicPaints"),
                because: "DLCManager._ownedDLC is read at world entry by both paths that fill the session pool, so a removal that did not survive it would leave the host quietly owning the DLC and every reading below would be about the wrong arrangement",
                select: "state.removedOwned");

            // The harness's own bring-up path, not a copy of it. This check
            // declares no host in its instances (so bring-up stops at the menu and
            // the body can strip the entitlement first), which used to mean it
            // also had to hand-roll the join; the hand-rolled version connected
            // once and read the roster once, and reported joiner-not-in-roster on
            // 2026-08-11 on a rig that was joining fine.
            ctx.ConnectJoiner("joiner", "hostie");
            ctx.Wait(5);

            // ---- 5. The pool, read on the authority. This is vanilla shared-DLC
            // behaviour rather than anything the mod does, so it is guarded and
            // not asserted: an empty pool means the arrangement failed, not that
            // the mod misbehaved.
            var pool = ctx.Read("hostie", Reader.Dlc, "state.shared");
            if (!pool.Text.Contains("MetallicPaints", StringComparison.OrdinalIgnoreCase))
            {
                ctx.SetInconclusive(
                    $"the host's shared pool reads [{pool.Text}] with the owner connected, so ClientFinishedLoad's AvailableDLCMessage did not land. Without it there is nothing for a non-owner to inherit and the check would measure an ordinary refusal.",
                    "entitlement-not-in-pool");
            }

            // ---- 6. Cycling has to be able to leave the base family at all.
            // WithinFamily would pin a base can to the base colours, which is a
            // correct refusal and would read here as the gate blocking a shared
            // entitlement.
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

            ctx.AssertValue("hostie", Reader.Config, ValueMatcher.Is(false),
                because: "the scroll below counts twelve notches forward from swatch 0, and an inverted wheel would count backwards into the base colours and fail for a reason that has nothing to do with entitlement",
                select: "value", of: "Client - Preferences/Invert Color Scroll Direction",
                readerArgs: new ConfigRequest { Guid = guid });

            // ---- 7. The non-owner scrolls a BASE can up into the metallic band.
            // Twelve notches from ColorBlue lands on ColorObsidian when nothing
            // is skipped, and DlcPaintGate is the only thing that skips.
            var spawn = ctx.Act("hostie", Endpoints.SpawnStructure, new SpawnStructureRequest
            {
                Prefab = "StructureCableStraight", Distance = 3,
            });

            var spawnedId = spawn.As<SpawnStructureResponse>()?.ReferenceId;
            if (spawnedId is null or 0)
            {
                ctx.SetInconclusive(
                    "the structure to paint did not come back with a reference id, so the scroll has nothing to prove itself against",
                    "scene-not-staged");
            }

            cable = spawnedId.Value;

            ctx.Act("hostie", Endpoints.InventoryArm, new InventoryArmRequest
            {
                Prefab = "ItemSprayCanBlue", Hand = "activeHand", Replace = true,
            });
            ctx.Wait(2);
            ctx.Act("hostie", Endpoints.InputScroll, new InputScrollRequest { Notches = 1, Repeat = 12, GapFrames = 3 });
            ctx.Wait(2);

            // The can's colour lives in a Material and a static dictionary, so it
            // is read where it becomes a number: on the object it paints, on the
            // machine that owns the simulation.
            ctx.Act("hostie", Endpoints.PlayerUse, new PlayerUseRequest { TargetId = cable });
            ctx.Wait(2);

            ctx.AssertValue("hostie", Reader.Thing, ValueMatcher.AtLeast(firstMetallic),
                because: "a player who owns nothing must still reach the four Metallic Paints swatches while an owner is in the session, because DlcPaintGate asks SharedDLCManager.CheckSharedAccess and that reads the session pool; a base swatch index here means the scroll skipped every metallic colour and the shared entitlement was ignored",
                select: "value", of: $"{Id(cable)}/CustomColor.Index",
                readerArgs: new ThingRequest { RefIds = Id(cable), Fields = "CustomColor.Index" });
        }
        finally
        {
            if (cable != 0)
            {
                Quietly(() => ctx.Act("hostie", Endpoints.ConsoleExec, new ConsoleExecRequest { Command = $"thing delete {Id(cable)}" }, noRetry: true));
            }

            // Put the host's own entitlement back. It is per process and in
            // memory only, so it would go anyway when the process ends, but a
            // check that leaves a stripped process running is one the next check
            // under the same lock would inherit.
            if (stripped)
            {
                Quietly(() => ctx.Act("hostie", Endpoints.DlcRestore, new DlcRestoreRequest(), noRetry: true));
            }

            // Leave the world with nobody attached, so the guaranteed teardown
            // never has to stop a host underneath a live joiner.
            Quietly(() => ctx.Act("joiner", Endpoints.Disconnect, new DisconnectRequest(), blocking: true, noRetry: true));
        }
    }
}
