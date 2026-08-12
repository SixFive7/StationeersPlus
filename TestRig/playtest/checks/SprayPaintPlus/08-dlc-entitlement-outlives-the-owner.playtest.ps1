# =============================================================================
# Spray Paint Plus: the entitlement outlives the owner
# =============================================================================
# From Mods/SprayPaintPlus/PLAYTEST.md, Session B: "After the owning client
# disconnects, metallic must stay available to everyone still connected until the
# world unloads. Needs a second player to remain connected and observe."
#
# The player who remains is the HOST, for the same reason as in check 07: only a
# joining client can put an entitlement into the pool of a freshly created world,
# so the owner has to be the joiner and the observer has to be the host. The host
# is also the authority, which is where every reading below is taken.
#
# The behaviour under test is a property of the pool, and the pool only ever
# GROWS during a session: nothing subtracts on disconnect, and ClearAll() runs on
# world teardown rather than on a player leaving. So a non-owner who could reach
# metallic while the owner was connected must still reach it afterwards.
#
# WHY THE SECOND HALF IS A SCROLL AND NOT A SECOND PAINT
# Entitlement is consulted on the mod's cycling path and on the eyedropper, and
# nowhere on the paint-application path (Research/GameSystems/DLCGating.md: there
# is no check in Thing.SetCustomColor, OnServer.SetCustomColor or ISprayer.
# DoSpray). Painting again with a can that is ALREADY metallic would therefore
# prove nothing about entitlement at all. The check arms a fresh base can after
# the owner has gone and scrolls it up from swatch 0, which is the only action
# that has to ask the gate.
#
# SEQUENCING, AND WHY BOTH INSTANCES ARE Role='client' WITH NO HOST IN THE LIST
# Identical to check 07 and for the same reason: POST /dlc/remove has to run at
# the MENU and before POST /host, and the harness's bring-up leaves no window
# between "reached the menu" and "hosts or connects". The joiner is declared
# first so the guaranteed teardown stops it before the instance holding the
# world. scope=owned, never shared, because shared is the pool this check is
# about.
#
# WHAT THIS CHECK DEPENDS ON THAT IS NOT YET PROVEN LIVE
# POST /dlc/remove, exactly as in check 07. Everything else is the ordinary
# connect, disconnect and paint path.
#
# WHAT WOULD MAKE THIS FAIL
#   - the pool being cleared when its contributor leaves, which would take the
#     entitlement away from every remaining player mid-session;
#   - DlcPaintGate consulting local ownership rather than the pool, which would
#     make the host lose access the moment the owner disconnected even though
#     the pool still carried the bit.
#
# THIS CHECK IS A SUPERSET OF CHECK 07 BY CONSTRUCTION: it has to establish that
# the non-owner could reach metallic WHILE the owner was connected before "still"
# means anything. Check 07 remains worth running on its own, because when both
# fail together the pair says which half broke.
#
# THIS CHECK WOULD ALSO PASS AGAINST THE PRE-v1.11.0 BUILD, like check 07. The
# shared-DLC path predates the settings split.
#
# PREREQUISITES
#   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
#   copy bin/Release/SprayPaintPlus.dll into the SprayPaintPlus folder under
#     TestRig/ClientRig/data/hostie/userdata/mods/ and .../joiner/userdata/mods/
#   the Steam session must own Metallic Paints; the check declines otherwise
# =============================================================================

$sppRepoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

Register-PlaytestCheck `
    -Name 'the entitlement outlives the owner' `
    -Summary 'a non-owning host that could reach metallic while the owner was connected must still reach it after the owner disconnects' `
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

        $guid          = 'net.spraypaintplus'
        $firstMetallic = 12
        $spawned       = @()
        $stripped      = $false

        try {
            # ---- 1. Menu, entitlement, strip. Same three steps as check 07 and
            # in the same order, because the order is the mechanism.
            foreach ($name in @('hostie', 'joiner')) {
                $phase = Read-RigValue -From $name -Reader status -Select 'phase'
                if ("$($phase.Value)" -ne 'menu') {
                    Set-PlaytestInconclusive -Detector 'not-at-menu' `
                        -Because "'$name' reports phase=$($phase.Value), and entitlement can only be removed between GameManager.IsInitialized and world entry. Nothing was measured about the mod."
                }
                $owned = Read-RigValue -From $name -Reader dlc -Select 'state.owned'
                if ("$($owned.Value)" -notmatch 'MetallicPaints') {
                    Set-PlaytestInconclusive -Detector 'dlc-not-owned' `
                        -Because "'$name' reports owning [$($owned.Value)], which does not include MetallicPaints, so this session has no owner to lose and nothing to strip"
                }
            }

            Invoke-RigAction -On 'hostie' -Path '/dlc/remove' -Body @{ dlc = 'MetallicPaints'; scope = 'owned' } | Out-Null
            $stripped = $true
            Assert-RigValue -From 'hostie' -Reader dlc -Select 'state.removedOwned' -Matches 'MetallicPaints' `
                -Because 'the observer in this check has to be a genuine non-owner, or the reading after the owner leaves is just a process consulting its own entitlement'

            # ---- 2. Host, then bring the owner in.
            Invoke-RigAction -On 'hostie' -Path '/host' -Body @{ world = 'Lunar' } -Blocking | Out-Null
            Wait-RigStage -Name 'hostie' -Stage 'inWorld' -WaitSeconds 600 | Out-Null

            $hosting = Read-RigValue -From 'hostie' -Reader status -Select 'hosting'
            $role    = Read-RigValue -From 'hostie' -Reader status -Select 'role'
            if ($hosting.Value -ne $true -or "$($role.Value)" -ne 'listenHost') {
                Set-PlaytestInconclusive -Detector 'host-not-hosting' `
                    -Because "the host reports hosting=$($hosting.Value) role=$($role.Value) after POST /host, so there is no session for the owner to join"
            }

            Assert-RigValue -From 'hostie' -Reader dlc -Select 'state.removedOwned' -Matches 'MetallicPaints' `
                -Because 'a strip that did not survive world entry would leave the host owning the DLC outright, and the whole check would be measuring a process that never lost anything'

            # The harness's own bring-up path, not a copy of it: it reads the port
            # off the host, polls the HOST roster rather than reading it once, and
            # retries from the menu. The copy this replaced reported
            # joiner-not-in-roster on 2026-08-11 on a rig that was joining fine.
            Connect-RigJoiner -Name 'joiner' -To 'hostie' | Out-Null
            Wait-PlaytestSeconds 5

            $poolWithOwner = Read-RigValue -From 'hostie' -Reader dlc -Select 'state.shared'
            if ("$($poolWithOwner.Value)" -notmatch 'MetallicPaints') {
                Set-PlaytestInconclusive -Detector 'entitlement-not-in-pool' `
                    -Because "the host's pool reads [$($poolWithOwner.Value)] with the owner connected, so nothing was ever shared and 'it outlives the owner' has no starting point"
            }

            # ---- 3. Cycling has to be able to leave the base family, and the
            # wheel has to run forwards.
            foreach ($pair in @(
                @{ section = 'Client - Color Cycling'; key = 'Color Cycling';                 value = 'AllColors' }
                @{ section = 'Server - Color Cycling'; key = 'Color Cycling';                 value = 'AllColors' }
                @{ section = 'Client - Preferences';   key = 'Invert Color Scroll Direction'; value = 'false' }
            )) {
                Invoke-RigAction -On 'hostie' -Path '/config/set' -Body @{
                    guid = $guid; section = $pair.section; key = $pair.key; value = $pair.value; save = $false
                } | Out-Null
            }

            # ---- 4. The starting point: the non-owner reaches metallic WHILE
            # the owner is connected. Without this, "still" below means nothing.
            for ($i = 0; $i -lt 2; $i++) {
                $r = Invoke-RigAction -On 'hostie' -Path '/spawn/structure' -Body @{
                    prefab = 'StructureCableStraight'; distance = 3; offset = @(($i * 6), 0, 0)
                }
                $id = "$($r.Response.referenceId)"
                if (-not $id -or $id -eq '0') {
                    Set-PlaytestInconclusive -Detector 'scene-not-staged' `
                        -Because 'a structure to paint did not come back with a reference id, so a scroll has nothing to prove itself against'
                }
                $spawned += $id
            }

            Invoke-RigAction -On 'hostie' -Path '/inventory/arm' -Body @{
                prefab = 'ItemSprayCanBlue'; hand = 'activeHand'; replace = $true
            } | Out-Null
            Wait-PlaytestSeconds 2
            Invoke-RigAction -On 'hostie' -Path '/input/scroll' -Body @{ notches = 1; repeat = 12; gapFrames = 3 } | Out-Null
            Wait-PlaytestSeconds 2
            Invoke-RigAction -On 'hostie' -Path '/player/use' -Body @{ targetId = $spawned[0] } | Out-Null
            Wait-PlaytestSeconds 2

            Assert-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $spawned[0]; fields = 'CustomColor.Index' } `
                -Of "$($spawned[0])/CustomColor.Index" -Select 'value' -AtLeast $firstMetallic `
                -Because 'this is the starting point the rest of the check depends on: a non-owner reaching the metallic band while an owner is connected. If this already fails, the pool is not being consulted at all and nothing can be said about what happens after the owner leaves'

            # ---- 5. The owner leaves.
            Invoke-RigAction -On 'joiner' -Path '/disconnect' -Body @{ } -Blocking | Out-Null
            Wait-RigStage -Name 'joiner' -Stage 'menu' -WaitSeconds 180 | Out-Null

            $rosterAfter = Read-RigValue -From 'hostie' -Reader roster -Select 'count'
            if ([int]$rosterAfter.Value -gt 1) {
                Set-PlaytestInconclusive -Detector 'owner-still-connected' `
                    -Because "the host roster still carries $($rosterAfter.Value) entries after the owner was told to disconnect, so the owner has not actually left and 'after the owner leaves' has not happened yet"
            }
            Wait-PlaytestSeconds 5

            Assert-RigValue -From 'hostie' -Reader dlc -Select 'state.shared' -Matches 'MetallicPaints' `
                -Because 'the pool only ever grows during a session and is cleared on world teardown, not on a player leaving; losing the bit here would take metallic paint away from everyone still in the world the moment its owner logged off'

            # ---- 6. And the behaviour, not just the bookkeeping: a fresh base
            # can, scrolled from swatch 0, with no owner in the session.
            Invoke-RigAction -On 'hostie' -Path '/inventory/arm' -Body @{
                prefab = 'ItemSprayCanBlue'; hand = 'activeHand'; replace = $true
            } | Out-Null
            Wait-PlaytestSeconds 2
            Invoke-RigAction -On 'hostie' -Path '/input/scroll' -Body @{ notches = 1; repeat = 12; gapFrames = 3 } | Out-Null
            Wait-PlaytestSeconds 2
            Invoke-RigAction -On 'hostie' -Path '/player/use' -Body @{ targetId = $spawned[1] } | Out-Null
            Wait-PlaytestSeconds 2

            Assert-RigValue -From 'hostie' -Reader thing `
                -ReaderArgs @{ refIds = $spawned[1]; fields = 'CustomColor.Index' } `
                -Of "$($spawned[1])/CustomColor.Index" -Select 'value' -AtLeast $firstMetallic `
                -Because 'the entitlement has to outlive the player who brought it, all the way to world teardown: a base swatch here means the gate started refusing the moment the owner left, which is the mid-session capability loss this check exists to catch'
        }
        finally {
            foreach ($id in $spawned) {
                try { Invoke-RigAction -On 'hostie' -Path '/console/exec' -Body @{ command = "thing delete $id" } -NoRetry | Out-Null } catch { }
            }
            if ($stripped) {
                try { Invoke-RigAction -On 'hostie' -Path '/dlc/restore' -Body @{ } -NoRetry | Out-Null } catch { }
            }
            # Belt and braces on the teardown ordering: if this check ended
            # between the connect and the disconnect, the world holder would be
            # stopped underneath a live joiner. The guaranteed teardown already
            # stops the joiner first because of the order these instances are
            # declared in, and this makes it true regardless of that ordering.
            try { Invoke-RigAction -On 'joiner' -Path '/disconnect' -Body @{ } -Blocking -NoRetry | Out-Null } catch { }
        }
    }
