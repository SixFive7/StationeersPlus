# =============================================================================
# Spray Paint Plus: the join summary is one line, naming everything
# =============================================================================
# From Mods/SprayPaintPlus/PLAYTEST.md: "Join that same server. Expected:
# exactly one console line naming every blocked function, prefixed
# '[Spray Paint Plus] '."
#
# WarningNotifier.OnJoinPayloadReceived collects every function the joining
# player has enabled and this server refuses, and prints ONE line for all of
# them. The design note in that file is explicit that it must never be one line
# each, which is what this check pins.
#
# WHY THE JOINER IS DISCONNECTED AND RECONNECTED INSIDE THE BODY
# The summary is produced when the host's values land through
# SettingsConfigSync's join suffix, so the host's server halves have to be set
# BEFORE the join. The harness connects joiners during bring-up, before a check
# body runs, so the only way to get the ordering right is to bounce the joiner
# once the arrangement is in place. Leaving the world also runs
# WarningNotifier.ResetSession, so the rejoin starts from a clean notice state.
#
# WHAT WOULD MAKE THIS FAIL
#   - one line per blocked function instead of one line for all of them: the
#     count of "[Spray Paint Plus] " lines in the join window would be 3;
#   - a function silently dropped from the summary: its name would be missing;
#   - the summary firing when nothing is blocked, or not firing at all;
#   - the console prefix reverting to the code name.
#
# AGAINST THE PRE-v1.11.0 BUILD THIS CHECK FAILS: the console prefix was
# "[SprayPaintPlus] ", so every contains filter naming the display name matches
# nothing and the first assertion reads 0 against an expected 1.
#
# PREREQUISITES
#   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
#   copy bin/Release/SprayPaintPlus.dll into the SprayPaintPlus folder under
#     TestRig/ClientRig/data/hostie/userdata/mods/ and .../joiner/userdata/mods/
# =============================================================================

$sppRepoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

Register-PlaytestCheck `
    -Name 'the join summary is one console line naming every blocked function' `
    -Summary 'a joiner whose enabled functions this server refuses is told once, in one line, listing all of them' `
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

        $guid = 'net.spraypaintplus'

        # Three functions from three different groups, so a summary that only
        # walks one group is caught. The names are the settings-panel entry
        # names, which WarningNotifier.Functions uses verbatim.
        $blocked = @('Unlimited Spray Paint Uses', 'Glow Paint', 'Network Paint Cables')

        try {
            # ---- 1. Arrange on the host: three server halves off.
            foreach ($pair in @(
                @{ section = 'Server - Consumables';      key = 'Unlimited Spray Paint Uses'; value = 'false' }
                @{ section = 'Server - Glow Paint';       key = 'Glow Paint';                 value = 'false' }
                @{ section = 'Server - Network Painting'; key = 'Network Paint Cables';       value = 'false' }
            )) {
                Invoke-RigAction -On 'hostie' -Path '/config/set' -Body @{
                    guid = $guid; section = $pair.section; key = $pair.key; value = $pair.value; save = $false
                } | Out-Null
            }

            # ---- 2. And on the joiner: the matching client halves ON, because
            # AddIfBlocked only reports a real mismatch. A function the player
            # turned off themselves is not worth a line and would not appear.
            foreach ($pair in @(
                @{ section = 'Client - Consumables';      key = 'Unlimited Spray Paint Uses'; value = 'true' }
                @{ section = 'Client - Glow Paint';       key = 'Glow Paint';                 value = 'true' }
                @{ section = 'Client - Network Painting'; key = 'Network Paint Cables';       value = 'true' }
            )) {
                Invoke-RigAction -On 'joiner' -Path '/config/set' -Body @{
                    guid = $guid; section = $pair.section; key = $pair.key; value = $pair.value; save = $false
                } | Out-Null
            }

            Assert-RigValue -From 'hostie' -Reader config -ReaderArgs @{ guid = $guid } `
                -Of 'Server - Glow Paint/Glow Paint' -Select 'value' -Is $false `
                -Because 'the summary only names functions this server actually refuses, so an arrangement that did not take would produce a shorter line and a misleading pass'
            Assert-RigValue -From 'joiner' -Reader config -ReaderArgs @{ guid = $guid } `
                -Of 'Client - Glow Paint/Glow Paint' -Select 'value' -Is $true `
                -Because 'a client half the player switched off is deliberately not reported, so this has to be on for the mismatch to exist at all'

            # ---- 3. Baseline the joiner's console before the bounce. The tee
            # is process-local and survives leaving a world, so a sequence taken
            # now excludes everything the first join printed.
            $seq0 = Read-RigValue -From 'joiner' -Reader console -ReaderArgs @{ limit = 1 } -Select 'nextSeq'

            # ---- 4. Bounce the joiner so the join payload is rebuilt from the
            # arrangement above.
            Invoke-RigAction -On 'joiner' -Path '/disconnect' -Body @{ } -Blocking | Out-Null
            Wait-RigStage -Name 'joiner' -Stage 'menu' -WaitSeconds 180 | Out-Null

            # Connect-RigJoiner is the harness's own bring-up path, reused here
            # verbatim. This check used to have its own copy, and the copy did not
            # confirm-and-retry: on 2026-08-11 it reported joiner-not-in-roster on
            # a rig where 10 of 10 hand-driven joins landed the same evening. The
            # helper reads the port off the host, polls the HOST roster rather
            # than reading it once, and retries from the menu, because "a client
            # that has just disconnected is still settling" is documented
            # behaviour and this is exactly that window.
            $join = Connect-RigJoiner -Name 'joiner' -To 'hostie'

            # Re-baseline from the join that actually LANDED. The summary is
            # printed once per join, so if the helper needed three attempts the
            # window opened before them holds three lines and a correct mod fails
            # the "exactly one" assertion. Measured: that is precisely what
            # happened on the first run after the helper was introduced.
            if ($join.SeqBeforeConnect) { $seq0 = @{ Value = $join.SeqBeforeConnect } }

            # The payload rides the join itself, so it has normally landed by
            # the time inWorld is reported. A few seconds of slack costs nothing
            # and removes a timing false negative.
            Wait-PlaytestSeconds 5

            # ---- 5. Conclude, on the joiner, which is the authority for what
            # its own player was told. source=console because the tee merges the
            # game console with the BepInEx log and this line goes to both.
            Assert-RigValue -From 'joiner' -Reader console `
                -ReaderArgs @{ since = "$($seq0.Value)"; source = 'console'; contains = '[Spray Paint Plus] This server does not allow'; limit = 200 } `
                -Select 'count' -Is 1 `
                -Because 'a joiner whose enabled functions the server refuses must be told exactly once, and with the display-name prefix the shared PlayerMessage helper supplies'

            Assert-RigValue -From 'joiner' -Reader console `
                -ReaderArgs @{ since = "$($seq0.Value)"; source = 'console'; contains = '[Spray Paint Plus] '; limit = 200 } `
                -Select 'count' -Is 1 `
                -Because 'ONE line for all of them is the whole point: three blocked functions producing three lines is the regression this pins, and counting every line the mod printed in the join window is the only way to see it'

            foreach ($name in $blocked) {
                Assert-RigValue -From 'joiner' -Reader console `
                    -ReaderArgs @{ since = "$($seq0.Value)"; source = 'console'; contains = $name; limit = 200 } `
                    -Select 'count' -Is 1 `
                    -Because "'$name' is refused by this server and the player has it enabled, so the summary has to name it; a function silently missing from the list is a player who never finds out why their setting does nothing"
            }
        }
        finally {
            # ---- Clean up the config on both halves. Values were written with
            # save=false so nothing reached disk, but the live entries stay set
            # for as long as the process runs and the next check may share it.
            foreach ($pair in @(
                @{ on = 'hostie'; section = 'Server - Consumables';      key = 'Unlimited Spray Paint Uses' }
                @{ on = 'hostie'; section = 'Server - Glow Paint';       key = 'Glow Paint' }
                @{ on = 'hostie'; section = 'Server - Network Painting'; key = 'Network Paint Cables' }
            )) {
                try {
                    Invoke-RigAction -On $pair.on -Path '/config/set' -NoRetry -Body @{
                        guid = 'net.spraypaintplus'; section = $pair.section; key = $pair.key; value = 'true'; save = $false
                    } | Out-Null
                }
                catch { }
            }
        }
    }
