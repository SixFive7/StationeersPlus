# =============================================================================
# Spray Paint Plus: the effective-settings line stays in the log, alone
# =============================================================================
# THE HEADLESS REGRESSION IN Mods/SprayPaintPlus/PLAYTEST.md IS NOT EXPRESSIBLE
# IN THIS HARNESS, AND THIS FILE IS NOT A SUBSTITUTE FOR IT. Read this before
# treating a pass here as the regression guard being green.
#
# That entry says: re-run Scenario = spp-settings-merge-verify and assert its own
# pass tally. Three things stop this harness from doing it, and none of them is
# a missing verb that a check could invent:
#
#   1. The scenario runs inside ScenarioRunner, which is deployed to the
#      DEDICATED SERVER only. The harness's launcher seam is wired to
#      client-rig.ps1 in TestRig/playtest/playtest.ps1, so no check can drive
#      dedicated-server.ps1 through it.
#   2. Every reader resolves an instance NAME to a client-rig control-plane
#      port. The dedicated server has no control plane and is not in the client
#      rig registry, so there is nothing for -From to name.
#   3. The tally is a line in the server's BepInEx log:
#        [ScenarioRunner] spp-settings-merge | RESULT ALL PASS pass=N fail=0 total=N
#      No reader answers "what is in that log", and the assert verbs take a
#      reader and nothing else. Reaching around them to compare a string a check
#      read for itself would be the bare-boolean assert the harness exists to
#      prevent.
#
# Run it by hand, under the same session lock, and read the tally yourself:
#   set Scenario = spp-settings-merge-verify in
#     TestRig/DedicatedServer/install/BepInEx/config/net.scenariorunner.cfg
#   dedicated-server.ps1 -Start -As <id>
#   dedicated-server.ps1 -Logs -Grep 'spp-settings-merge \| RESULT'
#
# WHAT THIS CHECK DOES COVER
# PLAYTEST.md names ONE assertion inside that scenario as the reason to re-run it
# for this change: "P6 asserts LogEffectiveSettings emits exactly one Info line
# on the mod's log source; nothing in this change touches that method, but it is
# the assertion that would catch a stray PlayerMessage.Info slipping onto the
# same source." That property is observable on a client instance, and this check
# pins it plus the two things around it:
#
#   - the support line goes to the BepInEx log EXACTLY ONCE per join;
#   - it never reaches the player's console, because it is long and is for
#     whoever reads a bug report rather than for the player mid-game;
#   - a joiner with nothing blocked is told nothing at all, which is the
#     if (blocked.Count == 0) return in OnJoinPayloadReceived and the other half
#     of the join-summary check next to this one.
#
# WHY THE LINE IS COUNTED ON A JOINER AND NOT ON A LONE HOST
# On a host the only emission comes from OnAllModsLoaded, during boot, and the
# console tee keeps 2000 lines per source while mod loading produces thousands
# in a couple of seconds. A boot-time line is routinely evicted before anything
# can read it, and a check built on that would fail for a reason that has
# nothing to do with the mod. OnJoinPayloadReceived emits it again at join time,
# in a quiet window the check controls, which is measurable.
#
# WHAT WOULD MAKE THIS FAIL
#   - a stray PlayerMessage.Info on the mod's log source inside the join window:
#     the count would exceed one;
#   - LogEffectiveSettings being emitted twice per join, or not at all;
#   - the support line reaching the console, where it does not belong;
#   - a join summary appearing when this server refuses nothing.
#
# THIS CHECK WOULD ALSO PASS AGAINST THE PRE-v1.11.0 BUILD. It deliberately
# carries no display-name prefix in any filter, because the line it counts is a
# log line and never had one. It is a regression guard, not a migration
# discriminator: checks 01, 02 and 03 are what tell the two builds apart.
#
# PREREQUISITES
#   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
#   copy bin/Release/SprayPaintPlus.dll into the SprayPaintPlus folder under
#     TestRig/ClientRig/data/hostie/userdata/mods/ and .../joiner/userdata/mods/
# =============================================================================

$sppRepoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

Register-PlaytestCheck `
    -Name 'the effective-settings line is one log line and never reaches the console' `
    -Summary 'the support dump lands in the BepInEx log exactly once per join, stays out of the player console, and a server that blocks nothing says nothing' `
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
        $supportLine = 'Effective settings (client/server'

        # Every paired boolean, by group. The instance's BepInEx config is
        # re-copied from the developer's own install when a lock is taken, so a
        # value the developer left switched off would otherwise put a function in
        # the blocked list and make the last assertion here fail for a reason
        # that is nothing to do with the code under test. Both halves are pinned
        # to permissive so "nothing is blocked" is a fact rather than a hope.
        $networkKeys = @(
            'Network Painting', 'Network Paint Pipes', 'Network Paint Cables',
            'Network Paint Chutes', 'Network Paint Walls', 'Network Paint Rails',
            'Network Paint Large Structures', 'Network Paint Elevators',
            'Network Paint Ladders', 'Network Paint Stairs', 'Network Paint Stairwells'
        )

        $permissive = @()
        foreach ($key in $networkKeys) {
            $permissive += @{ on = 'hostie'; section = 'Server - Network Painting'; key = $key; value = 'true' }
            $permissive += @{ on = 'joiner'; section = 'Client - Network Painting'; key = $key; value = 'true' }
        }
        $permissive += @{ on = 'hostie'; section = 'Server - Consumables';  key = 'Unlimited Spray Paint Uses'; value = 'true' }
        $permissive += @{ on = 'joiner'; section = 'Client - Consumables';  key = 'Unlimited Spray Paint Uses'; value = 'true' }
        $permissive += @{ on = 'hostie'; section = 'Server - Glow Paint';   key = 'Glow Paint';                 value = 'true' }
        $permissive += @{ on = 'joiner'; section = 'Client - Glow Paint';   key = 'Glow Paint';                 value = 'true' }
        $permissive += @{ on = 'hostie'; section = 'Server - Color Cycling'; key = 'Color Picking';             value = 'true' }
        $permissive += @{ on = 'joiner'; section = 'Client - Color Cycling'; key = 'Color Picking';             value = 'true' }
        $permissive += @{ on = 'hostie'; section = 'Server - Color Cycling'; key = 'Color Cycling';             value = 'AllColors' }
        $permissive += @{ on = 'joiner'; section = 'Client - Color Cycling'; key = 'Color Cycling';             value = 'AllColors' }

        foreach ($entry in $permissive) {
            Invoke-RigAction -On $entry.on -Path '/config/set' -Body @{
                guid = $guid; section = $entry.section; key = $entry.key; value = $entry.value; save = $false
            } | Out-Null
        }

        # Two spot reads from the two authorities, one per half. Reading all 30
        # back would say nothing the first two do not.
        Assert-RigValue -From 'hostie' -Reader config -ReaderArgs @{ guid = $guid } `
            -Of 'Server - Network Painting/Network Paint Cables' -Select 'value' -Is $true `
            -Because 'the last assertion in this check is that a server refusing nothing says nothing, and a single server half left off from a previous session would produce a summary line and turn a correct build into a failure'
        Assert-RigValue -From 'joiner' -Reader config -ReaderArgs @{ guid = $guid } `
            -Of 'Client - Glow Paint/Glow Paint' -Select 'value' -Is $true `
            -Because 'AddIfBlocked only reports a function the player has enabled, so a client half left off would hide a genuine mismatch instead of reporting it'

        # ---- Baseline the joiner's console, then bounce it so the join payload
        # is rebuilt and LogEffectiveSettings runs inside a window this check
        # controls. The tee is process-local and survives leaving a world.
        $seq0 = Read-RigValue -From 'joiner' -Reader console -ReaderArgs @{ limit = 1 } -Select 'nextSeq'

        Invoke-RigAction -On 'joiner' -Path '/disconnect' -Body @{ } -Blocking | Out-Null
        Wait-RigStage -Name 'joiner' -Stage 'menu' -WaitSeconds 180 | Out-Null

        # The harness's own bring-up path, reused rather than copied. The copy
        # this replaced connected once and read the roster once, which is the
        # 2026-08-11 joiner-not-in-roster inconclusive on a rig that was joining
        # fine.
        $join = Connect-RigJoiner -Name 'joiner' -To 'hostie'

        # Re-baseline from the join that actually LANDED. LogEffectiveSettings
        # runs once per join, so a window opened before a retried join holds one
        # line per attempt and the "exactly one Info line" assertion would fail a
        # correct mod.
        if ($join.SeqBeforeConnect) { $seq0 = @{ Value = $join.SeqBeforeConnect } }
        Wait-PlaytestSeconds 5

        # ---- Conclude on the joiner, which is the authority for its own log
        # and its own console.
        Assert-RigValue -From 'joiner' -Reader console `
            -ReaderArgs @{ since = "$($seq0.Value)"; source = 'bepinex'; contains = $supportLine; limit = 500 } `
            -Select 'count' -Is 1 `
            -Because 'SettingsConfigSync calls LogEffectiveSettings once when the host values land, and exactly one line is the property the headless P6 assertion protects: two would mean something calls it twice, none would mean a bug report arrives with no settings dump in it'

        Assert-RigValue -From 'joiner' -Reader console `
            -ReaderArgs @{ since = "$($seq0.Value)"; source = 'console'; contains = 'Effective settings'; limit = 500 } `
            -Select 'count' -Is 0 `
            -Because 'the support line is long and is for whoever reads the log after a bug report, never for the player mid-game; a PlayerMessage call replacing the plain log call would put it on screen and this is what would catch it'

        Assert-RigValue -From 'joiner' -Reader console `
            -ReaderArgs @{ since = "$($seq0.Value)"; source = 'console'; contains = 'This server does not allow'; limit = 500 } `
            -Select 'count' -Is 0 `
            -Because 'this server refuses nothing the joiner asked for, so OnJoinPayloadReceived must return without printing; a summary listing nothing, or listing a function that is not actually blocked, is noise a player cannot act on'
    }
