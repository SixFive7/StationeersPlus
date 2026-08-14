// =============================================================================
// Spray Paint Plus: the eyedropper explains a cross-family pick, every time
// =============================================================================
// From Mods/SprayPaintPlus/PLAYTEST.md: "With 'Cycles within paint family' in
// force, right-click a metallic-painted object holding a base can. Expected: the
// same explanation as before, once per click with no cap, and now also present
// in BepInEx\LogOutput.log at Info level, which it never used to be."
//
// ColorCyclerPatch.HandleEyedropper reaches the family branch only after four
// earlier gates pass: no shift held, the cursor is on a paintable Thing, the
// picked colour is ENTITLED, and it differs from the one on the can. So the
// arrangement below is not decoration, and every part of it is guarded rather
// than assumed.
//
// WHY A JOINER IS IN THIS CHECK AND NEVER TOUCHED
// DlcPaintGate.IsColorAllowed delegates to SharedDLCManager.CheckSharedAccess,
// which reads the session POOL and nothing else, and the new-world path never
// seeds that pool: a freshly created world starts empty even on an install that
// owns Metallic Paints (Research/GameSystems/DLCGating.md, "Single player: new
// world versus loaded world"). A joined client contributes its own entitlement
// at the very end of its join, which is what puts MetallicPaints in the pool
// here. Without it the eyedropper returns at the entitlement gate, prints
// nothing, and this check would fail for a reason that has nothing to do with
// paint families. The pool is read back and guarded before anything is clicked.
//
// WHAT WOULD MAKE THIS FAIL
//   - the family rule going quiet: a cross-family pick that answers with silence
//     reads to a player as the mod being broken;
//   - the line acquiring a throttle: the second click would print nothing, and
//     Throttle.Never on this call site is a deliberate decision (a second click
//     at a different object answering with silence is the failure it avoids);
//   - the line reaching the console but not the BepInEx log, which is exactly
//     what the PlayerMessage migration changed;
//   - the console prefix reverting to the code name.
//
// AGAINST THE PRE-v1.11.0 BUILD THIS CHECK FAILS TWICE OVER: the console prefix
// was "[SprayPaintPlus] ", and the line did not reach the BepInEx log at all.
//
// PREREQUISITES
//   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
//   copy bin/Release/SprayPaintPlus.dll into the SprayPaintPlus folder under
//     TestRig/ClientRig/data/hostie/userdata/mods/ and .../joiner/userdata/mods/
//   the Steam session must own Metallic Paints; the check declines otherwise
// =============================================================================

using System.Runtime.CompilerServices;
using TestRig.Contracts;
using TestRig.Playtest;
using TestRig.Playtest.Model;
using TestRig.Playtest.Values;
using static SprayPaintPlus.Playtests.Spp;

namespace SprayPaintPlus.Playtests;

internal sealed class EyedropperCrossFamilyLine : IPlaytestCheck
{
    public CheckSpec Spec { get; } = new(
        name: "the eyedropper explains a cross-family pick once per click",
        summary: "under Cycles within paint family, right-clicking metallic paint with a base can answers every time, on the console and in the log",
        instances:
        [
            new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar"),
            new InstanceSpec("joiner", InstanceRole.Client, ConnectTo: "hostie"),
        ]);

    public void Run(IPlaytestContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        const string guid = ModGuid;
        const int metallic = 12;          // ColorObsidian, the first Metallic Paints swatch
        const string familyLine = "limited to one paint family";
        long target = 0;
        var cursorSet = false;

        try
        {
            // ---- 1. Entitlement, before anything else. This is a precondition
            // of the world, not a claim about the mod, so a session without it
            // declines rather than failing.
            var pool = ctx.Read("hostie", Reader.Dlc, "state.shared");
            if (!pool.Text.Contains("MetallicPaints", StringComparison.OrdinalIgnoreCase))
            {
                ctx.SetInconclusive(
                    $"the host's shared DLC pool reads [{pool.Text}] and does not carry MetallicPaints, so DlcPaintGate.IsColorAllowed refuses every metallic swatch and the eyedropper returns before it can reach the family rule. Either this Steam session does not own Metallic Paints, or the joiner's AvailableDLCMessage did not land. Nothing was measured about the mod.",
                    "entitlement-not-in-pool");
            }

            // ---- 2. Both halves of the cycling mode, and both halves of colour
            // picking. EffectiveColorCycling is the stricter of the two halves,
            // and EffectiveColorPicking folds in the mode, so a wrong value here
            // sends the click down a different branch entirely.
            foreach (var pair in new[]
            {
                ("Client - Color Cycling", "Color Cycling", "WithinFamily"),
                ("Server - Color Cycling", "Color Cycling", "WithinFamily"),
                ("Client - Color Cycling", "Color Picking", "true"),
                ("Server - Color Cycling", "Color Picking", "true"),
            })
            {
                ctx.Act("hostie", Endpoints.ConfigSet, new ConfigSetRequest
                {
                    Guid = guid, Section = pair.Item1, Key = pair.Item2, Value = pair.Item3, Save = false,
                });
            }

            ctx.AssertValue("hostie", Reader.Config, ValueMatcher.Is("WithinFamily"),
                because: "the family rule only exists under this mode; under AllColors the pick is simply allowed and the line is correctly absent, which would read as the message going missing",
                select: "value", of: "Server - Color Cycling/Color Cycling",
                readerArgs: new ConfigRequest { Guid = guid });

            ctx.AssertValue("hostie", Reader.Config, ValueMatcher.Is(true),
                because: "with picking off the click is answered by the blocked-function notice instead, which is a different message with a three-per-session cap",
                select: "value", of: "Client - Color Cycling/Color Picking",
                readerArgs: new ConfigRequest { Guid = guid });

            // ---- 3. A metallic-painted object to aim at. Spawning it already
            // painted avoids needing a metallic can in hand, which would only add
            // a second DLC-gated step.
            var spawn = ctx.Act("hostie", Endpoints.SpawnStructure, new SpawnStructureRequest
            {
                Prefab = "StructureCableStraight", Distance = 3, ColorIndex = metallic,
            });

            var spawnedId = spawn.As<SpawnStructureResponse>()?.ReferenceId;
            if (spawnedId is null or 0)
            {
                ctx.SetInconclusive(
                    "the target structure did not come back with a reference id, so there is nothing to right-click and nothing was measured about the mod",
                    "scene-not-staged");
            }

            target = spawnedId.Value;

            // The fixture, read back from the authority. A structure that did not
            // actually take the metallic swatch would send the click down the
            // same-family path and print nothing.
            ctx.AssertValue("hostie", Reader.Thing, ValueMatcher.Is(metallic),
                because: "the whole check is a cross-family pick, so the target has to be carrying a metallic swatch; a target in the base family is a pick the rule correctly permits in silence",
                select: "value", of: $"{Id(target)}/CustomColor.Index",
                readerArgs: new ThingRequest { RefIds = Id(target), Fields = "CustomColor.Index" });

            // ---- 4. A BASE can in the host's hand. Blue is swatch 0, family
            // DLCType.None, which is the other side of the boundary.
            ctx.Act("hostie", Endpoints.InventoryArm, new InventoryArmRequest
            {
                Prefab = "ItemSprayCanBlue", Hand = "activeHand", Replace = true,
            });

            // ---- 5. Put the cursor on the target. HandleEyedropper reads
            // CursorManager.CursorThing, so nothing else will do, and aiming a
            // driven client by look angle has already been tried and does not
            // land. /cursor/force pins the collider alongside the target and
            // refuses a target it cannot find one for.
            ctx.Act("hostie", Endpoints.CursorForce, new CursorForceRequest { TargetId = target });
            cursorSet = true;

            var seq0 = Seq(ctx.Read("hostie", Reader.Console, "nextSeq", readerArgs: new ConsoleLogRequest { Limit = 1 }));

            // ---- 6. One right-click. requireConsumed defaults to true, so an
            // input the game never read answers 409 and ends this check as
            // inconclusive rather than as a missing message.
            ctx.Act("hostie", Endpoints.InputMouse, new InputMouseRequest { Button = 1, Mode = "tap", Frames = 3 });
            ctx.Wait(2);

            ctx.AssertValue("hostie", Reader.Console, ValueMatcher.Is(1),
                because: "a deliberate right-click at a colour the rule refuses must be answered, once, with the display-name prefix the shared PlayerMessage helper supplies; silence in reply to a deliberate action reads as the mod being broken",
                select: "count",
                readerArgs: new ConsoleLogRequest { Since = seq0, Source = "console", Contains = $"[Spray Paint Plus] {familyLine}", Limit = 200 });

            ctx.AssertValue("hostie", Reader.Console, ValueMatcher.AtLeast(1),
                because: "the migration onto PlayerMessage put this line in the BepInEx log as well as the console, which is what makes it survive in a bug report; before the migration it existed only on screen",
                select: "count",
                readerArgs: new ConsoleLogRequest { Since = seq0, Source = "bepinex", Contains = familyLine, Limit = 200 });

            // ---- 7. Second click, same target: no cap. This is the assertion
            // that pins Throttle.Never at this call site.
            ctx.Act("hostie", Endpoints.InputMouse, new InputMouseRequest { Button = 1, Mode = "tap", Frames = 3 });
            ctx.Wait(2);

            ctx.AssertValue("hostie", Reader.Console, ValueMatcher.Is(2),
                because: "the family rule answers a deliberate action every single time and is exempt from the three-per-session cap that bounds the blocked-function notices; a second click answered with silence would be the caller getting nothing back from a rule that is still enforcing",
                select: "count",
                readerArgs: new ConsoleLogRequest { Since = seq0, Source = "console", Contains = $"[Spray Paint Plus] {familyLine}", Limit = 200 });

            // ---- 8. The rule is a restriction, not a paint: the can must not
            // have taken the colour it was refused.
            ctx.AssertValue("hostie", Reader.Thing, ValueMatcher.Is(metallic),
                because: "the target is only ever read by an eyedropper, so its colour must be untouched; a changed value would mean the right-click painted instead of picking",
                select: "value", of: $"{Id(target)}/CustomColor.Index",
                readerArgs: new ThingRequest { RefIds = Id(target), Fields = "CustomColor.Index" });
        }
        finally
        {
            // ---- Clean up. The forced cursor first and unconditionally: a
            // driven client left with a pinned cursor is the one piece of state
            // here that outlives the check in a way that matters.
            if (cursorSet)
            {
                Quietly(() => ctx.Act("hostie", Endpoints.CursorForce, new CursorForceRequest { Clear = true }, noRetry: true));
            }

            if (target != 0)
            {
                Quietly(() => ctx.Act("hostie", Endpoints.ConsoleExec, new ConsoleExecRequest { Command = $"thing delete {Id(target)}" }, noRetry: true));
            }

            foreach (var pair in new[]
            {
                ("Client - Color Cycling", "Color Cycling", "AllColors"),
                ("Server - Color Cycling", "Color Cycling", "AllColors"),
            })
            {
                Quietly(() => ctx.Act("hostie", Endpoints.ConfigSet, new ConfigSetRequest
                {
                    Guid = ModGuid, Section = pair.Item1, Key = pair.Item2, Value = pair.Item3, Save = false,
                }, noRetry: true));
            }
        }
    }
}

internal static class EyedropperCrossFamilyLineRegistration
{
    [ModuleInitializer]
    internal static void Register() => PlaytestCheckRegistry.Register(new EyedropperCrossFamilyLine());
}
