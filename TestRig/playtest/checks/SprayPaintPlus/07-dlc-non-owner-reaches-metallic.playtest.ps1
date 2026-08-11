# =============================================================================
# Spray Paint Plus: a non-owner reaches metallic while an owner is connected
# =============================================================================
# From Mods/SprayPaintPlus/PLAYTEST.md, Session B: "The non-owning player must be
# able to scroll to and paint with metallic colors while the owner is connected.
# That is vanilla shared-DLC behavior, not a bypass."
#
# THE NON-OWNER HERE IS THE HOST, AND THAT IS FORCED BY THE GAME, not a
# preference. DlcPaintGate.IsColorAllowed delegates to
# SharedDLCManager.CheckSharedAccess, which reads the session POOL and nothing
# else. Only two things ever fill that pool: HostFinishedLoad, on the world LOAD
# path, and ClientFinishedLoad, at the very end of each client's join. The
# new-world path never seeds it at all. So on a freshly created world the only
# way an entitlement can be in the pool is for a JOINING CLIENT to contribute it,
# which makes the joiner the owner and the host the non-owner.
# Research/GameSystems/DLCGating.md, "Single player: new world versus loaded
# world" and "Dedicated server behavior".
#
# SEQUENCING IS LOAD BEARING AND EASY TO GET WRONG
# POST /dlc/remove is removal-only and it refuses outright before
# GameManager.IsInitialized. It has to run at the MENU and before POST /host:
# SharedDLCManager.HostFinishedLoad re-seeds the pool from
# DLCManager.GetOwnedDLC() at the end of the load path, so a host stripped after
# its world is up would already have been seeded, and the removal would look
# exactly like one that worked. scope=owned, never shared: shared is the pool
# this check needs the joiner to fill. The endpoint returns the full ordering in
# the sequence array of every /dlc response.
#
# THAT SEQUENCING IS WHY BOTH INSTANCES ARE DECLARED Role='client' WITH NO HOST
# IN THE LIST. The harness brings hosts all the way into their world and connects
# joiners before a check body runs, and the window this check needs is exactly
# between "reached the menu" and "hosts or connects". Declared this way, bring-up
# stops at the menu and the body drives /dlc/remove, /host and /connect in the
# order above. The joiner is declared FIRST so the guaranteed teardown stops it
# before the instance that ends up holding the world.
#
# WHAT THIS CHECK DEPENDS ON THAT IS NOT YET PROVEN LIVE
# POST /dlc/remove itself. It is new, and the whole arrangement rests on it: if
# it refuses, or if the strip does not survive world entry, this check declines
# at the guard rather than accusing the mod. The paint-and-read half is the same
# shape as checks that have already run live.
#
# WHAT WOULD MAKE THIS FAIL
#   - DlcPaintGate refusing a shared entitlement, so the non-owner's scroll skips
#     every metallic swatch and the can never leaves the base family: the painted
#     structure would read a base swatch index;
#   - the gate being bypassed in the other direction is NOT what this check
#     measures. Check 08 covers the pool outliving its owner; a session that owns
#     nothing at all is what the headless spp-dlc-gate-verify scenario covers.
#
# THIS CHECK WOULD ALSO PASS AGAINST THE PRE-v1.11.0 BUILD. The shared-DLC path
# it exercises predates the settings split; it is here because Session B has
# never been run with a real non-owner, not because the migration touched it.
#
# LIVE-RUN RISK WORTH KNOWING: the assertion reads ColorSwatch.Index off the
# painted structure and compares it against the 12-to-15 metallic band from
# Research/GameClasses/ColorSwatch.md. GET /colors reports index and swatchIndex
# as separate numbers, so if a swatch ever carries an Index that is not its
# position in GameManager.CustomColors, this comparison is against the wrong
# numbering and would need /colors consulted first.
#
# PREREQUISITES
#   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
#   copy bin/Release/SprayPaintPlus.dll into the SprayPaintPlus folder under
#     TestRig/ClientRig/data/hostie/userdata/mods/ and .../joiner/userdata/mods/
#   the Steam session must own Metallic Paints; the check declines otherwise
# =============================================================================

$sppRepoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

Register-PlaytestCheck `
    -Name 'a non-owner reaches metallic while the owner is connected' `
    -Summary 'a host stripped of Metallic Paints can still scroll a base can onto a metallic swatch, because a connected owner put the entitlement in the session pool' `
    -Instances @(
        @{ Name = 'joiner'; Role = 'client' }
        @{ Name = 'hostie'; Role = 'client' }
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
        $firstMetallic = 12       # ColorObsidian; 12 to 15 are the Metallic Paints band
        $cable      = ''
        $stripped   = $false

        try {
            # ---- 1. Both instances have to be AT THE MENU, because that is the
            # only window in which entitlement can be removed at all.
            foreach ($name in @('hostie', 'joiner')) {
                $phase = Read-RigValue -From $name -Reader status -Select 'phase'
                if ("$($phase.Value)" -ne 'menu') {
                    Set-PlaytestInconclusive -Detector 'not-at-menu' `
                        -Because "'$name' reports phase=$($phase.Value) and this check has to strip entitlement before world entry: POST /dlc/remove refuses before GameManager.IsInitialized, and a removal after world entry is silently undone by the game's own re-seeding. Nothing was measured about the mod."
                }
            }

            # ---- 2. The entitlement precondition, from each process's own view.
            foreach ($name in @('hostie', 'joiner')) {
                $owned = Read-RigValue -From $name -Reader dlc -Select 'state.owned'
                if ("$($owned.Value)" -notmatch 'MetallicPaints') {
                    Set-PlaytestInconclusive -Detector 'dlc-not-owned' `
                        -Because "'$name' reports owning [$($owned.Value)], which does not include MetallicPaints, so there is no owner to share it and no entitlement to strip. This Steam session does not own the DLC; nothing was measured about the mod."
                }
            }

            # ---- 3. Strip the HOST, at the menu, owned scope only. shared is
            # the pool the joiner is about to fill and must be left alone.
            Invoke-RigAction -On 'hostie' -Path '/dlc/remove' -Body @{ dlc = 'MetallicPaints'; scope = 'owned' } | Out-Null
            $stripped = $true

            Assert-RigValue -From 'hostie' -Reader dlc -Select 'state.removedOwned' -Matches 'MetallicPaints' `
                -Because 'the whole arrangement is a host that does not own the DLC, and the endpoint reports what it actually cleared: an empty removedOwned means the process still owns it and the non-owner in this check is not one'

            # ---- 4. Host the world, THEN bring the owner in. The joiner is the
            # only thing that can put MetallicPaints in the pool of a created
            # world.
            Invoke-RigAction -On 'hostie' -Path '/host' -Body @{ world = 'Lunar' } -Blocking | Out-Null
            Wait-RigStage -Name 'hostie' -Stage 'inWorld' -WaitSeconds 600 | Out-Null

            $hosting = Read-RigValue -From 'hostie' -Reader status -Select 'hosting'
            $role    = Read-RigValue -From 'hostie' -Reader status -Select 'role'
            if ($hosting.Value -ne $true -or "$($role.Value)" -ne 'listenHost') {
                Set-PlaytestInconclusive -Detector 'host-not-hosting' `
                    -Because "the host answered POST /host but reports hosting=$($hosting.Value) role=$($role.Value). NetworkServer.Host() gives up quietly after three failed binds, so the call returning proves nothing and there is nothing for the owner to join."
            }

            # The strip has to have survived world entry. This is the assertion
            # that catches the sequencing mistake the endpoint warns about.
            Assert-RigValue -From 'hostie' -Reader dlc -Select 'state.removedOwned' -Matches 'MetallicPaints' `
                -Because 'DLCManager._ownedDLC is read at world entry by both paths that fill the session pool, so a removal that did not survive it would leave the host quietly owning the DLC and every reading below would be about the wrong arrangement'

            $hostPort = Read-RigValue -From 'hostie' -Reader status -Select 'hostPort'
            Invoke-RigAction -On 'joiner' -Path '/connect' `
                -Body @{ address = '127.0.0.1'; port = [int]$hostPort.Value } -Blocking | Out-Null
            Wait-RigStage -Name 'joiner' -Stage 'inWorld' -WaitSeconds 600 | Out-Null

            $roster = Read-RigValue -From 'hostie' -Reader roster -Select 'count'
            if ([int]$roster.Value -lt 2) {
                Set-PlaytestInconclusive -Detector 'joiner-not-in-roster' `
                    -Because "the host roster carries $($roster.Value) entries (the host counts as one of them), so the owner is not in the session and cannot have contributed anything to the pool"
            }
            Wait-PlaytestSeconds 5

            # ---- 5. The pool, read on the authority. This is vanilla shared-DLC
            # behaviour rather than anything the mod does, so it is guarded and
            # not asserted: an empty pool means the arrangement failed, not that
            # the mod misbehaved.
            $pool = Read-RigValue -From 'hostie' -Reader dlc -Select 'state.shared'
            if ("$($pool.Value)" -notmatch 'MetallicPaints') {
                Set-PlaytestInconclusive -Detector 'entitlement-not-in-pool' `
                    -Because "the host's shared pool reads [$($pool.Value)] with the owner connected, so ClientFinishedLoad's AvailableDLCMessage did not land. Without it there is nothing for a non-owner to inherit and the check would measure an ordinary refusal."
            }

            # ---- 6. Cycling has to be able to leave the base family at all.
            # WithinFamily would pin a base can to the base colours, which is a
            # correct refusal and would read here as the gate blocking a shared
            # entitlement.
            foreach ($pair in @(
                @{ section = 'Client - Color Cycling'; key = 'Color Cycling';                value = 'AllColors' }
                @{ section = 'Server - Color Cycling'; key = 'Color Cycling';                value = 'AllColors' }
                @{ section = 'Client - Preferences';   key = 'Invert Color Scroll Direction'; value = 'false' }
            )) {
                Invoke-RigAction -On 'hostie' -Path '/config/set' -Body @{
                    guid = $guid; section = $pair.section; key = $pair.key; value = $pair.value; save = $false
                } | Out-Null
            }
            Assert-RigValue -From 'hostie' -Reader config -ReaderArgs @{ guid = $guid } `
                -Of 'Client - Preferences/Invert Color Scroll Direction' -Select 'value' -Is $false `
                -Because 'the scroll below counts twelve notches forward from swatch 0, and an inverted wheel would count backwards into the base colours and fail for a reason that has nothing to do with entitlement'

            # ---- 7. The non-owner scrolls a BASE can up into the metallic band.
            # Twelve notches from ColorBlue lands on ColorObsidian when nothing
            # is skipped, and DlcPaintGate is the only thing that skips.
            $spawn = Invoke-RigAction -On 'hostie' -Path '/spawn/structure' -Body @{
                prefab = 'StructureCableStraight'; distance = 3
            }
            $cable = "$($spawn.Response.referenceId)"
            if (-not $cable -or $cable -eq '0') {
                Set-PlaytestInconclusive -Detector 'scene-not-staged' `
                    -Because 'the structure to paint did not come back with a reference id, so the scroll has nothing to prove itself against'
            }

            Invoke-RigAction -On 'hostie' -Path '/inventory/arm' -Body @{
                prefab = 'ItemSprayCanBlue'; hand = 'activeHand'; replace = $true
            } | Out-Null
            Wait-PlaytestSeconds 2
            Invoke-RigAction -On 'hostie' -Path '/input/scroll' -Body @{ notches = 1; repeat = 12; gapFrames = 3 } | Out-Null
            Wait-PlaytestSeconds 2

            # The can's colour lives in a Material and a static dictionary, so it
            # is read where it becomes a number: on the object it paints, on the
            # machine that owns the simulation.
            Invoke-RigAction -On 'hostie' -Path '/player/use' -Body @{ targetId = $cable } | Out-Null
            Wait-PlaytestSeconds 2

            Assert-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $cable; fields = 'CustomColor.Index' } `
                -Of "$cable/CustomColor.Index" -Select 'value' -AtLeast $firstMetallic `
                -Because 'a player who owns nothing must still reach the four Metallic Paints swatches while an owner is in the session, because DlcPaintGate asks SharedDLCManager.CheckSharedAccess and that reads the session pool; a base swatch index here means the scroll skipped every metallic colour and the shared entitlement was ignored'
        }
        finally {
            if ($cable) {
                try { Invoke-RigAction -On 'hostie' -Path '/console/exec' -Body @{ command = "thing delete $cable" } -NoRetry | Out-Null } catch { }
            }
            # Put the host's own entitlement back. It is per process and in
            # memory only, so it would go anyway when the process ends, but a
            # check that leaves a stripped process running is one the next check
            # under the same lock would inherit.
            if ($stripped) {
                try { Invoke-RigAction -On 'hostie' -Path '/dlc/restore' -Body @{ } -NoRetry | Out-Null } catch { }
            }
            # Leave the world with nobody attached, so the guaranteed teardown
            # never has to stop a host underneath a live joiner.
            try { Invoke-RigAction -On 'joiner' -Path '/disconnect' -Body @{ } -Blocking -NoRetry | Out-Null } catch { }
        }
    }
