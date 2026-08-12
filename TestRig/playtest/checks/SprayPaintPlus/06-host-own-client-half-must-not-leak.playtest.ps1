# =============================================================================
# Spray Paint Plus: the host's own client half must not leak onto a joiner
# =============================================================================
# From Mods/SprayPaintPlus/PLAYTEST.md, Session A: "Host turns Client - Glow
# Paint Off but leaves Server - Glow Paint On. The client's gun must keep working
# normally, and the host sees the result. Before the fix the host's personal
# setting silently disabled glow for everyone. The host's own gun stays inert,
# which is what their own half asked for."
#
# ONLY A REMOTE ACTOR DISCRIMINATES THE FIXED CODE FROM THE OLD CODE. With the
# host swinging, both versions block the stroke, because the host's own half is
# what was turned off. So the joiner has to hold a spray gun, and until August
# 2026 the rig could not put an item into a remote client's hand: /spawn/hand
# needs simulation authority and refuses on a joiner, /spawn/world viaServer=true
# drops the item on the ground, and picking it up is cursor-driven onto a slot
# collider. That is why this check sat blocked.
#
# WHAT THIS CHECK DEPENDS ON THAT IS NOT YET PROVEN LIVE
# POST /inventory/arm, which claims to work on any role, joiner included: it
# spawns through the server, waits for the Thing to arrive, moves it with a
# MoveToSlotMessage and answers 200 only when the hand actually holds it. If that
# claim does not hold on a joiner, this check ends inconclusive at the arm call
# and never accuses the mod. Nothing else here is new.
#
# WHERE THE ASSERTIONS ARE READ
# All of them on the HOST, which runs the simulation. A joiner claiming its own
# gun worked proves only that the joiner thinks so. GET /thing carries a
# location block with an authoritative flag (GameManager.RunSimulation), and this
# check asserts that flag before it believes anything else it reads there.
#
# WHY EmissionColor NEEDS A BASELINE AND NOT A SINGLE READING
# Thing.EmissionColor initialises to Color.white, so an object that has never
# been painted reads (1,1,1,1) and looks like it is glowing; matchesPrefab is
# therefore TRUE for a genuinely glowing object and useless as evidence here. The
# answer is a baseline: both cables are painted with a plain can first, which
# runs SetCustomColor with emissive false and puts EmissionColor at (0,0,0,0), a
# value that differs from the prefab and can only have been written.
#
# WHAT WOULD MAKE THIS FAIL
#   - the host's client half leaking back onto the server-side decision: the
#     joiner's stroke would leave the target matte, which is the pre-fix defect;
#   - the host's own half being ignored: the host's own stroke would glow;
#   - glow leaking onto an object nobody aimed at, which the control catches;
#   - the host being told its own stroke was blocked, which it must not be,
#     because a player who switched their own copy off got what they asked for.
#
# AGAINST THE PRE-v1.11.0 BUILD THIS CHECK FAILS on the first assertion: the
# joiner's stroke leaves the target matte because the host's own client half
# gated the server-side decision.
#
# PREREQUISITES
#   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
#   copy bin/Release/SprayPaintPlus.dll into the SprayPaintPlus folder under
#     TestRig/ClientRig/data/hostie/userdata/mods/ and .../joiner/userdata/mods/
# =============================================================================

$sppRepoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

Register-PlaytestCheck `
    -Name 'the host own client half must not leak onto a joiner' `
    -Summary 'a host with its own Glow Paint off must still let a joiner glow-paint, must stay inert itself, and must say nothing about it' `
    -Instances @(
        @{ Name = 'hostie'; Role = 'host';   World = 'Lunar' }
        @{ Name = 'joiner'; Role = 'client'; ConnectTo = 'hostie' }
    ) `
    -Binary @{
        Mod                  = 'net.spraypaintplus'
        ConfigEntryCount     = 33
        ConfigGroupCount     = 9
        DllPath              = (Join-Path $sppRepoRoot 'Mods\SprayPaintPlus\SprayPaintPlus\bin\Release\SprayPaintPlus.dll')
        DeployedRelativePath = 'userdata\mods\SprayPaintPlus\SprayPaintPlus.dll'
    } `
    -Body {
        param($ctx)

        $guid    = 'net.spraypaintplus'
        $spawned = @()

        try {
            # ---- 1. The arrangement that makes this check mean anything: the
            # server half ON, the HOST's own client half OFF, the joiner's client
            # half ON.
            foreach ($pair in @(
                @{ on = 'hostie'; section = 'Server - Glow Paint'; key = 'Glow Paint'; value = 'true'  }
                @{ on = 'hostie'; section = 'Client - Glow Paint'; key = 'Glow Paint'; value = 'false' }
                @{ on = 'joiner'; section = 'Client - Glow Paint'; key = 'Glow Paint'; value = 'true'  }
            )) {
                Invoke-RigAction -On $pair.on -Path '/config/set' -Body @{
                    guid = $guid; section = $pair.section; key = $pair.key; value = $pair.value; save = $false
                } | Out-Null
            }

            Assert-RigValue -From 'hostie' -Reader config -ReaderArgs @{ guid = $guid } `
                -Of 'Server - Glow Paint/Glow Paint' -Select 'value' -Is $true `
                -Because 'the server half is what decides for everybody, and with it off the joiner would be blocked legitimately, which is not the thing under test'
            Assert-RigValue -From 'hostie' -Reader config -ReaderArgs @{ guid = $guid } `
                -Of 'Client - Glow Paint/Glow Paint' -Select 'value' -Is $false `
                -Because 'the host own client half being OFF is the entire arrangement: with it on, the fixed code and the old code behave identically and the run would prove nothing'
            Assert-RigValue -From 'joiner' -Reader config -ReaderArgs @{ guid = $guid } `
                -Of 'Client - Glow Paint/Glow Paint' -Select 'value' -Is $true `
                -Because 'the acting player half is merged per player on the server, so a joiner with its own half off would be blocked by its own choice'

            # ---- 2. Two cable segments, six metres apart so a network flood
            # cannot reach from one to the other. (Measured separately: cables
            # placed by Constructor.SpawnConstruct never join each other's
            # CableNetwork on this rig at any spacing, so they are independent
            # anyway. The six metres is belt and braces, not the mechanism.)
            #
            # colorIndex 1 (ColorGray) is load bearing. A cable spawned with no
            # colour comes up at customColorIndex 4, which is exactly what
            # ItemSprayCanRed applies, so "did the plain paint land" would be
            # unanswerable: before and after would both read 4. Gray in, red out.
            foreach ($offset in @(0, 6)) {
                $r = Invoke-RigAction -On 'hostie' -Path '/spawn/structure' -Body @{
                    prefab = 'StructureCableStraight'; distance = 3; offset = @($offset, 0, 0); colorIndex = 1
                }
                $id = "$($r.Response.referenceId)"
                if (-not $id -or $id -eq '0') {
                    Set-PlaytestInconclusive -Detector 'scene-not-staged' `
                        -Because 'a cable segment did not come back with a reference id, so there is nothing to paint and nothing was measured about the mod'
                }
                $spawned += $id
            }
            $target  = $spawned[0]
            $control = $spawned[1]

            # ---- 3. Paint both with a plain can from the host, so both carry a
            # real EmissionColor of (0,0,0,0) rather than the prefab's white.
            Invoke-RigAction -On 'hostie' -Path '/inventory/arm' -Body @{
                prefab = 'ItemSprayCanRed'; hand = 'activeHand'; replace = $true
            } | Out-Null
            foreach ($id in $spawned) {
                Invoke-RigAction -On 'hostie' -Path '/player/use' -Body @{ targetId = $id } | Out-Null
                Wait-PlaytestSeconds 1
            }

            # The reading is only worth having if it came from the machine that
            # owns the simulation. This is that check, made explicitly rather
            # than assumed from the instance's name.
            Assert-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $target; fields = 'EmissionColor' } `
                -Of $target -Select 'location.authoritative' -Is $true `
                -Because 'every glow assertion below is read here, and a value read on a machine that does not run the simulation is that machine own view rather than the world state'

            # Did the plain paint land at all? Ask that FIRST, and separately.
            # On 2026-08-11 this check declined with 'baseline-not-matte' and the
            # message guessed at two causes without being able to tell them apart.
            # The colour index answers it outright: gray in, red out means the
            # stroke landed, so anything still wrong with EmissionColor after this
            # point is a fact about EmissionColor rather than a missing stroke.
            $paintLanded = Read-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $target; fields = 'CustomColor' } `
                -Of $target -Select 'customColorIndex'
            if ("$($paintLanded.Value)" -ne '4') {
                Set-PlaytestInconclusive -Detector 'seed-not-painted' `
                    -Because "the target was spawned ColorGray (1) and reads customColorIndex=$($paintLanded.Value) after a plain ItemSprayCanRed stroke, so the stroke never landed and the matte baseline every glow assertion rests on was never established. Nothing was measured about the mod. This is the rig or the scene, not the mod: the prefix on OnServer.SetCustomColor is void and cannot suppress the seed."
            }

            $targetBefore = Read-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $target; fields = 'EmissionColor.r' } `
                -Of "$target/EmissionColor.r" -Select 'value'
            if ("$($targetBefore.Value)" -ne '0') {
                Set-PlaytestInconclusive -Detector 'baseline-not-matte' `
                    -Because "the plain stroke DID land (customColorIndex went 1 to 4) and the target still reads EmissionColor.r=$($targetBefore.Value), so a plain paint does not drive EmissionColor to (0,0,0,0) on a StructureCableStraight the way it does on Piping. Thing.EmissionColor initialises to Color.white, so a later reading of 1 would be indistinguishable from that initial value and the glow assertion cannot be made on this object. Restage the check on a pipe, which is what the 2026-08-09 glow run used."
            }

            # ---- 4. The joiner arms a gun, switches it on, and paints. Holding
            # the gun for a few seconds first is not padding: the acting player's
            # client-half bits reach the server through PaintModifierMessage,
            # which ColorCyclerPatch sends from InventoryManager.NormalMode while
            # a can or gun is in the active hand.
            $arm = Invoke-RigAction -On 'joiner' -Path '/inventory/arm' -Body @{
                prefab = 'ItemSprayGun'; hand = 'activeHand'; replace = $true; timeoutMs = 30000
            }
            $joinerGun = "$($arm.Response.referenceId)"
            Wait-PlaytestSeconds 3

            Invoke-RigAction -On 'joiner' -Path '/input/key' -Body @{ key = 'SecondaryAction'; mode = 'tap'; frames = 3 } | Out-Null
            Wait-PlaytestSeconds 2

            # A gun that is off paints plain colour and would leave the target
            # matte for a reason that is not the mod's.
            $gunOn = Read-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $joinerGun; fields = 'OnOff' } `
                -Of "$joinerGun/OnOff" -Select 'value'
            if ("$($gunOn.Value)" -ne 'True') {
                Set-PlaytestInconclusive -Detector 'tool-not-toggled' `
                    -Because "the host reads the joiner's spray gun as OnOff=$($gunOn.Value), so the right-click toggle did not reach the simulation. A gun that is off applies plain paint, so the stroke below would say nothing about glow."
            }

            $seq0 = Read-RigValue -From 'hostie' -Reader console -ReaderArgs @{ limit = 1 } -Select 'nextSeq'

            Invoke-RigAction -On 'joiner' -Path '/player/use' -Body @{ targetId = $target } | Out-Null
            Wait-PlaytestSeconds 3

            # ---- 5. The assertion this whole check exists for.
            #
            # Written as Assert-RigValue against a captured baseline rather than
            # as Assert-RigChange, and not by choice: Assert-RigChange re-reads
            # through the baseline's reader with no ReaderArgs, and the thing
            # reader needs refIds and fields in the query to answer at all. The
            # baseline discipline is kept by hand instead: it was read above,
            # guarded above, and is named in the text below, so a failure still
            # says what was compared with what.
            Assert-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $target; fields = 'EmissionColor.r' } `
                -Of "$target/EmissionColor.r" -Select 'value' -Is 1 `
                -Because "the target read EmissionColor.r=$($targetBefore.Value) before the joiner's stroke and must read 1 after it: a remote actor whose own half allows glow must be able to apply it on a server whose half allows it, whatever the host has set for ITSELF. Staying at 0 is the pre-v1.11.0 defect where the host personal setting silently disabled glow for everyone"

            $controlAfterJoiner = Read-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $control; fields = 'EmissionColor.r' } `
                -Of "$control/EmissionColor.r" -Select 'value'
            if ("$($controlAfterJoiner.Value)" -ne '0') {
                Set-PlaytestInconclusive -Detector 'control-contaminated' `
                    -Because "the control cable reads EmissionColor.r=$($controlAfterJoiner.Value) before the host has touched it, so the two cables are not independent and the host-side half of this check cannot be measured on it"
            }

            # ---- 6. And the other half: the host's own gun stays inert, and the
            # host is not lectured about a setting it chose itself.
            Invoke-RigAction -On 'hostie' -Path '/inventory/arm' -Body @{
                prefab = 'ItemSprayGun'; hand = 'activeHand'; replace = $true; timeoutMs = 30000
            } | Out-Null
            Wait-PlaytestSeconds 3
            Invoke-RigAction -On 'hostie' -Path '/input/key' -Body @{ key = 'SecondaryAction'; mode = 'tap'; frames = 3 } | Out-Null
            Wait-PlaytestSeconds 2
            Invoke-RigAction -On 'hostie' -Path '/player/use' -Body @{ targetId = $control } | Out-Null
            Wait-PlaytestSeconds 3

            Assert-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $control; fields = 'EmissionColor.r' } `
                -Of "$control/EmissionColor.r" -Select 'value' -Is 0 `
                -Because "the control read EmissionColor.r=$($controlAfterJoiner.Value) before the host's own stroke and must still read 0 after it: the host asked for a gun that does nothing by turning its own half off, and must get exactly that. A glowing control means a client half is decorative"

            Assert-RigValue -From 'hostie' -Reader console `
                -ReaderArgs @{ since = "$($seq0.Value)"; source = 'console'; contains = 'Glow Paint is turned off'; limit = 200 } `
                -Select 'count' -Is 0 `
                -Because 'the blocked-function notice speaks only when the SERVER half is the blocker; here the host own half is, so telling the host that the server refused would be both wrong and confusing'
        }
        finally {
            foreach ($id in $spawned) {
                try { Invoke-RigAction -On 'hostie' -Path '/console/exec' -Body @{ command = "thing delete $id" } -NoRetry | Out-Null }
                catch { }
            }
            try {
                Invoke-RigAction -On 'hostie' -Path '/config/set' -NoRetry -Body @{
                    guid = 'net.spraypaintplus'; section = 'Client - Glow Paint'; key = 'Glow Paint'; value = 'true'; save = $false
                } | Out-Null
            }
            catch { }
        }
    }
