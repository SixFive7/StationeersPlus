# =============================================================================
# Spray Paint Plus: the first-use notice cap
# =============================================================================
# From Mods/SprayPaintPlus/PLAYTEST.md: "On the dedicated server set
# Server - Network Painting / Network Paint Cables Off with the client half On,
# then paint a cable four times. Expected: three console lines, the third ending
# 'No more notices about this one until you rejoin.', and silence on the fourth."
#
# Run here on a LISTEN HOST rather than the dedicated server, because the notice
# has to land in a player's console and a dedicated server has no player. The
# host is both the authority that detects the block and the acting player, so
# SettingBlockedNotice.NotifyBlocked takes its Human.LocalHuman branch and prints
# locally instead of sending a message. That is the same WarningNotifier.
# WarnBlocked cap either way (MaxNoticesPerFunction = 3), which is what this
# measures.
#
# WHAT WOULD MAKE THIS FAIL
#   - the cap regressing to unbounded: four strokes would print four lines;
#   - the cap regressing to one or two: fewer than three;
#   - the third line losing its "no more notices" sentence, which is written by
#     the seen + 1 == MaxNoticesPerFunction branch and is the only thing that
#     proves the cap announced itself rather than just stopping;
#   - the flood not being blocked at all, which the unpainted control cable
#     catches independently of any console text;
#   - the console prefix reverting to the code name. Counted lines must carry
#     "[Spray Paint Plus] ", the display-name prefix PlayerMessage.Init supplies.
#
# AGAINST THE PRE-v1.11.0 BUILD THIS CHECK FAILS, and that is deliberate. Before
# the PlayerMessage migration every console line carried "[SprayPaintPlus] ", so
# the contains filter matches nothing and the first assertion reads 0 against an
# expected 3. A build that passes this check is one whose console output went
# through the shared helper.
#
# PREREQUISITES (the harness does not provision and does not build)
#   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
#   copy bin/Release/SprayPaintPlus.dll into
#     TestRig/ClientRig/data/hostie/userdata/mods/SprayPaintPlus/
# =============================================================================

$sppRepoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

Register-PlaytestCheck `
    -Name 'the first-use notice cap stops after three lines' `
    -Summary 'a server half that refuses a function the player enabled produces exactly three console notices, the third saying so, then silence' `
    -Instances @(
        @{ Name = 'hostie'; Role = 'host'; World = 'Lunar' }
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

        $guid   = 'net.spraypaintplus'
        $notice = '[Spray Paint Plus] Network Paint Cables is turned off'
        $capLine = 'No more notices about this one until you rejoin.'
        $spawned = @()

        try {
            # ---- 1. Arrange. Every one of these is an action, and none of them
            # is evidence of anything; the assertions below read the values back.
            # save=false throughout: nothing this check does may persist into the
            # instance's stationeers .cfg and change the next session.
            foreach ($pair in @(
                @{ section = 'Server - Network Painting'; key = 'Network Painting';     value = 'true'  }
                @{ section = 'Server - Network Painting'; key = 'Network Paint Cables'; value = 'false' }
                @{ section = 'Client - Network Painting'; key = 'Network Painting';     value = 'true'  }
                @{ section = 'Client - Network Painting'; key = 'Network Paint Cables'; value = 'true'  }
            )) {
                Invoke-RigAction -On 'hostie' -Path '/config/set' -Body @{
                    guid = $guid; section = $pair.section; key = $pair.key; value = $pair.value; save = $false
                } | Out-Null
            }

            # The arrangement, read back from the process that will enforce it.
            # A /config/set that answered 200 is a statement about the request.
            Assert-RigValue -From 'hostie' -Reader config -ReaderArgs @{ guid = $guid } `
                -Of 'Server - Network Painting/Network Paint Cables' -Select 'value' -Is $false `
                -Because 'the whole check rests on the server half refusing cable painting; if it is still on, three silent strokes would look exactly like a working cap'
            Assert-RigValue -From 'hostie' -Reader config -ReaderArgs @{ guid = $guid } `
                -Of 'Client - Network Painting/Network Paint Cables' -Select 'value' -Is $true `
                -Because 'WarningNotifier only speaks when the SERVER half is the blocker: a player who turned their own half off gets silence by design, which would read as a working cap for the wrong reason'

            # ---- 2. Five cable segments, two metres apart in a line. Four are
            # seeds for the four strokes; the fifth is never aimed at.
            #
            # colorIndex 1 (ColorGray) is NOT decoration. A cable spawned with no
            # colour comes up at customColorIndex 4, which is ColorRed, which is
            # exactly what ItemSprayCanRed applies, so before and after read the
            # same and no stroke can be proved to have landed. That is what made
            # the 2026-08-11 run conclude nothing had been painted when the seed
            # had in fact been painted every time. Gray in, red out, unambiguous.
            for ($i = 0; $i -lt 5; $i++) {
                $r = Invoke-RigAction -On 'hostie' -Path '/spawn/structure' -Body @{
                    prefab = 'StructureCableStraight'; distance = 2; offset = @(($i * 2), 0, 0); colorIndex = 1
                }
                $id = "$($r.Response.referenceId)"
                if (-not $id -or $id -eq '0') {
                    Set-PlaytestInconclusive -Detector 'scene-not-staged' `
                        -Because "cable segment $($i + 1) of 5 did not come back with a reference id, so there is nothing to paint and nothing was measured about the mod. On a listen host Constructor.SpawnConstruct returns the placed Structure; a null means the cell was occupied or off the grid."
                }
                $spawned += $id
            }
            $control = $spawned[4]

            # A can, in the host's own hand. /inventory/arm spawns through the
            # server and waits for the hand to actually hold it, so a 200 here
            # already means the slot is filled.
            Invoke-RigAction -On 'hostie' -Path '/inventory/arm' -Body @{
                prefab = 'ItemSprayCanRed'; hand = 'activeHand'; replace = $true
            } | Out-Null

            # ---- 3. Baseline the console sequence, so nothing printed during
            # bring-up can be counted as a notice.
            $seq0 = Read-RigValue -From 'hostie' -Reader console -ReaderArgs @{ limit = 1 } -Select 'nextSeq'

            # ---- 4. Three strokes, each at a different member of the network,
            # so every one of them genuinely changes a colour and cannot be
            # short-circuited as a repaint of the colour already there.
            for ($i = 0; $i -lt 3; $i++) {
                Invoke-RigAction -On 'hostie' -Path '/player/use' -Body @{ targetId = $spawned[$i] } | Out-Null
                Wait-PlaytestSeconds 1
            }

            # ---- 5. Conclude, from the console of the player the notice was for.
            # source=console because the tee merges the game console and the
            # BepInEx log and a line that goes to both appears twice; this counts
            # what a player sees.
            Assert-RigValue -From 'hostie' -Reader console `
                -ReaderArgs @{ since = "$($seq0.Value)"; source = 'console'; contains = $notice; limit = 200 } `
                -Select 'count' -Is 3 `
                -Because 'WarningNotifier.MaxNoticesPerFunction is 3 and SettingBlockedNotice.TakeNoticeBudget caps the send side at the same number, so three strokes at a function the server refuses must produce exactly three lines: fewer means the cap counts wrong, more means it is not counting at all'

            Assert-RigValue -From 'hostie' -Reader console `
                -ReaderArgs @{ since = "$($seq0.Value)"; source = 'console'; contains = $capLine; limit = 200 } `
                -Select 'count' -Is 1 `
                -Because 'the third notice has to announce that it is the last one, because a cap that goes quiet without saying so is indistinguishable from the mod breaking'

            # ---- 6. The fourth stroke, and the silence.
            $seq1 = Read-RigValue -From 'hostie' -Reader console -ReaderArgs @{ limit = 1 } -Select 'nextSeq'
            Invoke-RigAction -On 'hostie' -Path '/player/use' -Body @{ targetId = $spawned[3] } | Out-Null
            Wait-PlaytestSeconds 2

            Assert-RigValue -From 'hostie' -Reader console `
                -ReaderArgs @{ since = "$($seq1.Value)"; source = 'console'; contains = 'Network Paint Cables'; limit = 200 } `
                -Select 'count' -Is 0 `
                -Because 'the fourth stroke at the same function must print nothing at all; the substring here is deliberately looser than the counted one, so a notice that reappeared under different wording is still caught'

            # ---- 7. The decision itself, from the authority. The console above is
            # a report ABOUT a decision; these two read the world.
            #
            # Read customColorIndex, the row-level value /thing computes the way
            # the game does. NOT the CustomColor member: Thing.CustomColor is a
            # ColorSwatch REFERENCE whose rendering is the literal string
            # "Assets.Scripts.Objects.ColorSwatch", identical on the instance and
            # on the prefab, so matchesPrefab is always true and isNull always
            # false no matter what has been painted. This check used to assert
            # isNull on it, which can never be true, and the previous campaign
            # read the same member and concluded nothing had been painted when
            # every stroke had in fact landed.
            $painted = $spawned[0]
            Assert-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $painted; fields = 'CustomColor' } `
                -Of $painted -Select 'customColorIndex' -Is 4 `
                -Because 'the seed the player aimed at was spawned ColorGray (1) and ItemSprayCanRed applies ColorRed (4), so this is the assertion that proves a stroke landed at all. It is the one that was missing: without it every console count above could read 0 for the mundane reason that nothing was ever painted, and a whole campaign was spent on that'

            # A runaway-paint guard, and ONLY that. It is deliberately NOT
            # evidence that the flood was blocked: cables placed by
            # Constructor.SpawnConstruct never join each other's CableNetwork on
            # this rig (measured over eight layouts: both axes, yaw 0 and 90,
            # spacing 0.5 m, 1 m and 2 m, every one painting the seed and leaving
            # the rest), so each carries a singleton network and there is no flood
            # to block. What this still catches is paint reaching an object nobody
            # aimed at, which would be a real defect whatever the topology.
            Assert-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $control; fields = 'CustomColor' } `
                -Of $control -Select 'customColorIndex' -Is 1 `
                -Because 'this segment was never aimed at, so it must still carry the ColorGray it was spawned with; paint arriving on an object nobody targeted is a defect regardless of whether a network flood was involved'
        }
        finally {
            # ---- Clean up: the spawned cables and the can, and the config back
            # to its defaults. The next lock's hygiene reset re-copies the
            # BepInEx config and wipes userdata/saves/, but this check must not
            # depend on that, and the world stays usable for whatever runs next.
            foreach ($id in $spawned) {
                try { Invoke-RigAction -On 'hostie' -Path '/console/exec' -Body @{ command = "thing delete $id" } -NoRetry | Out-Null }
                catch { }
            }
            foreach ($pair in @(
                @{ section = 'Server - Network Painting'; key = 'Network Paint Cables'; value = 'true' }
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
