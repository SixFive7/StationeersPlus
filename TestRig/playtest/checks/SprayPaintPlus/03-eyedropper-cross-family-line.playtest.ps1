# =============================================================================
# Spray Paint Plus: the eyedropper explains a cross-family pick, every time
# =============================================================================
# From Mods/SprayPaintPlus/PLAYTEST.md: "With 'Cycles within paint family' in
# force, right-click a metallic-painted object holding a base can. Expected: the
# same explanation as before, once per click with no cap, and now also present
# in BepInEx\LogOutput.log at Info level, which it never used to be."
#
# ColorCyclerPatch.HandleEyedropper reaches the family branch only after four
# earlier gates pass: no shift held, the cursor is on a paintable Thing, the
# picked colour is ENTITLED, and it differs from the one on the can. So the
# arrangement below is not decoration, and every part of it is guarded rather
# than assumed.
#
# WHY A JOINER IS IN THIS CHECK AND NEVER TOUCHED
# DlcPaintGate.IsColorAllowed delegates to SharedDLCManager.CheckSharedAccess,
# which reads the session POOL and nothing else, and the new-world path never
# seeds that pool: a freshly created world starts empty even on an install that
# owns Metallic Paints (Research/GameSystems/DLCGating.md, "Single player: new
# world versus loaded world"). A joined client contributes its own entitlement
# at the very end of its join, which is what puts MetallicPaints in the pool
# here. Without it the eyedropper returns at the entitlement gate, prints
# nothing, and this check would fail for a reason that has nothing to do with
# paint families. The pool is read back and guarded before anything is clicked.
#
# WHAT WOULD MAKE THIS FAIL
#   - the family rule going quiet: a cross-family pick that answers with silence
#     reads to a player as the mod being broken;
#   - the line acquiring a throttle: the second click would print nothing, and
#     Throttle.Never on this call site is a deliberate decision (a second click
#     at a different object answering with silence is the failure it avoids);
#   - the line reaching the console but not the BepInEx log, which is exactly
#     what the PlayerMessage migration changed;
#   - the console prefix reverting to the code name.
#
# AGAINST THE PRE-v1.11.0 BUILD THIS CHECK FAILS TWICE OVER: the console prefix
# was "[SprayPaintPlus] ", and the line did not reach the BepInEx log at all.
#
# PREREQUISITES
#   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
#   copy bin/Release/SprayPaintPlus.dll into the SprayPaintPlus folder under
#     TestRig/ClientRig/data/hostie/userdata/mods/ and .../joiner/userdata/mods/
#   the Steam session must own Metallic Paints; the check declines otherwise
# =============================================================================

$sppRepoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

Register-PlaytestCheck `
    -Name 'the eyedropper explains a cross-family pick once per click' `
    -Summary 'under Cycles within paint family, right-clicking metallic paint with a base can answers every time, on the console and in the log' `
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

        $guid       = 'net.spraypaintplus'
        $metallic   = 12          # ColorObsidian, the first Metallic Paints swatch
        $familyLine = 'limited to one paint family'
        $target     = ''
        $cursorSet  = $false

        try {
            # ---- 1. Entitlement, before anything else. This is a precondition
            # of the world, not a claim about the mod, so a session without it
            # declines rather than failing.
            $pool = Read-RigValue -From 'hostie' -Reader dlc -Select 'state.shared'
            if ("$($pool.Value)" -notmatch 'MetallicPaints') {
                Set-PlaytestInconclusive -Detector 'entitlement-not-in-pool' `
                    -Because "the host's shared DLC pool reads [$($pool.Value)] and does not carry MetallicPaints, so DlcPaintGate.IsColorAllowed refuses every metallic swatch and the eyedropper returns before it can reach the family rule. Either this Steam session does not own Metallic Paints, or the joiner's AvailableDLCMessage did not land. Nothing was measured about the mod."
            }

            # ---- 2. Both halves of the cycling mode, and both halves of colour
            # picking. EffectiveColorCycling is the stricter of the two halves,
            # and EffectiveColorPicking folds in the mode, so a wrong value here
            # sends the click down a different branch entirely.
            foreach ($pair in @(
                @{ section = 'Client - Color Cycling'; key = 'Color Cycling'; value = 'WithinFamily' }
                @{ section = 'Server - Color Cycling'; key = 'Color Cycling'; value = 'WithinFamily' }
                @{ section = 'Client - Color Cycling'; key = 'Color Picking'; value = 'true' }
                @{ section = 'Server - Color Cycling'; key = 'Color Picking'; value = 'true' }
            )) {
                Invoke-RigAction -On 'hostie' -Path '/config/set' -Body @{
                    guid = $guid; section = $pair.section; key = $pair.key; value = $pair.value; save = $false
                } | Out-Null
            }

            Assert-RigValue -From 'hostie' -Reader config -ReaderArgs @{ guid = $guid } `
                -Of 'Server - Color Cycling/Color Cycling' -Select 'value' -Is 'WithinFamily' `
                -Because 'the family rule only exists under this mode; under AllColors the pick is simply allowed and the line is correctly absent, which would read as the message going missing'
            Assert-RigValue -From 'hostie' -Reader config -ReaderArgs @{ guid = $guid } `
                -Of 'Client - Color Cycling/Color Picking' -Select 'value' -Is $true `
                -Because 'with picking off the click is answered by the blocked-function notice instead, which is a different message with a three-per-session cap'

            # ---- 3. A metallic-painted object to aim at. Spawning it already
            # painted avoids needing a metallic can in hand, which would only add
            # a second DLC-gated step.
            $spawn = Invoke-RigAction -On 'hostie' -Path '/spawn/structure' -Body @{
                prefab = 'StructureCableStraight'; distance = 3; colorIndex = $metallic
            }
            $target = "$($spawn.Response.referenceId)"
            if (-not $target -or $target -eq '0') {
                Set-PlaytestInconclusive -Detector 'scene-not-staged' `
                    -Because 'the target structure did not come back with a reference id, so there is nothing to right-click and nothing was measured about the mod'
            }

            # The fixture, read back from the authority. A structure that did not
            # actually take the metallic swatch would send the click down the
            # same-family path and print nothing.
            Assert-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $target; fields = 'CustomColor.Index' } `
                -Of "$target/CustomColor.Index" -Select 'value' -Is $metallic `
                -Because 'the whole check is a cross-family pick, so the target has to be carrying a metallic swatch; a target in the base family is a pick the rule correctly permits in silence'

            # ---- 4. A BASE can in the host's hand. Blue is swatch 0, family
            # DLCType.None, which is the other side of the boundary.
            Invoke-RigAction -On 'hostie' -Path '/inventory/arm' -Body @{
                prefab = 'ItemSprayCanBlue'; hand = 'activeHand'; replace = $true
            } | Out-Null

            # ---- 5. Put the cursor on the target. HandleEyedropper reads
            # CursorManager.CursorThing, so nothing else will do, and aiming a
            # driven client by look angle has already been tried and does not
            # land. /cursor/force pins the collider alongside the target and
            # refuses a target it cannot find one for.
            Invoke-RigAction -On 'hostie' -Path '/cursor/force' -Body @{ targetId = $target } | Out-Null
            $cursorSet = $true

            $seq0 = Read-RigValue -From 'hostie' -Reader console -ReaderArgs @{ limit = 1 } -Select 'nextSeq'

            # ---- 6. One right-click. requireConsumed defaults to true, so an
            # input the game never read answers 409 and ends this check as
            # inconclusive rather than as a missing message.
            Invoke-RigAction -On 'hostie' -Path '/input/mouse' -Body @{ button = 1; mode = 'tap'; frames = 3 } | Out-Null
            Wait-PlaytestSeconds 2

            Assert-RigValue -From 'hostie' -Reader console `
                -ReaderArgs @{ since = "$($seq0.Value)"; source = 'console'; contains = "[Spray Paint Plus] $familyLine"; limit = 200 } `
                -Select 'count' -Is 1 `
                -Because 'a deliberate right-click at a colour the rule refuses must be answered, once, with the display-name prefix the shared PlayerMessage helper supplies; silence in reply to a deliberate action reads as the mod being broken'

            Assert-RigValue -From 'hostie' -Reader console `
                -ReaderArgs @{ since = "$($seq0.Value)"; source = 'bepinex'; contains = $familyLine; limit = 200 } `
                -Select 'count' -AtLeast 1 `
                -Because 'the migration onto PlayerMessage put this line in the BepInEx log as well as the console, which is what makes it survive in a bug report; before the migration it existed only on screen'

            # ---- 7. Second click, same target: no cap. This is the assertion
            # that pins Throttle.Never at this call site.
            Invoke-RigAction -On 'hostie' -Path '/input/mouse' -Body @{ button = 1; mode = 'tap'; frames = 3 } | Out-Null
            Wait-PlaytestSeconds 2

            Assert-RigValue -From 'hostie' -Reader console `
                -ReaderArgs @{ since = "$($seq0.Value)"; source = 'console'; contains = "[Spray Paint Plus] $familyLine"; limit = 200 } `
                -Select 'count' -Is 2 `
                -Because 'the family rule answers a deliberate action every single time and is exempt from the three-per-session cap that bounds the blocked-function notices; a second click answered with silence would be the caller getting nothing back from a rule that is still enforcing'

            # ---- 8. The rule is a restriction, not a paint: the can must not
            # have taken the colour it was refused.
            Assert-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $target; fields = 'CustomColor.Index' } `
                -Of "$target/CustomColor.Index" -Select 'value' -Is $metallic `
                -Because 'the target is only ever read by an eyedropper, so its colour must be untouched; a changed value would mean the right-click painted instead of picking'
        }
        finally {
            # ---- Clean up. The forced cursor first and unconditionally: a
            # driven client left with a pinned cursor is the one piece of state
            # here that outlives the check in a way that matters.
            if ($cursorSet) {
                try { Invoke-RigAction -On 'hostie' -Path '/cursor/force' -Body @{ clear = $true } -NoRetry | Out-Null } catch { }
            }
            if ($target) {
                try { Invoke-RigAction -On 'hostie' -Path '/console/exec' -Body @{ command = "thing delete $target" } -NoRetry | Out-Null } catch { }
            }
            foreach ($pair in @(
                @{ section = 'Client - Color Cycling'; key = 'Color Cycling'; value = 'AllColors' }
                @{ section = 'Server - Color Cycling'; key = 'Color Cycling'; value = 'AllColors' }
            )) {
                try {
                    Invoke-RigAction -On 'hostie' -Path '/config/set' -NoRetry -Body @{
                        guid = 'net.spraypaintplus'; section = $pair.section; key = $pair.key; value = $pair.value; save = $false
                    } | Out-Null
                }
                catch { }
            }
        }
    }
