# =============================================================================
# Spray Paint Plus: the conflict banner, boot line then six world lines
# =============================================================================
# From Mods/SprayPaintPlus/PLAYTEST.md: one red line at boot, then a six-line
# banner starting only after the world is up, at 5 second intervals, the sixth
# ending "(This warning will stop repeating; see the BepInEx log.)", then
# nothing. None of the six may appear while still at the menu.
#
# THIS EXERCISES THE DETECTOR, NOT A REAL CONFLICT. Say it plainly in any report
# of a pass here. The fixture at
# .work/2026-08-11-spraypaintplus-playtests/ConflictStub/ is two assemblies whose
# Assembly.GetName().Name values are exactly ColorCycler and NetworkPainter, and
# nothing else: no Harmony patch, no prefab, no reference to Assembly-CSharp. The
# detector in Plugin.cs.OnAllModsLoaded compares that simple assembly name
# against two literals, so the stub is a faithful trigger for the detector and no
# evidence at all about coexisting with the real Workshop mods, which patch the
# same methods this mod patches. It also inherits the assumption it is testing:
# that the real assemblies are named exactly that. The fixture's own README says
# the same thing at more length.
#
# WHY THE INSTANCE IS RESTARTED INSIDE THE BODY
# The detector runs once, on Prefab.OnPrefabsLoaded, during boot. A stub seeded
# after the process is up would never be seen, and the harness brings instances
# up before a check body runs. So the body seeds the fixture into the instance's
# OWN save root and then restarts that ONE instance, which is also the ordering
# that lets Assert-BinaryUnderTest attest a clean process first.
#
# THE INSTANCE IS DECLARED Role='client' WITH NO HOST IN THE LIST, deliberately.
# That leaves bring-up at the menu instead of creating a world that would be
# thrown away by the restart, and the body drives POST /host itself once the
# fixture is live. The spec role only steers the harness; POST /host works on any
# instance and the live answer is /status.role.
#
# CLEANUP IS THE DANGEROUS PART OF THIS CHECK, not the assertions. The
# between-session state reset that runs when a new lock is taken does NOT clear
# userdata/mods/ or modconfig.xml: they are on the KEPT side of it. A stub left
# behind therefore disables Spray Paint Plus on every later run of this instance,
# silently, and the next agent spends a session finding out why. The finally
# below restores the modconfig verbatim from a snapshot taken before the edit,
# deletes the seeded folder, verifies both, and writes the result into the
# evidence bundle whether it worked or not.
#
# WHAT WOULD MAKE THIS FAIL
#   - the banner going unbounded again, which is what it used to be: more than
#     six lines;
#   - the banner firing at the menu, where console overlay lines have aged off
#     long before the player reaches a world;
#   - the sixth line losing the sentence that says it is the last one;
#   - the detector not firing at all, which the CONFLICT log line catches
#     independently of the banner;
#   - the console prefix reverting to the code name.
#
# AGAINST THE PRE-v1.11.0 BUILD THIS CHECK FAILS: the banner was an unbounded
# Debug.LogError loop with the code-name prefix, so the counted substring matches
# nothing and the six-line assertion reads 0.
#
# PREREQUISITES
#   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
#   copy bin/Release/SprayPaintPlus.dll into
#     TestRig/ClientRig/data/hostie/userdata/mods/SprayPaintPlus/
#   dotnet build .work/2026-08-11-spraypaintplus-playtests/ConflictStub/ColorCycler/ColorCycler.csproj -c Release
#   dotnet build .work/2026-08-11-spraypaintplus-playtests/ConflictStub/NetworkPainter/NetworkPainter.csproj -c Release
# =============================================================================

$sppRepoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))

Register-PlaytestCheck `
    -Name 'the conflict banner is one boot line then six world lines' `
    -Summary 'with a stub that only carries the two conflicting assembly names, the detector fires, patches are withheld, and the banner is bounded and starts after the menu' `
    -Instances @(
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

        $bannerLine = '[Spray Paint Plus] NOT LOADED! Conflicting mods:'
        $lastLine   = 'This warning will stop repeating'

        $fixture     = Join-Path $sppRepoRoot '.work\2026-08-11-spraypaintplus-playtests\ConflictStub\mod'
        $userData    = Join-Path (Get-PlaytestRigHome) 'ClientRig\data\hostie\userdata'
        $modConfig   = Join-Path $userData 'modconfig.xml'
        $stubFolder  = Join-Path (Join-Path $userData 'mods') 'ConflictStub'
        $configBackup = ''
        $seeded      = $false

        try {
            # ---- 1. The fixture has to exist and has to be built. Neither is
            # something the mod can be blamed for, so both decline.
            foreach ($needed in @('ColorCycler.dll', 'NetworkPainter.dll')) {
                if (-not (Test-Path -LiteralPath (Join-Path $fixture $needed))) {
                    Set-PlaytestInconclusive -Detector 'fixture-not-built' `
                        -Because "the conflict stub is missing $needed at $fixture, so there is nothing for the detector to find and nothing was measured about the mod. Build it: dotnet build .work/2026-08-11-spraypaintplus-playtests/ConflictStub/ColorCycler/ColorCycler.csproj -c Release (and the NetworkPainter project beside it)."
                }
            }
            if (-not (Test-Path -LiteralPath $modConfig)) {
                Set-PlaytestInconclusive -Detector 'instance-not-provisioned' `
                    -Because "the instance has no modconfig.xml at $modConfig, so a local mod cannot be registered with it. Re-provision: client-rig.ps1 -Provision -Force -As <id> -Instance hostie"
            }

            # ---- 2. Seed the stub into the instance's OWN save root. This tree
            # is tier 3 and free to edit; the developer's own mods folder and
            # modconfig are the read-only provisioning source and are never
            # touched.
            $configBackup = Get-Content -Raw -LiteralPath $modConfig
            New-Item -ItemType Directory -Force -Path $stubFolder | Out-Null
            Copy-Item -Path (Join-Path $fixture '*') -Destination $stubFolder -Recurse -Force
            $seeded = $true

            # StationeersLaunchPad prunes a <Local> entry whose folder is not
            # under the active save path, which is why the copy above has to live
            # inside this instance's own userdata rather than being referenced
            # where it was built.
            #
            # Inserted by index rather than by -replace: the replacement string
            # carries a Windows path, and a regex replacement treats $ specially,
            # so a path with one in it would be silently mangled.
            $closing = '</ModConfig>'
            $at = $configBackup.LastIndexOf($closing)
            if ($at -lt 0) {
                Set-PlaytestInconclusive -Detector 'modconfig-unrecognised' `
                    -Because "the instance's modconfig.xml has no $closing element, so the stub cannot be registered with StationeersLaunchPad and the detector would never see it. Re-provision: client-rig.ps1 -Provision -Force -As <id> -Instance hostie"
            }
            $entry = "  <Local Enabled=`"true`">`r`n    <Path Value=`"$stubFolder`" />`r`n  </Local>`r`n"
            Set-Content -LiteralPath $modConfig -Encoding utf8 -NoNewline `
                -Value ($configBackup.Substring(0, $at) + $entry + $configBackup.Substring($at))

            Write-PlaytestEvidence -Name 'conflict-stub-seeded.txt' -Content (@(
                "fixture   : $fixture"
                "stub      : $stubFolder"
                "modconfig : $modConfig"
                "seededAt  : $(Get-PlaytestStamp)"
            ) -join "`n") | Out-Null

            # ---- 3. Restart that ONE instance so the detector runs against it.
            Restart-RigInstance -Name 'hostie' -Reason 'seeding the conflict stub, which is only read at boot'
            Wait-RigStage -Name 'hostie' -Stage 'menu' -WaitSeconds 400 | Out-Null

            # ---- 4. The fixture is live, and the detector saw it. Every line in
            # this step is printed during BOOT, which is precisely what the
            # console tee cannot be asked for: it is a 2000-line ring per source
            # and StationeersLaunchPad's mod loading evicts thousands of lines
            # before a check can read anything. On 2026-08-11 this check declined
            # with console-tee-evicted for exactly that reason, which was the
            # honest answer to the wrong question.
            #
            # The bepinexlog reader reads BepInEx/LogOutput.log on disk instead.
            # It has no ring, so nothing ages off, and the between-session state
            # reset deletes it, so what it holds is this run and only this run.
            # Boot-time evidence belongs there; the tee is still the right reader
            # for the runtime half of this check below, where sequence numbers
            # are what separate "at the menu" from "in a world".
            $logSeen = Read-RigValue -From 'hostie' -Reader bepinexlog `
                -ReaderArgs @{ contains = 'TEST FIXTURE ACTIVE' } -Select 'count'
            $logExists = Read-RigValue -From 'hostie' -Reader bepinexlog -Select 'exists'
            if ($logExists.Value -ne $true) {
                Set-PlaytestInconclusive -Detector 'bepinex-log-missing' `
                    -Because "the instance has no BepInEx/LogOutput.log to read, so a boot-time line cannot be looked for at all and nothing was measured about the mod. It is deleted by the state reset and written afresh on every launch, so an absent one means the instance tree is not where the registry says it is."
            }

            Assert-RigValue -From 'hostie' -Reader bepinexlog `
                -ReaderArgs @{ contains = 'TEST FIXTURE ACTIVE' } `
                -Select 'count' -AtLeast 2 `
                -Because 'both stub assemblies have to be loaded before anything is read into the banner or its absence; a fixture that did not load makes every assertion below meaningless'

            foreach ($name in @('CONFLICT: ColorCycler.dll is loaded', 'CONFLICT: NetworkPainter.dll is loaded')) {
                Assert-RigValue -From 'hostie' -Reader bepinexlog `
                    -ReaderArgs @{ contains = $name } `
                    -Select 'count' -AtLeast 1 `
                    -Because "the deferred assembly scan on Prefab.OnPrefabsLoaded is what withholds PatchAll, and it names each conflict separately; a banner without this line would mean the banner fired for some other reason"
            }
            Assert-RigValue -From 'hostie' -Reader bepinexlog `
                -ReaderArgs @{ contains = 'SprayPaintPlus NOT LOADED' } `
                -Select 'count' -AtLeast 1 `
                -Because 'the permanent record of a refused load is this line in the log, which is what a player is pointed at when the banner stops repeating'

            # ---- 5. Nothing may be announced while the player is at the menu.
            # The boot line above has already been printed by now, so counting
            # from here separates it from the six that must wait for a world.
            $seqMenu = Read-RigValue -From 'hostie' -Reader console -ReaderArgs @{ limit = 1 } -Select 'nextSeq'
            Wait-PlaytestSeconds 15

            Assert-RigValue -From 'hostie' -Reader console `
                -ReaderArgs @{ since = "$($seqMenu.Value)"; source = 'console'; contains = $bannerLine; limit = 200 } `
                -Select 'count' -Is 0 `
                -Because 'the banner waits on PlayerMessage.WaitForWorld, and 15 seconds at the menu is three of its five-second intervals: a line here means the wait is not working and the whole banner would play to an empty room again'

            # ---- 6. Into a world, and then the six lines. The wait for a world
            # releases when GameManager.GameState leaves None, which is when
            # loading STARTS, so some of the six can land during the load. That
            # is what the code does and is what is asserted: none at the menu,
            # all six once it has left the menu.
            Invoke-RigAction -On 'hostie' -Path '/host' -Body @{ world = 'Lunar' } -Blocking | Out-Null
            Wait-RigStage -Name 'hostie' -Stage 'inWorld' -WaitSeconds 600 | Out-Null

            Assert-RigValue -From 'hostie' -Reader status -Select 'hosting' -Is $true `
                -Because 'NetworkServer.Host() gives up quietly after three failed binds, so a world that came up without hosting would still run the banner and would still be the wrong arrangement to have measured'

            # Six lines at five second intervals, plus slack for a busy load.
            Wait-PlaytestSeconds 45

            Assert-RigValue -From 'hostie' -Reader console `
                -ReaderArgs @{ since = "$($seqMenu.Value)"; source = 'console'; contains = $bannerLine; limit = 200 } `
                -Select 'count' -Is 6 `
                -Because 'the banner is bounded at ConflictBannerRepeats and that bound is the point: the old form was an unbounded Debug.LogError every five seconds that the console re-printed in red with a stack trace and that no player could silence'

            Assert-RigValue -From 'hostie' -Reader console `
                -ReaderArgs @{ since = "$($seqMenu.Value)"; source = 'console'; contains = $lastLine; limit = 200 } `
                -Select 'count' -Is 1 `
                -Because 'the last line has to say it is the last one and point at the log, because a banner that simply stops is indistinguishable from the mod having crashed'
        }
        finally {
            # ---- Remove the fixture. This runs whatever happened above, and it
            # is the most important part of this file: the between-session state
            # reset keeps userdata/mods/ and modconfig.xml, so a stub left here
            # would disable Spray Paint Plus on every later run of this instance
            # with nothing to say why.
            $notes = @()
            if ($configBackup) {
                try {
                    Set-Content -LiteralPath $modConfig -Value $configBackup -Encoding utf8 -NoNewline
                    $notes += 'modconfig.xml restored from the pre-seed snapshot'
                }
                catch { $notes += "modconfig.xml RESTORE FAILED: $($_.Exception.Message)" }
            }
            if ($seeded) {
                try {
                    Remove-Item -LiteralPath $stubFolder -Recurse -Force -ErrorAction Stop
                    $notes += 'stub folder deleted'
                }
                catch { $notes += "stub folder DELETE FAILED: $($_.Exception.Message)" }
            }

            # Verify the removal rather than trusting it, and say so out loud
            # either way. A silent cleanup is indistinguishable from none.
            $stubGone = -not (Test-Path -LiteralPath $stubFolder)
            $configClean = $true
            try { $configClean = ((Get-Content -Raw -LiteralPath $modConfig) -notmatch 'ConflictStub') }
            catch { $configClean = $false }
            $notes += "verify: stubFolderGone=$stubGone modConfigClean=$configClean"

            if (-not $stubGone -or -not $configClean) {
                $warning = "CONFLICT STUB NOT FULLY REMOVED from instance 'hostie'. It disables Spray Paint Plus on every later run of that instance and the state reset does NOT clear it. Delete $stubFolder and the ConflictStub <Local> entry in $modConfig, or re-provision: client-rig.ps1 -Provision -Force -As <id> -Instance hostie"
                $ctx.TeardownNotes += $warning
                Write-Warning "[Playtest] $warning"
            }

            Write-PlaytestEvidence -Name 'conflict-stub-cleanup.txt' -Content (@(
                "cleanedAt : $(Get-PlaytestStamp)"
                "stub      : $stubFolder"
                "modconfig : $modConfig"
                ''
                ($notes -join "`n")
            ) -join "`n") | Out-Null
        }
    }
