// =============================================================================
// Spray Paint Plus: the host's own client half must not leak onto a joiner
// =============================================================================
// From Mods/SprayPaintPlus/PLAYTEST.md, Session A: "Host turns Client - Glow
// Paint Off but leaves Server - Glow Paint On. The client's gun must keep working
// normally, and the host sees the result. Before the fix the host's personal
// setting silently disabled glow for everyone. The host's own gun stays inert,
// which is what their own half asked for."
//
// ONLY A REMOTE ACTOR DISCRIMINATES THE FIXED CODE FROM THE OLD CODE. With the
// host swinging, both versions block the stroke, because the host's own half is
// what was turned off. So the joiner has to hold a spray gun, and until August
// 2026 the rig could not put an item into a remote client's hand: /spawn/hand
// needs simulation authority and refuses on a joiner, /spawn/world viaServer=true
// drops the item on the ground, and picking it up is cursor-driven onto a slot
// collider. That is why this check sat blocked.
//
// WHAT THIS CHECK DEPENDS ON THAT IS NOT YET PROVEN LIVE
// POST /inventory/arm, which claims to work on any role, joiner included: it
// spawns through the server, waits for the Thing to arrive, moves it with a
// MoveToSlotMessage and answers 200 only when the hand actually holds it. If that
// claim does not hold on a joiner, this check ends inconclusive at the arm call
// and never accuses the mod. Nothing else here is new.
//
// WHERE THE ASSERTIONS ARE READ
// All of them on the HOST, which runs the simulation. A joiner claiming its own
// gun worked proves only that the joiner thinks so. GET /thing carries a
// location block with an authoritative flag (GameManager.RunSimulation), and this
// check asserts that flag before it believes anything else it reads there.
//
// WHY EmissionColor NEEDS A BASELINE AND NOT A SINGLE READING
// Thing.EmissionColor initialises to Color.white, so an object that has never
// been painted reads (1,1,1,1) and looks like it is glowing; matchesPrefab is
// therefore TRUE for a genuinely glowing object and useless as evidence here. The
// answer is a baseline: both cables are painted with a plain can first, which
// runs SetCustomColor with emissive false and puts EmissionColor at (0,0,0,0), a
// value that differs from the prefab and can only have been written.
//
// WHAT WOULD MAKE THIS FAIL
//   - the host's client half leaking back onto the server-side decision: the
//     joiner's stroke would leave the target matte, which is the pre-fix defect;
//   - the host's own half being ignored: the host's own stroke would glow;
//   - glow leaking onto an object nobody aimed at, which the control catches;
//   - the host being told its own stroke was blocked, which it must not be,
//     because a player who switched their own copy off got what they asked for.
//
// AGAINST THE PRE-v1.11.0 BUILD THIS CHECK FAILS on the first assertion: the
// joiner's stroke leaves the target matte because the host's own client half
// gated the server-side decision.
//
// PREREQUISITES
//   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
//   copy bin/Release/SprayPaintPlus.dll into the SprayPaintPlus folder under
//     TestRig/ClientRig/data/hostie/userdata/mods/ and .../joiner/userdata/mods/
// =============================================================================

using System.Runtime.CompilerServices;
using TestRig.Contracts;
using TestRig.Playtest;
using TestRig.Playtest.Model;
using TestRig.Playtest.Values;
using static SprayPaintPlus.Playtests.Spp;

namespace SprayPaintPlus.Playtests;

internal sealed class HostOwnClientHalfMustNotLeak : IPlaytestCheck
{
    public CheckSpec Spec { get; } = new(
        name: "the host own client half must not leak onto a joiner",
        summary: "a host with its own Glow Paint off must still let a joiner glow-paint, must stay inert itself, and must say nothing about it",
        instances:
        [
            new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar"),
            new InstanceSpec("joiner", InstanceRole.Client, ConnectTo: "hostie"),
        ]);

    public void Run(IPlaytestContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        const string guid = ModGuid;
        var spawned = new List<long>();

        try
        {
            // ---- 1. The arrangement that makes this check mean anything: the
            // server half ON, the HOST's own client half OFF, the joiner's client
            // half ON.
            foreach (var pair in new[]
            {
                ("hostie", "Server - Glow Paint", "Glow Paint", "true"),
                ("hostie", "Client - Glow Paint", "Glow Paint", "false"),
                ("joiner", "Client - Glow Paint", "Glow Paint", "true"),
            })
            {
                ctx.Act(pair.Item1, Endpoints.ConfigSet, new ConfigSetRequest
                {
                    Guid = guid, Section = pair.Item2, Key = pair.Item3, Value = pair.Item4, Save = false,
                });
            }

            ctx.AssertValue("hostie", Reader.Config, ValueMatcher.Is(true),
                because: "the server half is what decides for everybody, and with it off the joiner would be blocked legitimately, which is not the thing under test",
                select: "value", of: "Server - Glow Paint/Glow Paint",
                readerArgs: new ConfigRequest { Guid = guid });

            ctx.AssertValue("hostie", Reader.Config, ValueMatcher.Is(false),
                because: "the host own client half being OFF is the entire arrangement: with it on, the fixed code and the old code behave identically and the run would prove nothing",
                select: "value", of: "Client - Glow Paint/Glow Paint",
                readerArgs: new ConfigRequest { Guid = guid });

            ctx.AssertValue("joiner", Reader.Config, ValueMatcher.Is(true),
                because: "the acting player half is merged per player on the server, so a joiner with its own half off would be blocked by its own choice",
                select: "value", of: "Client - Glow Paint/Glow Paint",
                readerArgs: new ConfigRequest { Guid = guid });

            // ---- 2. Two cable segments, six metres apart so a network flood
            // cannot reach from one to the other. (Measured separately: cables
            // placed by Constructor.SpawnConstruct never join each other's
            // CableNetwork on this rig at any spacing, so they are independent
            // anyway. The six metres is belt and braces, not the mechanism.)
            //
            // colorIndex 1 (ColorGray) is load bearing. A cable spawned with no
            // colour comes up at customColorIndex 4, which is exactly what
            // ItemSprayCanRed applies, so "did the plain paint land" would be
            // unanswerable: before and after would both read 4. Gray in, red out.
            foreach (var offset in new[] { 0, 6 })
            {
                var spawn = ctx.Act("hostie", Endpoints.SpawnStructure, new SpawnStructureRequest
                {
                    Prefab = "StructureCableStraight", Distance = 3, Offset = [offset, 0, 0], ColorIndex = 1,
                });

                var id = spawn.As<SpawnStructureResponse>()?.ReferenceId;
                if (id is null or 0)
                {
                    ctx.SetInconclusive(
                        "a cable segment did not come back with a reference id, so there is nothing to paint and nothing was measured about the mod",
                        "scene-not-staged");
                }

                spawned.Add(id.Value);
            }

            var target = spawned[0];
            var control = spawned[1];

            // ---- 3. Paint both with a plain can from the host, so both carry a
            // real EmissionColor of (0,0,0,0) rather than the prefab's white.
            ctx.Act("hostie", Endpoints.InventoryArm, new InventoryArmRequest
            {
                Prefab = "ItemSprayCanRed", Hand = "activeHand", Replace = true,
            });

            foreach (var id in spawned)
            {
                ctx.Act("hostie", Endpoints.PlayerUse, new PlayerUseRequest { TargetId = id });
                ctx.Wait(1);
            }

            // The reading is only worth having if it came from the machine that
            // owns the simulation. This is that check, made explicitly rather
            // than assumed from the instance's name.
            ctx.AssertValue("hostie", Reader.Thing, ValueMatcher.Is(true),
                because: "every glow assertion below is read here, and a value read on a machine that does not run the simulation is that machine own view rather than the world state",
                select: "location.authoritative", of: Id(target),
                readerArgs: new ThingRequest { RefIds = Id(target), Fields = "EmissionColor" });

            // Did the plain paint land at all? Ask that FIRST, and separately.
            // On 2026-08-11 this check declined with 'baseline-not-matte' and the
            // message guessed at two causes without being able to tell them apart.
            // The colour index answers it outright: gray in, red out means the
            // stroke landed, so anything still wrong with EmissionColor after this
            // point is a fact about EmissionColor rather than a missing stroke.
            var paintLanded = ctx.Read("hostie", Reader.Thing, "customColorIndex", Id(target),
                new ThingRequest { RefIds = Id(target), Fields = "CustomColor" });

            if (paintLanded.Text != "4")
            {
                ctx.SetInconclusive(
                    $"the target was spawned ColorGray (1) and reads customColorIndex={paintLanded.Text} after a plain ItemSprayCanRed stroke, so the stroke never landed and the matte baseline every glow assertion rests on was never established. Nothing was measured about the mod. This is the rig or the scene, not the mod: the prefix on OnServer.SetCustomColor is void and cannot suppress the seed.",
                    "seed-not-painted");
            }

            var targetBefore = ctx.Read("hostie", Reader.Thing, "value", $"{Id(target)}/EmissionColor.r",
                new ThingRequest { RefIds = Id(target), Fields = "EmissionColor.r" });

            if (targetBefore.Text != "0")
            {
                ctx.SetInconclusive(
                    $"the plain stroke DID land (customColorIndex went 1 to 4) and the target still reads EmissionColor.r={targetBefore.Text}, so a plain paint does not drive EmissionColor to (0,0,0,0) on a StructureCableStraight the way it does on Piping. Thing.EmissionColor initialises to Color.white, so a later reading of 1 would be indistinguishable from that initial value and the glow assertion cannot be made on this object. Restage the check on a pipe, which is what the 2026-08-09 glow run used.",
                    "baseline-not-matte");
            }

            // ---- 4. The joiner arms a gun, switches it on, and paints. Holding
            // the gun for a few seconds first is not padding: the acting player's
            // client-half bits reach the server through PaintModifierMessage,
            // which ColorCyclerPatch sends from InventoryManager.NormalMode while
            // a can or gun is in the active hand.
            var arm = ctx.Act("joiner", Endpoints.InventoryArm, new InventoryArmRequest
            {
                Prefab = "ItemSprayGun", Hand = "activeHand", Replace = true, TimeoutMs = 30000,
            });

            var joinerGun = arm.As<InventoryArmResponse>()?.ReferenceId ?? 0;
            ctx.Wait(3);

            ctx.Act("joiner", Endpoints.InputKey, new InputKeyRequest { Key = "SecondaryAction", Mode = "tap", Frames = 3 });
            ctx.Wait(2);

            // A gun that is off paints plain colour and would leave the target
            // matte for a reason that is not the mod's.
            var gunOn = ctx.Read("hostie", Reader.Thing, "value", $"{Id(joinerGun)}/OnOff",
                new ThingRequest { RefIds = Id(joinerGun), Fields = "OnOff" });

            if (gunOn.Text != "True")
            {
                ctx.SetInconclusive(
                    $"the host reads the joiner's spray gun as OnOff={gunOn.Text}, so the right-click toggle did not reach the simulation. A gun that is off applies plain paint, so the stroke below would say nothing about glow.",
                    "tool-not-toggled");
            }

            var seq0 = Seq(ctx.Read("hostie", Reader.Console, "nextSeq", readerArgs: new ConsoleLogRequest { Limit = 1 }));

            ctx.Act("joiner", Endpoints.PlayerUse, new PlayerUseRequest { TargetId = target });
            ctx.Wait(3);

            // ---- 5. The assertion this whole check exists for.
            //
            // Written as a value assertion against a captured baseline rather
            // than as a change assertion, and not by choice: a change assertion
            // re-reads through the baseline's reader, and while the reader args
            // now travel with the baseline, the message a failure prints is what
            // matters here. The baseline discipline is kept by hand instead: it
            // was read above, guarded above, and is named in the text below, so a
            // failure still says what was compared with what.
            ctx.AssertValue("hostie", Reader.Thing, ValueMatcher.Is(1),
                because: $"the target read EmissionColor.r={targetBefore.Text} before the joiner's stroke and must read 1 after it: a remote actor whose own half allows glow must be able to apply it on a server whose half allows it, whatever the host has set for ITSELF. Staying at 0 is the pre-v1.11.0 defect where the host personal setting silently disabled glow for everyone",
                select: "value", of: $"{Id(target)}/EmissionColor.r",
                readerArgs: new ThingRequest { RefIds = Id(target), Fields = "EmissionColor.r" });

            var controlAfterJoiner = ctx.Read("hostie", Reader.Thing, "value", $"{Id(control)}/EmissionColor.r",
                new ThingRequest { RefIds = Id(control), Fields = "EmissionColor.r" });

            if (controlAfterJoiner.Text != "0")
            {
                ctx.SetInconclusive(
                    $"the control cable reads EmissionColor.r={controlAfterJoiner.Text} before the host has touched it, so the two cables are not independent and the host-side half of this check cannot be measured on it",
                    "control-contaminated");
            }

            // ---- 6. And the other half: the host's own gun stays inert, and the
            // host is not lectured about a setting it chose itself.
            ctx.Act("hostie", Endpoints.InventoryArm, new InventoryArmRequest
            {
                Prefab = "ItemSprayGun", Hand = "activeHand", Replace = true, TimeoutMs = 30000,
            });
            ctx.Wait(3);
            ctx.Act("hostie", Endpoints.InputKey, new InputKeyRequest { Key = "SecondaryAction", Mode = "tap", Frames = 3 });
            ctx.Wait(2);
            ctx.Act("hostie", Endpoints.PlayerUse, new PlayerUseRequest { TargetId = control });
            ctx.Wait(3);

            ctx.AssertValue("hostie", Reader.Thing, ValueMatcher.Is(0),
                because: $"the control read EmissionColor.r={controlAfterJoiner.Text} before the host's own stroke and must still read 0 after it: the host asked for a gun that does nothing by turning its own half off, and must get exactly that. A glowing control means a client half is decorative",
                select: "value", of: $"{Id(control)}/EmissionColor.r",
                readerArgs: new ThingRequest { RefIds = Id(control), Fields = "EmissionColor.r" });

            ctx.AssertValue("hostie", Reader.Console, ValueMatcher.Is(0),
                because: "the blocked-function notice speaks only when the SERVER half is the blocker; here the host own half is, so telling the host that the server refused would be both wrong and confusing",
                select: "count",
                readerArgs: new ConsoleLogRequest { Since = seq0, Source = "console", Contains = "Glow Paint is turned off", Limit = 200 });
        }
        finally
        {
            foreach (var id in spawned)
            {
                Quietly(() => ctx.Act("hostie", Endpoints.ConsoleExec, new ConsoleExecRequest { Command = $"thing delete {Id(id)}" }, noRetry: true));
            }

            Quietly(() => ctx.Act("hostie", Endpoints.ConfigSet, new ConfigSetRequest
            {
                Guid = ModGuid, Section = "Client - Glow Paint", Key = "Glow Paint", Value = "true", Save = false,
            }, noRetry: true));
        }
    }
}

internal static class HostOwnClientHalfMustNotLeakRegistration
{
    [ModuleInitializer]
    internal static void Register() => PlaytestCheckRegistry.Register(new HostOwnClientHalfMustNotLeak());
}
