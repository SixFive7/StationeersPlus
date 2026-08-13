<#
    The client-instance half of TestRig/testrig.ps1.

    This file was TestRig/ClientRig/client-rig.ps1. It provisions and drives N
    isolated Stationeers game clients; the other half of the pair is the
    ClientDriver plugin, which is the control plane inside each instance.

    The boundary between launcher and plugin is process creation. This file owns
    everything outside a game process, and everything that must keep working when
    a process is dead or wedged: building an instance tree, the isolated Win32
    desktop, starting and stopping, PID files, and fanning one request across the
    rig. The plugin owns everything inside a process, which is everything needing
    the Unity main thread or the game's own types. There is no third category.

    An instance is a hard-linked copy of the developer's real install on the same
    NTFS volume, so it costs a few megabytes instead of seven gigabytes. Nothing
    the game or a mod writes to is ever a hard link, because a hard link shares the
    file data and a write would reach back into the developer's install.

    Every instance runs on a separate Win32 desktop that is created but never
    switched to. That is what stops the game taking the developer's foreground: the
    no-activate show flag alone loses (measured 40 focus steals out of 40 samples),
    a separate desktop wins (0 out of 55).

    The source install is treated as strictly read-only.

    Everything here is a function. There is no param block and no dispatch: the
    verb surface, the target resolution and the refusal matrix live in testrig.ps1,
    and the session lock lives in TestRig/rig-lock.ps1.

    Script variables are prefixed Cli because both halves share one script scope
    once testrig.ps1 has dot-sourced them, and both halves used to declare a
    $script:CliDataDir and a $script:CliRepoRoot of their own.

    Operating manual: TestRig/MANUAL.md.
    Durable internals: TestRig/RESEARCH.md.
    Rig conventions:   TestRig/CLAUDE.md.
#>

function Initialize-RigClient {
    <#
        Point this half at a TestRig-shaped root.

        -InstancesRoot is the launcher flag, and -InstancesRootTyped says whether
        the caller actually typed it, which is a different question from whether it
        has a value: a typed root wins over the one recorded in an instance's
        registry entry (that is how a tree gets moved), and an untyped one must not.
        That distinction used to be read out of $PSBoundParameters, which is
        per-scope and therefore empty inside any function that asked.
    #>
    param(
        [Parameter(Mandatory)] [string] $RigHome,
        [string] $InstancesRoot,
        [switch] $InstancesRootTyped
    )
    $script:CliRigHome  = $RigHome
    $script:CliRoot     = Join-Path $RigHome 'ClientRig'
    $script:CliRepoRoot = Split-Path -Parent $RigHome

    # Per-instance state (manifest, settings, save root, logs, PID file) is
    # ordinary files, not links, so it stays under the rig folder regardless of
    # which volume the trees are on.
    $script:CliDataDir  = Join-Path $script:CliRoot 'data'
    $script:CliRegistry = Join-Path $script:CliDataDir 'rig.json'

    # Dev-plugin layout, identical to the server half's: dev-plugins/<Name>/<Name>.sln
    # beside dev-plugins/<Name>/<Name>/ source. See TestRig/CLAUDE.md.
    $script:CliPluginSln = Join-Path $script:CliRoot 'dev-plugins\ClientDriver\ClientDriver.sln'
    $script:CliPluginDll = Join-Path $script:CliRoot 'dev-plugins\ClientDriver\ClientDriver\bin\Release\ClientDriver.dll'

    # The instance trees are hard links into the game install, so they must sit on
    # the install's volume. The repository frequently does not, so this is
    # relocatable and the volume check turns a wrong setting into a clear message
    # rather than a 7 GB copy.
    $resolved = Get-RigDefaultInstancesRoot -Override $InstancesRoot
    $script:CliInstancesDir       = $resolved.Root
    $script:CliInstancesDirSource = $resolved.Source
    $script:CliInstancesRootTyped = [bool]$InstancesRootTyped

    # Port bands and timeouts come from lib/common.ps1 so the two halves cannot
    # disagree about the dedicated server's ports. They are copied into script
    # scope here because they appear inside message strings all over this file.
    $script:CliControlPortBase = Get-RigControlPortBase
    $script:CliGamePortBase    = Get-RigGamePortBase
    $script:CliReservedGamePorts = Get-RigReservedGamePorts

    # Control-plane HTTP timeout bounds. Invoke-RestMethod defaults to 100 s and
    # this launcher used to pin 120 s, which meant the CALLER's own timeoutMs was
    # ignored: -Path /connect with timeoutMs 300000 died at 120 s and the plugin's
    # answer, the only thing that says WHY a join or a host attempt failed, was
    # never read. The timeout is derived from the request instead, between these.
    $script:CliControlTimeoutFloorSeconds   = $script:RigControlTimeoutFloorSeconds
    $script:CliControlTimeoutMarginSeconds  = $script:RigControlTimeoutMarginSeconds
    $script:CliControlTimeoutCeilingSeconds = $script:RigControlTimeoutCeilingSeconds
    $script:CliControlLongPathSeconds       = $script:RigControlLongPathSeconds
    $script:CliControlLongPaths             = $script:RigControlLongPaths

    $script:RootFallbackAnnounced = @{}

    # Both shared libraries default to the rig root, which is right for everything
    # except the instance tree location: the instances root is a launcher flag
    # neither of them can see. Re-point them here, so the reset looks inside the
    # trees this rig actually has and the lock's orphan scan watches the same ones.
    # Initialize-RigResetPaths re-points the lock library too.
    #
    # A RECORDED root wins over the launcher default here, and only here: the
    # shared libraries take ONE root, and a rig whose instances were built under an
    # explicit root has its trees there whether or not this shell happens to have
    # the environment variable set. $script:CliInstancesDir itself is deliberately
    # NOT touched, so the launcher's own fallback for an entry that records nothing
    # stays what it was and the note it prints names the real source.
    $libInstanceRoot = $script:CliInstancesDir
    if (-not $script:CliInstancesRootTyped) {
        $recordedRoots = @(Read-Registry |
            ForEach-Object { [string](Get-EntryValue $_ 'instancesRoot' '') } |
            Where-Object { $_ } | Select-Object -Unique)
        if ($recordedRoots.Count -ge 1) { $libInstanceRoot = $recordedRoots[0] }
    }
    Initialize-RigResetPaths -RigHome $RigHome -InstanceRoot $libInstanceRoot `
        -ServerImageName (Get-RigServerImageName) -ClientImageName (Get-RigClientImageName)
}

function Get-RigClientInstancesDir       { return $script:CliInstancesDir }
function Get-RigClientInstancesDirSource { return $script:CliInstancesDirSource }
function Get-RigClientRegistryPath       { return $script:CliRegistry }
function Get-RigClientPluginDllPath      { return $script:CliPluginDll }

# ---- environment helpers --------------------------------------------------

function Assert-SameVolume {
    param([string] $A, [string] $B)
    $ra = [IO.Path]::GetPathRoot((Resolve-Path $A).Path)
    $rb = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($B))
    if ($ra -ne $rb) {
        throw @"
Instance trees must be on the same NTFS volume as the game install, because hard links cannot cross
volumes and a real copy would cost about 7 GB per instance.
    game install    : $ra
    instances would be: $rb  ($B)
Point the rig at a folder on '$ra':
    `$env:STATIONEERS_CLIENTRIG_ROOT = '${ra}StationeersRig'
or pass -InstancesRoot '${ra}StationeersRig' on every call. Record the choice in DEV.md.
"@
    }
}

# The pid reader and the "is that pid alive" check used to live here, one copy per
# launcher plus the library's. Both launcher copies cast with [int], which THROWS
# on a corrupt pid file where the library's TryParse returns $null; the server
# half's liveness check had no image test at all, so a recycled process id made it
# refuse to start and report a dead server as up. Both are gone. This half now
# calls Get-RigPidFromFile (rig-lock.ps1) and Test-RigClientProcessAlive
# (lib/common.ps1), which is the same image-checking implementation the lock's busy
# probe and the state reset already used.

# ---- session lock ---------------------------------------------------------
#
# Rules: TestRig/CLAUDE.md. Implementation: TestRig/rig-lock.ps1,
# dot-sourced by testrig.ps1 before this file and shared with the server half.
#
# Every action that changes rig state goes through this gate, for the same reason the dedicated
# server has one. Without it, a stop of the whole rig tears down another agent's live test with no
# trace, a remove deletes an instance's save root out from under a run, and two concurrent creates
# read the registry before either writes it, pick the same free index, and hand two instances one
# ClientId. That last one is the failure this file already refuses to allow within a single call,
# and for the same stated reason: the server keys a player's body on ClientId, so a test that
# believes it has two players actually has one, and the results look plausible and mean nothing.

function Assert-RigClientMutatingAllowed {
    param(
        [Parameter(Mandatory)] [string] $Action,
        [string] $As
    )
    Assert-RigLockHeld -Action $Action -CallerId $As -Tool 'testrig.ps1'
}

# ---- the rig registry -----------------------------------------------------
#
# One file listing every instance. It is what makes -All work, and it is where each instance's
# manifest gets its peerPorts list, which is what lets an instance notice a sibling claiming the
# same ClientId.

function Read-Registry {
    if (-not (Test-Path $script:CliRegistry)) { return @() }
    try {
        $json = Get-Content -Raw $script:CliRegistry | ConvertFrom-Json
        if ($null -eq $json) { return @() }
        return @($json)
    }
    catch {
        Write-Warning "rig.json could not be parsed ($($_.Exception.Message)); treating the rig as empty."
        return @()
    }
}

function Write-Registry {
    param([Parameter(Mandatory)] $Entries)
    New-Item -ItemType Directory -Force -Path $script:CliDataDir | Out-Null
    $tmp = "$script:CliRegistry.tmp"
    ,@($Entries) | ConvertTo-Json -Depth 8 | Set-Content -Path $tmp -Encoding utf8
    Move-Item -Path $tmp -Destination $script:CliRegistry -Force
}

function Get-InstanceEntry {
    param([Parameter(Mandatory)] [string] $Name)
    Read-Registry | Where-Object { $_.instanceName -eq $Name } | Select-Object -First 1
}

function Get-EntryValue {
    # One field out of a registry entry, with a default for the fields that did not exist when the
    # entry was written. 'role' and 'gamePort' are exactly that case: a rig provisioned before
    # hosting existed has neither, and every reader has to cope rather than assume.
    param($Entry, [Parameter(Mandatory)] [string] $Field, $Default = $null)
    if ($null -eq $Entry) { return $Default }
    $prop = $Entry.PSObject.Properties[$Field]
    if (-not $prop) { return $Default }
    if ($null -eq $prop.Value -or '' -eq [string]$prop.Value) { return $Default }
    return $prop.Value
}

function Get-RigClientEntries {
    <#
        Registry entries for a set of instance names, in registry order.

        testrig.ps1 has already turned -Target into names by the time this is
        called; this is the lookup, and the refusal when a name matches nothing.
        The old version took the target set off script scope, which is what let a
        typo widen the blast radius: an unknown -Instance once fell through to
        stopping the whole rig, because the local holding the registry was named
        $all and PowerShell variable names are case-insensitive, so it overwrote the
        -All switch. Names come in as a parameter now and there is no switch to
        overwrite.
    #>
    param([string[]] $Names = @(), [switch] $All)
    $registry = @(Read-Registry)
    if ($All) { return $registry }
    $hits = New-Object System.Collections.Generic.List[object]
    foreach ($w in @($Names)) {
        $e = $registry | Where-Object { $_.instanceName -eq $w } | Select-Object -First 1
        if (-not $e) {
            $known = (@($registry | ForEach-Object { $_.instanceName }) -join ', ')
            if (-not $known) { $known = '(none)' }
            throw "Instance '$w' is not provisioned. Known instances: $known. Create it with: testrig create -Target $w [-Role host], or list them with: testrig list"
        }
        $hits.Add($e)
    }
    return $hits.ToArray()
}

$script:RootFallbackAnnounced = @{}

function Resolve-InstanceRoot {
    # Where THIS instance's tree lives, and where that answer came from.
    #
    # The recorded root is what makes -InstancesRoot stick. -Provision writes the resolved root into
    # the registry entry, and every later action reads it back, so the flag is typed once rather than
    # on every command. Before that, -Provision honoured the flag and -Start, -Stop, -Call and the
    # state reset all fell back to instances/ beside this script: -Start reported a provisioned
    # instance as having no tree at a path nothing had ever built, and the reset found no BepInEx
    # config to re-copy and said only that there was no tree.
    #
    # Precedence, and why:
    #   1. -InstancesRoot as TYPED on this command. An explicit flag has to win, or a tree could
    #      never be moved.
    #   2. The root recorded in the registry entry.
    #   3. The launcher default (-InstancesRoot from a default, then the environment variable, then
    #      instances/). This is the pre-existing behaviour and it is what an entry written before the
    #      field existed gets, with a note rather than a throw: an old rig must keep working.
    param(
        [Parameter(Mandatory)] [string] $Name,
        $Entry,
        [switch] $NoEntryLookup
    )
    if ($script:CliInstancesRootTyped) {
        return [pscustomobject]@{ Root = $script:CliInstancesDir; Source = '-InstancesRoot (typed on this command)' }
    }
    if (-not $PSBoundParameters.ContainsKey('Entry') -and -not $NoEntryLookup) {
        $Entry = Get-InstanceEntry -Name $Name
    }
    $recorded = [string](Get-EntryValue $Entry 'instancesRoot' '')
    if ($recorded) {
        return [pscustomobject]@{ Root = $recorded; Source = 'recorded in the registry at provision time' }
    }
    if ($Entry -and -not $script:RootFallbackAnnounced.ContainsKey($Name)) {
        $script:RootFallbackAnnounced[$Name] = $true
        Write-Host "[Rig] Instance '$Name' was provisioned before the instances root was recorded; using $($script:CliInstancesDirSource) ($($script:CliInstancesDir)). Re-record it with: testrig create -Target $Name -Force -As <id>"
    }
    return [pscustomobject]@{ Root = $script:CliInstancesDir; Source = $script:CliInstancesDirSource }
}

function Get-InstancePaths {
    # -Entry is an optimisation, not a second code path: pass the registry entry a caller already
    # has and the root resolution does not re-read rig.json. -Root is the provisioning override,
    # used once, when the entry does not exist yet.
    param(
        [Parameter(Mandatory)] [string] $Name,
        $Entry,
        [string] $Root
    )
    if ($Root) {
        $resolved = [pscustomobject]@{ Root = $Root; Source = 'this provision' }
    }
    elseif ($PSBoundParameters.ContainsKey('Entry')) {
        $resolved = Resolve-InstanceRoot -Name $Name -Entry $Entry
    }
    else {
        $resolved = Resolve-InstanceRoot -Name $Name
    }
    $tree = Join-Path $resolved.Root $Name
    [pscustomobject]@{
        Name       = $Name
        Tree       = $tree
        Exe        = Join-Path $tree 'rocketstation.exe'
        BepInEx    = Join-Path $tree 'BepInEx'
        Root       = $resolved.Root
        RootSource = $resolved.Source
        Data       = Join-Path $script:CliDataDir $Name
        Manifest   = Join-Path $script:CliDataDir "$Name\instance.json"
        PidFile    = Join-Path $script:CliDataDir "$Name\game.pid"
        Settings   = Join-Path $script:CliDataDir "$Name\setting.xml"
        UserData   = Join-Path $script:CliDataDir "$Name\userdata"
        LogDir     = Join-Path $script:CliDataDir "$Name\logs"
    }
}

# The shared libraries are re-pointed at the rig's real instance root inside
# Initialize-RigClient, which testrig.ps1 calls after dot-sourcing this file. It
# used to happen here, at dot-source time, which meant the reset's view of the rig
# depended on the ORDER the launcher's own statements ran in.
#
# The reset resolves each instance's tree from the registry itself, so a rig split
# across two roots (only reachable by moving one instance with -InstancesRoot)
# still resets correctly; what the single value costs there is the orphan scan
# missing a stray process out of the second root, which is a reporting gap rather
# than a safety one.

# ---- create (provisioning) ------------------------------------------------

function Copy-LinkedTree {
    param(
        [Parameter(Mandatory)] [string] $SrcDir,
        [Parameter(Mandatory)] [string] $DstDir,
        [string[]] $RealCopyRelative = @()
    )
    New-Item -ItemType Directory -Force -Path $DstDir | Out-Null
    Get-ChildItem $SrcDir -Recurse -Directory | ForEach-Object {
        $rel = $_.FullName.Substring($SrcDir.Length).TrimStart('\')
        New-Item -ItemType Directory -Force -Path (Join-Path $DstDir $rel) | Out-Null
    }
    Get-ChildItem $SrcDir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($SrcDir.Length).TrimStart('\')
        $target = Join-Path $DstDir $rel
        if ($RealCopyRelative -contains $rel) {
            Copy-Item $_.FullName $target -Force
            $script:copiedFiles++; $script:copiedBytes += $_.Length
        } else {
            New-Item -ItemType HardLink -Path $target -Value $_.FullName | Out-Null
            $script:linkedFiles++; $script:linkedBytes += $_.Length
        }
    }
}

function Assert-GamePortFree {
    # The game-port equivalent of the control-plane port refusal above, and it matters more.
    #
    # A second TCP listener on a taken port fails loudly, so the control-plane check is mostly
    # book-keeping. RakNet does not behave that way: two UDP bindings on one port coexist, and which
    # socket receives a datagram is decided by its destination address, not by who bound first.
    # Nothing errors, nothing warns, and the joiner ends up talking to whichever binding won. The
    # test then passes or fails against a session nobody chose. That failure is invisible from
    # inside the game, so it has to be refused here, before anything is launched.
    param(
        # NOT Mandatory, and null is allowed on purpose. Read-Registry returns an
        # empty collection on a rig with no data/rig.json yet, and PowerShell
        # collapses that to $null on the way into a parameter, so Mandatory would
        # reject the very first -Provision on a fresh rig.
        [AllowNull()] [AllowEmptyCollection()] $Registry = @(),
        [Parameter(Mandatory)] [string] $InstanceName,
        [Parameter(Mandatory)] [int] $Candidate
    )
    if ($Candidate -lt 1024 -or $Candidate -gt 65535) {
        throw "-GamePort $Candidate is out of range. Use 1024-65535; the rig's own band is $script:CliGamePortBase plus the instance index."
    }
    if ($script:CliReservedGamePorts.ContainsKey($Candidate)) {
        throw "-GamePort $Candidate is $($script:CliReservedGamePorts[$Candidate]). Two RakNet sockets on one port do not conflict, they coexist and route by destination address, so a joiner would reach whichever one won and the test would be wrong with no error anywhere. Pick another port; the rig's own band is $script:CliGamePortBase plus the instance index."
    }
    foreach ($e in $Registry) {
        if ($e.instanceName -eq $InstanceName) { continue }
        $peerGame = [int](Get-EntryValue $e 'gamePort' 0)
        if ($peerGame -eq $Candidate) {
            throw "-GamePort $Candidate is already used by instance '$($e.instanceName)'. Two instances sharing a game port coexist silently and route by destination address; pick a different -GamePort."
        }
        if ([int]$e.port -eq $Candidate) {
            throw "-GamePort $Candidate is instance '$($e.instanceName)' control-plane port. They are different protocols so the bind would succeed, but every later reading of that port is then ambiguous. Pick a different -GamePort."
        }
    }
}

function Invoke-RigClientCreate {
    <#
        Build or rebuild ONE instance: hard-link the game tree, point it at its own
        save root, seed its mod set, write its manifest and a provision stamp.

        A rebuild (-Force) replaces the instance TREE. It does NOT reset
        data/<instance>/: the save root, the logs, the PID file and the game-written
        setting.xml all survive, and only userdata/mods is rewritten. That is
        deliberate (a staged save must not evaporate on a plugin rebuild) but it
        does mean a rebuild is not a clean slate. Stopping an instance clears
        StartLocalHost out of setting.xml for the one case where a stale value would
        silently change what the next run is.

        -Typed is the caller's "which flags were actually passed" map. It has to be
        a parameter because $PSBoundParameters is per-scope: a function gets its own
        empty copy, and every ContainsKey test inside one silently answers false.
        -Role and -GamePort need it so a rebuild does not demote a host or move its
        port out from under a joiner.
    #>
    param(
        [string] $As,
        [Parameter(Mandatory)] [string] $Instance,
        [switch] $Force,
        [hashtable] $Typed = @{},
        [string] $Role = 'client',
        [int] $Port = 0,
        [int] $GamePort = 0,
        [string] $ClientId,
        [string] $Username,
        [int] $Width = 800,
        [int] $Height = 600,
        [bool] $ForceGameplayInput = $true,
        [bool] $SeedMods = $true,
        [string] $Desktop = 'StationeersRig'
    )
    if ($Instance.Contains(',')) { throw "'create' takes one instance at a time." }
    # Held across the whole read-modify-write of the registry below, which is what stops two
    # concurrent creates from selecting the same index and therefore the same ClientId.
    Assert-RigClientMutatingAllowed -Action 'create' -As $As

    $source = Get-RigStationeersPath

    # Index decides the defaults for port and identity, so provisioning three instances with no
    # flags produces three distinct, non-colliding ones. Read before the paths, because a rebuild
    # takes its instances root from the existing entry.
    $registry = Read-Registry
    $existing = $registry | Where-Object { $_.instanceName -eq $Instance } | Select-Object -First 1
    $index = if ($existing) { [int]$existing.index } else {
        $used = @($registry | ForEach-Object { [int]$_.index })
        $i = 1; while ($used -contains $i) { $i++ }; $i
    }

    # THE root this instance is built in, and the value that goes into the registry entry so every
    # later action finds the tree without the flag being typed again. A rebuild keeps the recorded
    # root for the same reason it keeps -Role and -GamePort: -Provision -Force is the routine way to
    # pick up a new plugin build, and relocating an instance in passing would be a trap.
    $recordedRoot = [string](Get-EntryValue $existing 'instancesRoot' '')
    $effRoot = if ($script:CliInstancesRootTyped) { $script:CliInstancesDir }
               elseif ($recordedRoot)   { $recordedRoot }
               else                     { $script:CliInstancesDir }
    if ($script:CliInstancesRootTyped -and $recordedRoot -and $recordedRoot -ne $effRoot) {
        Write-Warning "[Provision] '$Instance' was built under $recordedRoot and -InstancesRoot moves it to $effRoot. The old tree at $(Join-Path $recordedRoot $Instance) is NOT deleted (this launcher only ever removes the tree it is about to rebuild); delete it by hand once the rebuild succeeds."
    }

    $p = Get-InstancePaths -Name $Instance -Root $effRoot
    if ((Test-Path $p.Tree) -and -not $Force) {
        throw "Instance '$Instance' already exists at $($p.Tree). Pass -Force to rebuild it, or delete it first: testrig remove -Target $Instance -As <id>"
    }
    if (Test-RigClientProcessAlive (Get-RigPidFromFile $p.PidFile)) {
        throw "Instance '$Instance' is running. Stop it first: testrig stop -Target $Instance -As <id>"
    }

    $effPort = if ($Port -gt 0) { $Port } else { $script:CliControlPortBase + $index }
    $effId   = if ($ClientId) { $ClientId } else { (900000000000 + $index).ToString() }
    $effName = if ($Username) { $Username } else { $Instance }

    # Role and game port are KEPT across a rebuild unless they are typed again. -Provision -Force is
    # the routine way to pick up a new plugin build, and silently demoting a host to a client (or
    # moving its game port out from under a joiner's -Body) on the way through would be a trap.
    $effRole = if ($Typed.ContainsKey('Role')) { $Role }
               elseif ($existing) { [string](Get-EntryValue $existing 'role' $Role) }
               else { $Role }
    $effGamePort = if ($GamePort -gt 0) { $GamePort }
                   elseif ($existing) { [int](Get-EntryValue $existing 'gamePort' ($script:CliGamePortBase + $index)) }
                   else { $script:CliGamePortBase + $index }

    [uint64]$parsedId = 0
    if (-not [uint64]::TryParse($effId, [ref]$parsedId)) {
        throw "-ClientId '$effId' is not a decimal ulong."
    }
    if ($parsedId -eq 0) {
        throw "-ClientId 0 is the batch-mode sentinel and would collide with every other zero-id client. Pick a non-zero value."
    }
    $clash = $registry | Where-Object { $_.instanceName -ne $Instance -and $_.clientId -eq $effId } | Select-Object -First 1
    if ($clash) {
        throw "ClientId $effId is already used by instance '$($clash.instanceName)'. The server keys a player's body on this id, so both instances would resolve onto one character. Pick a different -ClientId."
    }
    $portClash = $registry | Where-Object { $_.instanceName -ne $Instance -and [int]$_.port -eq $effPort } | Select-Object -First 1
    if ($portClash) {
        throw "Port $effPort is already used by instance '$($portClash.instanceName)'. Pick a different -Port."
    }
    Assert-GamePortFree -Registry $registry -InstanceName $Instance -Candidate $effGamePort

    # Checked here, after the cheap identity and port guards, so a name clash is reported before a
    # volume misconfiguration and the caller fixes one thing at a time. It checks the root this
    # provision will actually build in, which on a rebuild is the recorded one.
    Assert-SameVolume -A $source -B $effRoot

    if (Test-Path $p.Tree) {
        Write-Host "[Provision] Removing existing tree $($p.Tree) ..."
        Remove-Item $p.Tree -Recurse -Force
    }

    $script:linkedFiles = 0; $script:copiedFiles = 0
    $script:linkedBytes = 0; $script:copiedBytes = 0

    New-Item -ItemType Directory -Force -Path $p.Tree, $p.Data, $p.UserData, $p.LogDir | Out-Null

    # Files the game or a mod writes into the install root. These must NEVER be hard links: a hard
    # link shares the file data, so a write here would reach into the developer's install.
    $realCopyRootFiles = @('doorstop_config.ini', 'Fixing The Controls modifiers.ini')
    # Regenerated, and resolved relative to the working directory, so not worth carrying.
    $skipRootFiles     = @('imgui.ini', 'output_log.txt')

    Write-Host "[Provision] Linking rocketstation_Data ..."
    # app.info is a real copy purely so a write cannot reach the source. It is NOT a
    # persistentDataPath lever: the player takes company and product from the serialized
    # PlayerSettings inside globalgamemanagers, and editing app.info changes nothing.
    Copy-LinkedTree -SrcDir (Join-Path $source 'rocketstation_Data') -DstDir (Join-Path $p.Tree 'rocketstation_Data') -RealCopyRelative @('app.info')

    Write-Host "[Provision] Linking MonoBleedingEdge ..."
    Copy-LinkedTree -SrcDir (Join-Path $source 'MonoBleedingEdge') -DstDir (Join-Path $p.Tree 'MonoBleedingEdge')

    Write-Host "[Provision] Copying BepInEx (real copy: config, plugins, cache and logs must be per-instance) ..."
    Copy-Item (Join-Path $source 'BepInEx') (Join-Path $p.Tree 'BepInEx') -Recurse -Force
    # A separate BepInEx root is what buys per-instance config, plugins, cache, LogOutput.log and
    # InspectorPlus request/snapshot folders in one move. The BepInEx root is always
    # <dir of rocketstation.exe>\BepInEx and no environment variable relocates it.
    Remove-Item (Join-Path $p.Tree 'BepInEx\LogOutput.log*') -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $p.Tree 'BepInEx\cache')     -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $p.Tree 'BepInEx\inspector') -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path (Join-Path $p.Tree 'BepInEx\cache') | Out-Null
    $bep = Get-ChildItem (Join-Path $p.Tree 'BepInEx') -Recurse -File | Measure-Object Length -Sum
    $script:copiedFiles += $bep.Count; $script:copiedBytes += $bep.Sum

    Write-Host "[Provision] Handling root files ..."
    Get-ChildItem $source -File | ForEach-Object {
        if ($skipRootFiles -contains $_.Name) { return }
        $target = Join-Path $p.Tree $_.Name
        if ($realCopyRootFiles -contains $_.Name) {
            Copy-Item $_.FullName $target -Force
            $script:copiedFiles++; $script:copiedBytes += $_.Length
        } else {
            New-Item -ItemType HardLink -Path $target -Value $_.FullName | Out-Null
            $script:linkedFiles++; $script:linkedBytes += $_.Length
        }
    }

    Invoke-DeployPlugin -Paths $p

    # Unconditional, and BEFORE the mod seed. It used to live at the end of Invoke-SeedMods, behind
    # that function's early return for a developer with no modconfig.xml, so an instance provisioned
    # on such a machine (or with -SeedMods:$false) got no save redirect at all and wrote into the
    # developer's tier-1 user-data folder, behind a warning whose text only mentioned mods. The
    # redirect is what keeps a driven session out of the developer's saves; it has nothing to do
    # with mods and must not be able to be skipped by anything mod-related.
    Set-RigSavePathOverride -BepInExDir $p.BepInEx -UserDataDir $p.UserData `
        -InstanceRole $effRole -InstanceName $p.Name -Context 'Provision' | Out-Null

    if ($SeedMods) { Invoke-SeedMods -Paths $p }

    # Register before writing manifests, because every manifest carries the whole rig's port list.
    #
    # instancesRoot is what makes -InstancesRoot stick past this command. It is the RESOLVED root
    # this tree was built in, so -Start, -Stop, -Call, -Remove and the state reset all find the tree
    # without the flag being re-passed, and an instance built on another volume stops being reported
    # as unprovisioned. Entries written before this field existed simply do not have it and fall
    # back to the launcher's resolution order; see Resolve-InstanceRoot.
    $entry = [pscustomobject]@{
        instanceName = $Instance
        index        = $index
        role         = $effRole
        port         = $effPort
        gamePort     = $effGamePort
        clientId     = $effId
        username     = $effName
        width        = $Width
        height       = $Height
        forceGameplayInput = [bool]$ForceGameplayInput
        instancesRoot = $effRoot
        provisionedUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    }
    $registry = @($registry | Where-Object { $_.instanceName -ne $Instance }) + $entry
    Write-Registry $registry
    Write-AllManifests -Desktop $Desktop
    Write-ProvisionStamp -Paths $p -Entry $entry -SourceInstall $source

    Write-Host ""
    Write-Host "[Provision] Instance '$Instance' built."
    Write-Host ("[Provision]   hard-linked : {0,6} files, {1,8:N1} MB shared (near-zero new disk)" -f $script:linkedFiles, ($script:linkedBytes/1MB))
    Write-Host ("[Provision]   real copies : {0,6} files, {1,8:N1} MB new disk" -f $script:copiedFiles, ($script:copiedBytes/1MB))
    Write-Host "[Provision]   role        : $effRole"
    Write-Host "[Provision]   port        : $effPort  (control plane, TCP, loopback only)"
    Write-Host "[Provision]   gamePort    : $effGamePort  (RakNet, UDP)"
    Write-Host "[Provision]   clientId    : $effId"
    Write-Host "[Provision]   username    : $effName"
    Write-Host "[Provision]   tree        : $($p.Tree)  (root recorded in the registry; later commands need no -InstancesRoot)"
    Write-Host "[Provision]   saveRoot    : $($p.UserData)"
    Write-Host "[Provision]   manifest    : $($p.Manifest)"
    if ($effRole -eq 'host') {
        Write-Host "[Provision] Next: testrig start -Target $Instance, testrig wait -Target $Instance -Stage menu,"
        Write-Host "[Provision]       then testrig call -Target $Instance -Path /host -Body '{`"world`":`"Lunar`"}'."
        Write-Host "[Provision]       Joiners reach it at 127.0.0.1:$effGamePort, and the host must be in its world BEFORE any joiner connects."
    }
    else {
        Write-Host "[Provision] Next: testrig start -Target $Instance -As <id>"
    }
    return $entry
}

function Invoke-DeployPlugin {
    param([Parameter(Mandatory)] $Paths)
    if (-not (Test-Path $script:CliPluginDll)) {
        Write-Warning "[$($Paths.Name)] ClientDriver.dll not found at $($script:CliPluginDll). Build it first: dotnet build $($script:CliPluginSln) -c Release. The instance will run without a control plane."
        return
    }
    $dst = Join-Path $Paths.BepInEx 'plugins\ClientDriver'
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item $script:CliPluginDll (Join-Path $dst 'ClientDriver.dll') -Force
    # BepInEx/plugins/ is loaded by the Chainloader directly, before StationeersLaunchPad runs.
    # The DLL must not ALSO sit under a StationeersLaunchPad mod folder: two loaders means Awake
    # twice and every Harmony patch registered twice.
    Write-Host "[Provision] ClientDriver -> $dst"
}

function Invoke-SeedMods {
    param([Parameter(Mandatory)] $Paths)

    $userData = Get-RigUserDataPath
    $srcMods  = Join-Path $userData 'mods'
    $srcCfg   = Join-Path $userData 'modconfig.xml'
    if (-not (Test-Path $srcCfg)) {
        Write-Warning "[$($Paths.Name)] No modconfig.xml at $srcCfg; skipping the mod seed. The instance will load Workshop mods only."
        return
    }

    Write-Host "[Provision] Seeding mods from the user data folder (read-only source) ..."
    $dstMods = Join-Path $Paths.UserData 'mods'
    $srcModsPresent = Test-Path $srcMods
    if (Test-Path $dstMods) { Remove-Item $dstMods -Recurse -Force }
    if ($srcModsPresent)    { Copy-Item $srcMods $dstMods -Recurse -Force }
    else { New-Item -ItemType Directory -Force -Path $dstMods | Out-Null }

    # Local mod entries are absolute paths, and StationeersLaunchPad prunes entries whose folder is
    # not under the active save path, so each instance needs its own copy and its own modconfig.
    #
    # Parsed and rewritten through the one shared reader and writer rather than
    # string-replaced, which is what made this a third modconfig format. DISABLED
    # entries are carried through as disabled rather than dropped: re-enabling one
    # is a normal thing to do, and the server half's bake filters them out itself.
    $entries = @(Get-RigModConfigEntries -Path $srcCfg | ForEach-Object {
        $path = [string]$_.Path
        if ($srcModsPresent -and $path -and $path.StartsWith($srcMods, [StringComparison]::OrdinalIgnoreCase)) {
            $path = $dstMods + $path.Substring($srcMods.Length)
        }
        [pscustomobject]@{ Kind = $_.Kind; Enabled = $_.Enabled; Path = $path; WorkshopId = $_.WorkshopId }
    })
    Write-RigModConfigFile -Path (Join-Path $Paths.UserData 'modconfig.xml') -Entries $entries

    foreach ($f in @('modrepos.xml', 'PlayerCosmetics_0.xml')) {
        $src = Join-Path $userData $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $Paths.UserData $f) -Force }
    }
}

# NOTE: Set-SavePathOverride used to live here. It is now Set-RigSavePathOverride in the shared
# TestRig/rig-reset.ps1, because a second caller needs it: the between-session state reset re-copies
# BepInEx/config from the source install, which WIPES SavePathOverride, and has to write it back
# through the same implementation. Two copies of the one setting that keeps a driven instance out of
# the developer's tier-1 save folder is exactly the drift that ends with somebody's saves overwritten.
# Do not re-add a local copy here.

# The local game-version reader is gone. It read a version.txt at the install root
# FIRST and fell back to the executable's Unity FileVersion. No Stationeers install
# has ever contained a version.txt, so every stamp this rig has ever written
# recorded the engine version, while the baseline recorded the version.ini string.
# The two are different strings, so nothing could compare a stamp against a
# baseline and a game update could never mark anything stale. Get-RigInstallVersion
# in lib/common.ps1 reads version.ini, which is where the number actually is.

function Write-ProvisionStamp {
    # When this instance was built, and out of what. Nothing used to record either, so "is this
    # instance stale" (game updated, plugin rebuilt, provisioned before a fix landed) had no answer
    # short of comparing file times by hand. It sits beside the manifest and is written on every
    # provision, so any later staleness check has something to key off.
    param(
        [Parameter(Mandatory)] $Paths,
        [Parameter(Mandatory)] $Entry,
        [Parameter(Mandatory)] [string] $SourceInstall
    )
    $pluginBuilt = ''
    try {
        if (Test-Path $script:CliPluginDll) {
            $pluginBuilt = (Get-Item $script:CliPluginDll).LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
        }
    } catch { }

    $stamp = [ordered]@{
        instanceName    = [string]$Entry.instanceName
        provisionedUtc  = [string]$Entry.provisionedUtc
        role            = [string]$Entry.role
        port            = [int]$Entry.port
        gamePort        = [int]$Entry.gamePort
        # Where the tree went. The registry entry is the authority every action reads; this copy is
        # for a human diagnosing an instance whose tree is not where they expected.
        tree            = [string]$Paths.Tree
        sourceInstall   = $SourceInstall
        sourceVersion   = (Get-RigInstallVersion -InstallDir $SourceInstall)
        pluginBuiltUtc  = $pluginBuilt
        launcherHostname = $env:COMPUTERNAME
    }
    $file = Join-Path $Paths.Data 'provision.stamp'
    $tmp  = "$file.tmp"
    $stamp | ConvertTo-Json -Depth 4 | Set-Content -Path $tmp -Encoding utf8
    Move-Item -Path $tmp -Destination $file -Force
    Write-Host "[Provision]   stamp       : $file (game $($stamp.sourceVersion))"
}

function Write-AllManifests {
    # Every manifest carries the whole rig's port list, so an instance can ask its siblings who
    # they are. Rewritten for every instance whenever the registry changes, which is why this is
    # one function rather than a step inside provisioning.
    param([string] $Desktop = 'StationeersRig')
    $registry = Read-Registry
    $ports = @($registry | ForEach-Object { [int]$_.port })
    foreach ($e in $registry) {
        $p = Get-InstancePaths -Name $e.instanceName -Entry $e
        New-Item -ItemType Directory -Force -Path $p.Data | Out-Null
        $manifest = [ordered]@{
            instanceName  = $e.instanceName
            # role is advisory for the plugin (it computes the LIVE role from the game's own state
            # and reports it on /status); gamePort is load-bearing, because POST /host binds it.
            role          = [string](Get-EntryValue $e 'role' 'client')
            port          = [int]$e.port
            gamePort      = [int](Get-EntryValue $e 'gamePort' 0)
            clientId      = [string]$e.clientId
            username      = [string]$e.username
            window        = [ordered]@{ forceWindowed = $true; width = [int]$e.width; height = [int]$e.height }
            gameplayInput = [ordered]@{ force = [bool]$e.forceGameplayInput; everywhere = $false }
            savePath      = $p.UserData
            desktop       = $Desktop
            rigRoot       = $script:CliRoot
            peerPorts     = $ports
        }
        $tmp = "$($p.Manifest).tmp"
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $tmp -Encoding utf8
        Move-Item -Path $tmp -Destination $p.Manifest -Force
    }
}

# ---- launching ------------------------------------------------------------

function Add-LauncherType {
    if ([System.Management.Automation.PSTypeName]'ClientRigLauncher'.Type) { return }
    Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

// Launches a process on a named Win32 desktop without activating its window.
//
// The desktop is the mechanism, not an optimisation. STARTF_USESHOWWINDOW with SW_SHOWNOACTIVATE
// alone loses: wShowWindow only governs the first ShowWindow(SW_SHOWDEFAULT), and Unity calls
// ShowWindow itself once its window exists, so the flag is ignored. Measured 40 focus steals out
// of 40 samples over two minutes. A window on another desktop cannot appear on the developer's
// screen and cannot touch their foreground or input queue at all: 0 out of 55.
//
// SwitchDesktop is deliberately NOT imported, and must never be. Switching is the one call that
// would put a driven instance in front of the developer.
//
// .NET's ProcessStartInfo cannot express either lpDesktop or wShowWindow with
// UseShellExecute = false, which is the whole reason for the P/Invoke.
public static class ClientRigLauncher
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess; public IntPtr hThread; public uint dwProcessId; public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX; public int dwY; public int dwXSize; public int dwYSize;
        public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute;
        public int dwFlags; public short wShowWindow; public short cbReserved2;
        public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(
        string lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles,
        uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateDesktopW(string desktop, string device, IntPtr devmode,
        int flags, uint accessMask, IntPtr attributes);

    const int  STARTF_USESHOWWINDOW = 0x00000001;
    const uint GENERIC_ALL = 0x10000000;

    // Creates the desktop if it does not exist, or opens it if it does. The desktop object stays
    // alive as long as a process is running on it and disappears on its own afterwards, so the
    // handle does not need to outlive this call and there is nothing to clean up.
    public static void EnsureDesktop(string name)
    {
        IntPtr h = CreateDesktopW(name, null, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (h == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    // CreateProcessW may write into lpCommandLine, so it has to be a mutable buffer.
    public static uint Start(string exe, string commandLine, string workingDir, short showWindow, string desktop)
    {
        STARTUPINFO si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.dwFlags = STARTF_USESHOWWINDOW;
        si.wShowWindow = showWindow;
        if (!string.IsNullOrEmpty(desktop)) si.lpDesktop = desktop;

        StringBuilder cmd = new StringBuilder(commandLine, commandLine.Length + 64);

        PROCESS_INFORMATION pi;
        if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, workingDir, ref si, out pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        uint procId = pi.dwProcessId;
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return procId;
    }
}
'@
}

function Invoke-RigClientStart {
    <#
        Launch each selected instance on the rig's isolated desktop.

        A client instance boots TO THE MENU and no further. It has no way to take a
        world on its command line (-settings SavePath is forbidden here, because
        StationeersLaunchPad then rewrites the developer's shared modconfig.xml with
        every Local entry deleted), so entering a world is a separate step over the
        control plane. That is the opposite of the dedicated server, which cannot
        start without a world at all, and it is why the two halves cannot share one
        meaning for 'start'.
    #>
    param(
        [string] $As,
        $Entries = @(),
        [string] $Desktop = 'StationeersRig',
        [int] $Width = 800,
        [int] $Height = 600
    )
    Assert-RigClientMutatingAllowed -Action 'start' -As $As
    Add-LauncherType
    $targets = @($Entries)
    if ($targets.Count -eq 0) { Write-Host "[Start] No client instances selected."; return }

    # Pre-flight the whole set BEFORE launching anything, and refuse rather than skip.
    #
    # Both of these used to be a warning and a `continue`. A skipped start is the worst possible
    # outcome: -Start -All comes back looking successful, the instance that was skipped is still in
    # whatever world it was already in (or is not there at all), and every assertion afterwards runs
    # against a rig that is not the one the caller asked for. The server half throws on
    # an already-running server for exactly this reason; this half now agrees.
    foreach ($e in $targets) {
        $p = Get-InstancePaths -Name $e.instanceName -Entry $e
        if (-not (Test-Path $p.Exe)) {
            # The root is named along with WHERE IT CAME FROM, because the usual cause of this
            # message is that the tree is somewhere else entirely: an instance built under
            # -InstancesRoot used to be looked for under instances/ beside this script, and the
            # message read as "unprovisioned" when the tree was sitting on another volume.
            throw "[Start] Instance '$($e.instanceName)' is in the registry but has no tree at $($p.Exe). That location came from $($p.RootSource). Rebuild it there (testrig create -Target $($e.instanceName) -Force -As <id>), or name the root the tree actually has with -InstancesRoot <root>, which also records it for every later command."
        }
        $running = Get-RigPidFromFile $p.PidFile
        if (Test-RigClientProcessAlive $running) {
            throw "[Start] Instance '$($e.instanceName)' is already running (PID $running). Nothing was started. Stop it first (testrig stop -Target $($e.instanceName) -As <id>) or check: testrig status. A start that silently skipped would leave it in whatever world it is already in."
        }
        if ($null -ne $running) {
            # A pid file whose process is gone, or whose number now belongs to something that is not
            # the game. Test-RigClientProcessAlive checks the process image for exactly this case: refusing to
            # start over a recycled id would make a crashed instance unstartable until somebody
            # deleted the file by hand.
            Write-Host "[$($e.instanceName)] Stale game.pid ignored: PID $running is not a live game client. This start replaces it."
        }
    }

    if ($Desktop) {
        [ClientRigLauncher]::EnsureDesktop($Desktop)
        Write-Host "[Start] Desktop: WinSta0\$Desktop (created if absent, never switched to)"
    } else {
        Write-Warning "[Start] No -Desktop given. Instances will run on the developer's desktop and WILL take the foreground. Debugging only."
    }

    # Hosts first. Process order is not the real constraint (that is "the host is IN ITS WORLD
    # before a joiner connects", which only the /host + /connect sequence can enforce), but starting
    # them in this order costs nothing and puts the longest pole in the ground first.
    $ordered = @($targets | Where-Object { (Get-EntryValue $_ 'role' 'client') -eq 'host' }) +
               @($targets | Where-Object { (Get-EntryValue $_ 'role' 'client') -ne 'host' })

    foreach ($e in $ordered) {
        $p = Get-InstancePaths -Name $e.instanceName -Entry $e

        New-Item -ItemType Directory -Force -Path $p.Data, $p.UserData, $p.LogDir | Out-Null
        Write-AllManifests -Desktop $Desktop

        $stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'
        $unityLog = Join-Path $p.LogDir "unity-$stamp.log"

        # -logFile with a unique path is mandatory, and not for the reason it looks like. Two
        # instances without it both start fine; what happens is that the second starter wins
        # Player.log, the first instance's log is discarded with no error, and Player-prev.log is
        # zeroed by two rotations in one second, destroying the developer's previous log.
        #
        # -settings SavePath is deliberately absent. See the note in Invoke-SeedMods.
        #
        # -screen-* are kept even though the game overwrites them a moment later, so the window is
        # the right size before the plugin's patches run and there is no fullscreen flash.
        $argv = @(
            '-logFile', $unityLog,
            '-settingspath', $p.Settings,
            '-screen-width', $Width, '-screen-height', $Height, '-screen-fullscreen', '0'
        )

        $commandLine = ConvertTo-RigCommandLine -Arguments (@($p.Exe) + $argv)

        # The plugin finds its manifest through this variable first, and through the working
        # directory second. CreateProcessW with a null environment block inherits ours.
        $env:STATIONEERS_CLIENTRIG_MANIFEST = $p.Manifest

        $SW_SHOWNOACTIVATE = 4
        $desktopSpec = if ($Desktop) { "WinSta0\$Desktop" } else { '' }

        try {
            $procId = [ClientRigLauncher]::Start($p.Exe, $commandLine, $p.Data, [int16]$SW_SHOWNOACTIVATE, $desktopSpec)
        }
        finally {
            Remove-Item Env:\STATIONEERS_CLIENTRIG_MANIFEST -ErrorAction SilentlyContinue
        }

        Set-Content -Path $p.PidFile -Value $procId
        $roleTag  = [string](Get-EntryValue $e 'role' 'client')
        $gameTag  = [int](Get-EntryValue $e 'gamePort' 0)
        Write-Host "[$($e.instanceName)] PID $procId, role $roleTag, port $($e.port), gamePort $gameTag, log $unityLog"
    }

    Write-Host "[Start] Boot to the main menu takes roughly 100 seconds. Wait for it with:"
    Write-Host "[Start]   testrig wait -Target clients -Stage menu"

    # The one ordering rule that cannot be enforced from out here, stated where it is needed rather
    # than left to a document nobody opens: a joiner has nothing to reach until the host is hosting,
    # and /connect against a host that is still loading fails in a way that reads like a bad address.
    # Not named $hosts purely out of caution: $Host is an automatic variable and PowerShell variable
    # names are case-insensitive, so anything in that neighbourhood is worth keeping distinct.
    $hostTargets = @($ordered | Where-Object { (Get-EntryValue $_ 'role' 'client') -eq 'host' })
    if ($hostTargets.Count -gt 0) {
        $h  = $hostTargets[0]
        $hp = [int](Get-EntryValue $h 'gamePort' 0)
        Write-Host "[Start] This set contains a host. The host must be IN ITS WORLD before any joiner connects:"
        Write-Host "[Start]   testrig wait -Target $($h.instanceName) -Stage menu"
        Write-Host "[Start]   testrig call -Target $($h.instanceName) -As <id> -Path /host -Body '{`"world`":`"Lunar`"}'"
        Write-Host "[Start]   testrig wait -Target $($h.instanceName) -Stage inWorld -WaitSeconds 600"
        Write-Host "[Start]   then each joiner: -Path /connect -Body '{`"address`":`"127.0.0.1`",`"port`":$hp}'"
    }
}

# ---- who is what ----------------------------------------------------------
#
# Every host-aware decision downstream reads from here: the teardown order, the -Stop and -Remove
# refusals, -Status and -List. One definition of "is this instance hosting" rather than four.
#
# It is two passes on purpose. Pass 1 asks each live instance what it is. Pass 2 classifies, and
# classification needs the whole rig: an instance whose control plane does not answer is only
# safely a joiner while NOBODY in the rig is joined to anything, because the moment somebody is
# joined, the silent process is a candidate for the thing they joined to.

function Get-LiveRole {
    # menu | singlePlayer | joinedClient | listenHost | dedicated, or '' when it cannot be told.
    #
    # Prefers /status.role, which the plugin computes, so nothing out here re-derives it from raw
    # flags and walks into the IsClient trap: a listen host reports isServer TRUE and isClient
    # FALSE, exactly like a dedicated server. The derivation below is only for an instance running
    # a plugin build from before /status.role existed, and it reads networkRole rather than
    # isClient for the same reason.
    param($Status)
    if ($null -eq $Status) { return '' }
    $reported = [string]$Status.role
    if ($reported) { return $reported }

    $networkRole = [string]$Status.networkRole
    $phase       = [string]$Status.phase
    if ($networkRole -eq 'Server') {
        $batch = $false
        if ($null -ne $Status.batchMode) { $batch = [bool]$Status.batchMode }
        if ($batch) { return 'dedicated' }
        return 'listenHost'
    }
    if ($networkRole -eq 'Client') { return 'joinedClient' }
    if ($phase -eq 'inWorld') { return 'singlePlayer' }
    if ($phase -eq 'menu')    { return 'menu' }
    return ''
}

function Get-AttachedJoinerCount {
    # How many OTHER clients are in this instance's session, or $null when it cannot be told.
    # Only meaningful for something that owns a session; a joiner's own player count describes the
    # host's session, not one of its own.
    #
    # The host consumes a ClientId too and appears in its own roster, so a raw roster count reports
    # a lone host as having one client. Its own id is excluded.
    param($Status, [string] $LiveRole)
    if ($null -eq $Status) { return $null }
    if ($LiveRole -ne 'listenHost' -and $LiveRole -ne 'dedicated' -and $LiveRole -ne 'singlePlayer') { return $null }

    $own = ''
    if ($null -ne $Status.localClientId) { $own = [string]$Status.localClientId }

    if ($null -ne $Status.connectedClients) {
        $n = 0
        foreach ($c in @($Status.connectedClients)) {
            $cid = ''
            if ($null -ne $c.clientId) { $cid = [string]$c.clientId }
            if ($own -and $cid -eq $own) { continue }
            $n++
        }
        return $n
    }
    if ($null -ne $Status.playersInGame) {
        $n = [int]$Status.playersInGame - 1
        if ($n -lt 0) { $n = 0 }
        return $n
    }
    return $null
}

function Get-InstanceRuntime {
    # Pass 1: process liveness plus one /status, with no interpretation beyond the live role.
    param([Parameter(Mandatory)] $Entry)
    $paths  = Get-InstancePaths -Name $Entry.instanceName -Entry $Entry
    $procId = Get-RigPidFromFile $paths.PidFile
    $alive  = Test-RigClientProcessAlive $procId
    $status = $null
    $err    = ''
    if ($alive) {
        try   { $status = Invoke-Control -Port ([int]$Entry.port) -Path '/status' -TimeoutSec 5 }
        catch { $err = $_.Exception.Message }
    }

    $liveRole = Get-LiveRole $status
    $phase    = ''
    $hosting  = $null
    $hostPort = 0
    if ($null -ne $status) {
        $phase = [string]$status.phase
        if ($null -ne $status.hosting)  { $hosting  = [bool]$status.hosting }
        if ($null -ne $status.hostPort) { $hostPort = [int]$status.hostPort }
    }

    [pscustomobject]@{
        Name            = [string]$Entry.instanceName
        Entry           = $Entry
        Paths           = $paths
        ProcessId       = $procId
        Alive           = $alive
        Status          = $status
        Answered        = ($null -ne $status)
        Error           = $err
        ProvisionedRole = [string](Get-EntryValue $Entry 'role' '')
        GamePort        = [int](Get-EntryValue $Entry 'gamePort' 0)
        LiveRole        = $liveRole
        Phase           = $phase
        Hosting         = $hosting
        HostPort        = $hostPort
        JoinerCount     = (Get-AttachedJoinerCount -Status $status -LiveRole $liveRole)
        # Pass 2 fills these in.
        Role            = 'unknown'
        RoleSource      = ''
        OwnsWorld       = $false
        NeedsDisconnect = $false
    }
}

function Set-InstanceRoles {
    # Pass 2: stopped | joiner | standalone | host | possiblyHost, for every runtime at once.
    # Not Mandatory: PowerShell rejects an empty array for a mandatory parameter, and an empty set
    # is a legitimate input (-Status naming an instance that is not provisioned, for one).
    param($Runtimes = @())

    # Is anybody in the rig joined to anything right now? This is what makes the fallback for a
    # silent instance relaxed in the common case and paranoid in the dangerous one. On a cold boot
    # nobody is joined to anything, so an instance that has not opened its control plane yet cannot
    # be somebody's host, and -Stop -All keeps working with no ceremony. The moment ANY instance
    # reports joinedClient, a silent process is a candidate for the thing it joined, and gets
    # treated as possibly-host rather than assumed safe.
    $anyoneJoined = @($Runtimes | Where-Object { $_.LiveRole -eq 'joinedClient' }).Count -gt 0

    foreach ($rt in $Runtimes) {
        if (-not $rt.Alive) {
            $rt.Role       = 'stopped'
            $rt.RoleSource = 'process not running'
            continue
        }
        if ($rt.Answered) {
            $rt.RoleSource = "control plane (role=$(if ($rt.LiveRole) { $rt.LiveRole } else { 'unreported' }))"
            if ($rt.LiveRole -eq 'listenHost' -or $rt.LiveRole -eq 'dedicated') {
                $rt.Role      = 'host'
                $rt.OwnsWorld = ($rt.Phase -eq 'inWorld')
            }
            elseif ($rt.LiveRole -eq 'singlePlayer') {
                $rt.Role      = 'standalone'
                $rt.OwnsWorld = ($rt.Phase -eq 'inWorld')
            }
            elseif ($rt.LiveRole -eq 'joinedClient') {
                $rt.Role            = 'joiner'
                $rt.NeedsDisconnect = $true
            }
            elseif ($rt.LiveRole -eq 'menu') {
                $rt.Role = 'standalone'
            }
            elseif ($rt.Phase -eq 'inWorld') {
                # It answered, it is in a world, and it will not say whose. Nothing here can rule
                # out that the world is its own.
                $rt.Role      = 'possiblyHost'
                $rt.OwnsWorld = $true
            }
            else {
                # Answered, not in a world: booting or loading. There is no world to lose.
                $rt.Role = 'standalone'
            }
            continue
        }
        # No answer at all.
        if ($rt.ProvisionedRole -eq 'host') {
            $rt.Role       = 'possiblyHost'
            $rt.RoleSource = 'provisioned as a host; control plane silent'
        }
        elseif ($anyoneJoined) {
            $rt.Role       = 'possiblyHost'
            $rt.RoleSource = 'control plane silent while another instance is joined to something, so this one cannot be ruled out as its host'
        }
        else {
            $rt.Role = 'joiner'
            $rt.RoleSource = if ($rt.ProvisionedRole) { 'provisioned as a client; control plane silent' }
                             else { 'registry entry predates -Role; control plane silent' }
        }
    }
    return $Runtimes
}

function Get-HostTeardownRisk {
    # Reasons it is not safe to take this host down (or delete its world) right now. Returns a list
    # of strings, empty when there is nothing in the way.
    #
    # The joiners that are part of this teardown do not count: they are about to be disconnected
    # first, in order. What counts is anything attached that will still be there afterwards.
    param(
        [Parameter(Mandatory)] $HostRuntime,
        $InTeardown = @(),
        $Outside    = @()
    )
    $reasons = @()

    $clearedIds = @()
    foreach ($rt in $InTeardown) {
        if ($null -ne $rt.Entry.clientId) { $clearedIds += [string]$rt.Entry.clientId }
    }

    $roster = $null
    if ($HostRuntime.Status -and $null -ne $HostRuntime.Status.connectedClients) {
        $roster = @($HostRuntime.Status.connectedClients)
    }
    if ($null -ne $roster) {
        $own = ''
        if ($null -ne $HostRuntime.Status.localClientId) { $own = [string]$HostRuntime.Status.localClientId }
        foreach ($c in $roster) {
            $cid = ''; $uname = ''
            if ($null -ne $c.clientId) { $cid = [string]$c.clientId }
            if ($null -ne $c.username) { $uname = [string]$c.username }
            if ($own -and $cid -eq $own) { continue }
            if ($clearedIds -contains $cid) { continue }
            $who = if ($uname) { "$uname ($cid)" } else { $cid }
            $reasons += "client $who is connected to '$($HostRuntime.Name)' and is not part of this teardown"
        }
    }
    elseif ($null -ne $HostRuntime.JoinerCount -and $HostRuntime.JoinerCount -gt 0) {
        # No roster in this plugin build, so the count is all there is and it cannot be attributed.
        $inSet = @($InTeardown | Where-Object { $_.Role -eq 'joiner' -and $_.Alive }).Count
        if ($HostRuntime.JoinerCount -gt $inSet) {
            $reasons += "'$($HostRuntime.Name)' reports $($HostRuntime.JoinerCount) connected client(s) and only $inSet of them are in this teardown (this build's /status carries no roster, so they cannot be matched by id)"
        }
    }

    foreach ($o in $Outside) {
        if (-not $o.Alive) { continue }
        if ($o.LiveRole -eq 'joinedClient') {
            $reasons += "'$($o.Name)' is a joined client and is not part of this teardown"
        }
        elseif (-not $o.Answered) {
            $reasons += "'$($o.Name)' is running but its control plane does not answer, so it cannot be ruled out as a joiner"
        }
    }
    return $reasons
}

# ---- stopping -------------------------------------------------------------

function Get-ControlErrorDetail {
    # The useful part of a failed control-plane call. The plugin answers a refusal or a timeout with
    # 409 AND a diagnostic body ('error', or 'warning' plus the console lines it did and did not
    # see), which Invoke-RestMethod turns into a thrown exception whose default message is just the
    # status code. Reporting that alone would throw away the only explanation there is.
    param([Parameter(Mandatory)] $ErrorRecord)
    $raw = $null
    try { $raw = $ErrorRecord.ErrorDetails.Message } catch { }
    if ($raw) {
        try {
            $body = $raw | ConvertFrom-Json
            foreach ($f in @('error', 'warning', 'result', 'message')) {
                if ($null -ne $body.$f -and [string]$body.$f) { return [string]$body.$f }
            }
        }
        catch {
            return $raw
        }
    }
    return $ErrorRecord.Exception.Message
}

function Disconnect-Instance {
    # Leave the session cleanly and confirm it happened. A killed client leaves the host holding a
    # peer that never said goodbye, which is exactly the state the host is about to save.
    param([Parameter(Mandatory)] $Runtime, [int] $TimeoutSec = 30)
    $json = ([ordered]@{ wait = $true; timeoutMs = [int]($TimeoutSec * 1000) } | ConvertTo-Json -Compress)
    try {
        $r = Invoke-Control -Port ([int]$Runtime.Entry.port) -Path '/disconnect' -BodyJson $json -TimeoutSec ($TimeoutSec + 15)
    }
    catch {
        return [pscustomobject]@{ Ok = $false; Detail = (Get-ControlErrorDetail -ErrorRecord $_) }
    }
    $ok = ($r.ok -eq $true) -or ([string]$r.result -eq 'menu')
    return [pscustomobject]@{ Ok = $ok; Detail = "result=$([string]$r.result)" }
}

function Save-InstanceWorld {
    # Ask an instance to write its world, then wait for the plugin to say it saw the save land.
    #
    # Same contract as the server half's save, for the same reason: "the request was accepted"
    # and "the world is on disk" are different facts, and only the second one survives a teardown.
    # With no confirmation this WARNS and returns $false. It never reports a success it did not see;
    # a caller that must not proceed without a saved world checks the return value.
    param(
        [Parameter(Mandatory)] $Runtime,
        [string] $SaveName,
        [int] $WaitSec = 300
    )
    $req = [ordered]@{ wait = $true; timeoutMs = [int]($WaitSec * 1000) }
    if ($SaveName) { $req['name'] = $SaveName }
    $json  = $req | ConvertTo-Json -Compress
    $label = if ($SaveName) { "'$SaveName'" } else { 'the current world' }
    Write-Host "[$($Runtime.Name)] Saving $label (up to ${WaitSec}s for confirmation) ..."

    $r = $null
    try {
        $r = Invoke-Control -Port ([int]$Runtime.Entry.port) -Path '/save' -BodyJson $json -TimeoutSec ($WaitSec + 30)
    }
    catch {
        # A refusal and a timeout both come back as 409 with the plugin's own explanation in the
        # body, so that explanation is what gets reported rather than the status code.
        Write-Warning "[$($Runtime.Name)] Save NOT confirmed: $(Get-ControlErrorDetail -ErrorRecord $_)"
        Write-Warning "[$($Runtime.Name)] Treat this world as NOT saved. testrig logs -Target $($Runtime.Name), or GET /console/log?contains=Saved, shows what the game actually did."
        return $false
    }
    if ($null -ne $r.ok -and -not $r.ok) {
        Write-Warning "[$($Runtime.Name)] /save refused: $([string]$r.error). Treat this world as NOT saved."
        return $false
    }
    # 'confirmed' is how the plugin distinguishes "I saw it land" from "I asked". A build that does
    # not send the field is taken at its word on ok=true, and whatever it did send is printed, so
    # the difference stays visible instead of being assumed away.
    if ($null -ne $r.confirmed -and -not $r.confirmed) {
        Write-Warning "[$($Runtime.Name)] /save was accepted but not confirmed inside its own timeout. It may have completed silently or failed; check -Logs. Treat this world as NOT saved."
        return $false
    }

    $where = ''
    foreach ($f in @('path', 'savePath', 'resolvedPath', 'file')) {
        if ($null -ne $r.$f -and [string]$r.$f) { $where = " -> $([string]$r.$f)"; break }
    }
    $how = ''
    foreach ($f in @('confirmedBy', 'confirmation', 'how')) {
        if ($null -ne $r.$f -and [string]$r.$f) { $how = " ($([string]$r.$f))"; break }
    }
    # Size is worth printing: a confirmation plus a zero-byte file is the shape of a save that was
    # reported before the archive finished streaming.
    $size = ''
    if ($null -ne $r.sizeBytes -and [int64]$r.sizeBytes -gt 0) {
        $size = ', {0:N1} KB' -f ([int64]$r.sizeBytes / 1KB)
    }
    Write-Host "[$($Runtime.Name)] Save confirmed$how$where$size."
    return $true
}

function Stop-InstanceProcess {
    # Ask through the control plane, then kill after the grace period. Lifted out of Invoke-Stop
    # unchanged so the ordered teardown can call it after the role-specific steps.
    param([Parameter(Mandatory)] $Runtime, [int] $TimeoutSec = 30)
    $procId = Get-RigPidFromFile $Runtime.Paths.PidFile
    if (-not (Test-RigClientProcessAlive $procId)) {
        Remove-Item -Force -ErrorAction SilentlyContinue $Runtime.Paths.PidFile
        Write-Host "[$($Runtime.Name)] Not running."
        return
    }
    # A clean Application.Quit lets the game flush its own state instead of being killed mid-write.
    try {
        Invoke-Control -Port ([int]$Runtime.Entry.port) -Path '/quit' -BodyJson '{"hard":false}' -TimeoutSec 5 | Out-Null
        Write-Host "[$($Runtime.Name)] Quit requested."
    }
    catch {
        Write-Host "[$($Runtime.Name)] Control plane did not answer; going straight to a kill."
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline -and (Test-RigClientProcessAlive $procId)) { Start-Sleep -Milliseconds 500 }

    if (Test-RigClientProcessAlive $procId) {
        Write-Warning "[$($Runtime.Name)] Still alive after ${TimeoutSec}s; killing PID $procId."
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
    Remove-Item -Force -ErrorAction SilentlyContinue $Runtime.Paths.PidFile
    Write-Host "[$($Runtime.Name)] Stopped."
}

function Reset-StartLocalHostFlag {
    # Clears StartLocalHost in a stopped instance's own setting.xml.
    #
    # StartLocalHost is the setting that decides whether entering a world hosts it, and the game
    # persists its settings on a clean exit. data/<instance>/setting.xml is NOT reset by
    # -Provision -Force (that rebuilds the instance TREE; everything under data/ except userdata/mods
    # survives), so a value left behind by a hosting run outlives the rebuild that was supposed to
    # give a clean instance. The next run then comes up hosting when the test believes it is a
    # joiner, and nothing anywhere says so. Clearing it at teardown is the cheap end of the fix.
    #
    # Only ever called after the process is gone, because the game rewrites this file on exit.
    param([Parameter(Mandatory)] $Runtime)
    $file = $Runtime.Paths.Settings
    if (-not (Test-Path $file)) { return }
    try {
        $text = Get-Content -Raw -ErrorAction Stop $file
        if (-not $text) { return }
        $patched = [regex]::Replace($text, '(?i)(<StartLocalHost\s*>)\s*true\s*(</StartLocalHost\s*>)', '${1}false${2}')
        $patched = [regex]::Replace($patched, '(?i)(StartLocalHost\s*=\s*")true(")', '${1}false${2}')
        if ($patched -ne $text) {
            Set-Content -Path $file -Value $patched -Encoding utf8 -NoNewline
            Write-Host "[$($Runtime.Name)] Cleared StartLocalHost in $file (it survives -Provision -Force, and a stale one silently makes the next run a host)."
        }
    }
    catch {
        Write-Warning "[$($Runtime.Name)] Could not check StartLocalHost in $file ($($_.Exception.Message)). If this instance ever hosted, confirm the flag before reusing it as a joiner."
    }
}

function Invoke-RigClientStop {
    <#
        Host-aware ordered teardown of the selected instances.

        The lock-state gate and the -Release handling are NOT here. testrig.ps1 owns
        both, because they are rig-wide and because the ordering between them (ask
        for the lock state BEFORE releasing) has to hold across a stop that touches
        both halves. This half used to carry its own inline copy of the release
        predicate, untested, next to the tested Test-RigLockReleasableOnStop the
        server half called.

        Stopping is the single most destructive action here: a stop of every
        instance ends whatever was running, and a torn-down client cannot report
        afterwards that its run was interrupted, so the results of the interrupted
        test simply look wrong.
    #>
    param(
        [string] $As,
        $Entries = @(),
        [int] $TimeoutSeconds = 0,
        [int] $WaitSeconds = 0,
        [string] $SaveName,
        [switch] $Force
    )
    if (-not $TimeoutSeconds) { $TimeoutSeconds = Get-RigTimeoutDefaultSeconds }
    if (-not $WaitSeconds)    { $WaitSeconds    = Get-RigWaitDefaultSeconds }
    $targets = @($Entries)
    if ($targets.Count -eq 0) { return }
    $timeout = $TimeoutSeconds

    # Classify the WHOLE rig before touching any of it. Registry insertion order used to decide the
    # teardown, which normally meant the host went first and took the world down under every joiner
    # still in it. The refusals below are only worth having if they fire while the rig is intact,
    # so nothing is stopped until every one of them has passed.
    $registry    = Read-Registry
    $targetNames = @($targets | ForEach-Object { [string]$_.instanceName })
    $everything  = @($registry | ForEach-Object { Get-InstanceRuntime -Entry $_ })
    Set-InstanceRoles -Runtimes $everything | Out-Null
    $rts     = @($everything | Where-Object { $targetNames -contains $_.Name })
    $outside = @($everything | Where-Object { $targetNames -notcontains $_.Name })

    foreach ($rt in @($rts | Where-Object { $_.Role -eq 'host' })) {
        $reasons = Get-HostTeardownRisk -HostRuntime $rt -InTeardown $rts -Outside $outside
        if ($reasons.Count -eq 0) { continue }
        $text = "[Stop] '$($rt.Name)' is hosting and something that is not part of this teardown is still attached to it:`n    " + ($reasons -join "`n    ")
        if (-not $Force) {
            throw "$text`nNothing was stopped. Take the joiners down too (-Target clients, or name them), or pass -Force to end the world under them. -Force is the same-session override and never touches the rig lock; taking a lock off another session is -BreakLock."
        }
        Write-Warning "$text`n[Stop] -Force: ending it under them anyway."
    }

    foreach ($rt in @($rts | Where-Object { $_.Role -eq 'possiblyHost' })) {
        $text = "[Stop] '$($rt.Name)' is running but cannot be classified ($($rt.RoleSource)). It may be holding a world, and with no control plane it cannot be asked to save one, so killing it would take an unsaved world with it."
        if (-not $Force) {
            throw "$text`nNothing was stopped. Give it a moment and retry (a booting instance answers within roughly 100 s), or pass -Force to kill it and accept the loss."
        }
        Write-Warning "$text`n[Stop] -Force: killing it anyway."
    }

    # Joiners leave first, then anything holding a world of its own, then hosts, then the ones that
    # could not be classified. The host outlives every client that was in its world, which is the
    # whole point of ordering this at all.
    $classOrder = @('stopped', 'joiner', 'standalone', 'host', 'possiblyHost')
    $sequence = @()
    foreach ($cls in $classOrder) { $sequence += @($rts | Where-Object { $_.Role -eq $cls }) }
    $sequence += @($rts | Where-Object { $classOrder -notcontains $_.Role })

    if ($sequence.Count -gt 1) {
        Write-Host ("[Stop] Order: " + (($sequence | ForEach-Object { "$($_.Name) [$($_.Role)]" }) -join ' -> '))
    }

    foreach ($rt in $sequence) {
        if ($rt.Role -eq 'stopped') {
            Remove-Item -Force -ErrorAction SilentlyContinue $rt.Paths.PidFile
            Write-Host "[$($rt.Name)] Not running."
            Reset-StartLocalHostFlag -Runtime $rt
            continue
        }

        if ($rt.NeedsDisconnect) {
            $d = Disconnect-Instance -Runtime $rt -TimeoutSec $timeout
            if ($d.Ok) {
                Write-Host "[$($rt.Name)] Left its session ($($d.Detail))."
            }
            elseif ($Force) {
                Write-Warning "[$($rt.Name)] Would not leave its session ($($d.Detail)); -Force, continuing."
            }
            else {
                throw "[Stop] '$($rt.Name)' would not leave its session ($($d.Detail)). Stopping the sequence here: killing it instead would leave the host holding a peer that never said goodbye, and that is the state the host is about to save. Everything after it in the order is still up. Fix it, or pass -Force."
            }
        }

        if ($rt.OwnsWorld) {
            if (-not (Save-InstanceWorld -Runtime $rt -SaveName $SaveName -WaitSec $WaitSeconds)) {
                if (-not $Force) {
                    throw "[Stop] '$($rt.Name)' holds a world and its save was not confirmed. Stopping the sequence here rather than quitting on top of it. Retry, save it by hand (testrig save -Target $($rt.Name) -As <id>), or pass -Force to quit and accept the loss."
                }
                Write-Warning "[$($rt.Name)] Save not confirmed; -Force, quitting anyway. Treat that world as lost."
            }
        }

        Stop-InstanceProcess -Runtime $rt -TimeoutSec $timeout
        Reset-StartLocalHostFlag -Runtime $rt
    }

}

function Invoke-RigClientSave {
    <#
        Ask each selected instance to write its world, and wait for the plugin to
        confirm it landed.

        -SaveName is OPTIONAL here and required on the server half. That asymmetry
        is real rather than sloppy: a client instance knows the world's current name
        and can save under it, and a dedicated server's console cannot.

        The confirmation mechanism cannot merge either. This half asks the plugin,
        which reports 'confirmed', a resolved path and a size; the server half can
        only grep its log. What IS identical, and stays identical, is the contract:
        confirmed or warn, never both.
    #>
    param(
        [string] $As,
        $Entries = @(),
        [string] $SaveName,
        [int] $WaitSeconds = 0
    )
    Assert-RigClientMutatingAllowed -Action 'save' -As $As
    if (-not $WaitSeconds) { $WaitSeconds = Get-RigWaitDefaultSeconds }
    $targets = @($Entries)
    $failed  = 0
    foreach ($e in $targets) {
        $rt = Get-InstanceRuntime -Entry $e
        if (-not $rt.Alive) {
            Write-Warning "[$($rt.Name)] Not running; there is nothing to save."
            $failed++
            continue
        }
        if (-not $rt.Answered) {
            Write-Warning "[$($rt.Name)] Control plane did not answer ($($rt.Error)); the world could not be saved."
            $failed++
            continue
        }
        if ($rt.LiveRole -eq 'joinedClient') {
            Write-Warning "[$($rt.Name)] is a joined client: the world belongs to whoever hosts it, so saving from here does not persist it. Save on the host instead."
        }
        if (-not (Save-InstanceWorld -Runtime $rt -SaveName $SaveName -WaitSec $WaitSeconds)) { $failed++ }
    }
    if ($failed -gt 0) {
        Write-Warning "[Save] $failed of $($targets.Count) instance(s) did not confirm a save. Do not treat those worlds as persisted: testrig logs -Target <name>, and GET /console/log, show what each instance actually did."
    }
}

function Invoke-RigClientRemove {
    # Deletes the instance's own save root under data/<name>/userdata/ along with the
    # tree. That is tier 3 (agent-managed) by design, but it belongs to whoever holds
    # the lock, and for a host that save root IS the world every joiner was in.
    param(
        [string] $As,
        [Parameter(Mandatory)] [string] $Instance,
        [switch] $Force,
        [string] $Desktop = 'StationeersRig'
    )
    if ($Instance.Contains(',')) { throw "'remove' takes one instance at a time." }
    Assert-RigClientMutatingAllowed -Action 'remove' -As $As
    $p = Get-InstancePaths -Name $Instance
    if (Test-RigClientProcessAlive (Get-RigPidFromFile $p.PidFile)) {
        throw "Instance '$Instance' is running. Stop it first: testrig stop -Target $Instance -As <id>"
    }

    # Same refusal -Stop applies to a host, for the stronger reason: a stopped host can be started
    # again, a deleted world cannot. For a host, the save root this deletes IS the world every
    # other instance was playing in.
    $entry = Get-InstanceEntry -Name $Instance
    if ((Get-EntryValue $entry 'role' 'client') -eq 'host') {
        $reasons = @()
        foreach ($o in @(Read-Registry | Where-Object { $_.instanceName -ne $Instance } | ForEach-Object { Get-InstanceRuntime -Entry $_ })) {
            if (-not $o.Alive) { continue }
            if ($o.LiveRole -eq 'joinedClient') {
                $reasons += "'$($o.Name)' is a joined client"
            }
            elseif (-not $o.Answered) {
                $reasons += "'$($o.Name)' is running but its control plane does not answer, so it cannot be ruled out as a joiner"
            }
        }
        if ($reasons.Count -gt 0) {
            $text = "[Remove] '$Instance' is a host, and removing it deletes its world at $($p.UserData), while:`n    " + ($reasons -join "`n    ")
            if (-not $Force) {
                throw "$text`nNothing was deleted. Stop the other instances first, or pass -Force."
            }
            Write-Warning "$text`n[Remove] -Force: deleting it anyway."
        }
    }
    if (Test-Path $p.Tree) { Remove-Item $p.Tree -Recurse -Force }
    if (Test-Path $p.Data) { Remove-Item $p.Data -Recurse -Force }
    Write-Registry (Read-Registry | Where-Object { $_.instanceName -ne $Instance })
    Write-AllManifests -Desktop $Desktop
    Write-Host "[Remove] Instance '$Instance' deleted. The source install is untouched: only hard links and per-instance copies were removed."
}

# ---- the control plane, from outside --------------------------------------

function Invoke-Control {
    # One HTTP call to one instance's control plane. Returns the parsed response, or throws.
    param(
        [Parameter(Mandatory)] [int] $Port,
        [Parameter(Mandatory)] [string] $Path,
        [string] $BodyJson,
        [int] $TimeoutSec = 30
    )
    $uri = "http://127.0.0.1:$Port$Path"
    if ($BodyJson) {
        return Invoke-RestMethod -Uri $uri -Method Post -Body $BodyJson -ContentType 'application/json' -TimeoutSec $TimeoutSec
    }
    return Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec $TimeoutSec
}

function Get-RequestedTimeoutMs {
    # The timeoutMs the CALLER asked the endpoint for, from the request body or from the query
    # string (every body field can also be passed as a query parameter, and a Windows path has to
    # be, so both are read). 0 when the request names none.
    #
    # Read with a regex rather than ConvertFrom-Json on purpose: this runs on a hand-typed -Body, and
    # working out a timeout must never be the thing that throws on a body the plugin would have
    # accepted, or refused with an explanation worth reading.
    param([string] $Path, [string] $BodyJson)
    $best = 0
    foreach ($pair in @(
        @{ Text = $Path;     Pattern = '[?&]timeoutMs=(\d+)' }
        @{ Text = $BodyJson; Pattern = '"timeoutMs"\s*:\s*"?(\d+)' }
    )) {
        if (-not $pair.Text) { continue }
        $m = [regex]::Match($pair.Text, $pair.Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $m.Success) { continue }
        $parsed = 0
        if ([int64]::TryParse($m.Groups[1].Value, [ref]$parsed) -and $parsed -gt $best) { $best = $parsed }
    }
    return $best
}

function Get-ControlTimeoutSeconds {
    # How long ONE -Call or -Broadcast request gets before the HTTP client gives up.
    #
    # This used to be a constant (120 s for -Call, 60 s for -Broadcast) and the constant WON, so a
    # request that told the endpoint to take up to five minutes was cut off by the launcher at two.
    # Every long endpoint was therefore unusable through the launcher: /connect, /host and /save all
    # died client-side, and the plugin's own answer, which is the only thing that says why a join or
    # a host attempt failed, was thrown away with the connection.
    #
    # The rule now: the caller's own timeoutMs plus a margin, never below a floor. The margin is what
    # makes the difference between "the plugin gave up and told us why" and "we gave up first".
    param([string] $Path, [string] $BodyJson, [int] $Override = 0)
    if ($Override -gt 0) { return $Override }

    $bare  = ([string]$Path).Split('?')[0].TrimEnd('/')
    $floor = if ($script:CliControlLongPaths -contains $bare.ToLowerInvariant()) { $script:CliControlLongPathSeconds }
             else { $script:CliControlTimeoutFloorSeconds }

    $asked = Get-RequestedTimeoutMs -Path $Path -BodyJson $BodyJson
    if ($asked -gt 0) {
        $derived = [int][Math]::Min($script:CliControlTimeoutCeilingSeconds,
                                    [Math]::Ceiling($asked / 1000.0) + $script:CliControlTimeoutMarginSeconds)
        if ($derived -ge $script:CliControlTimeoutCeilingSeconds) {
            Write-Warning "[Call] The request asks for timeoutMs $asked; capping the launcher's HTTP timeout at ${ControlTimeoutCeilingSeconds}s. The instance may still be working when this returns."
        }
        if ($derived -gt $floor) { return $derived }
    }
    return $floor
}

function Test-InstanceStage {
    # Is this instance at or past the named readiness stage. The three stages are genuinely
    # different and conflating them is a real trap: loadedPluginCount alone is not "ready", because
    # the splash screen is still drawing and it suppresses the in-game ImGui windows.
    param(
        [Parameter(Mandatory)] [int] $Port,
        [Parameter(Mandatory)] [string] $Want
    )
    # The thresholds themselves live in Test-RigStageReached (lib/common.ps1), pure
    # and testable, so the magic plugin count is declared once instead of being
    # copied next to every reader of /status.
    try {
        if ($Want -eq 'ping') { Invoke-Control -Port $Port -Path '/ping' -TimeoutSec 3 | Out-Null; return $true }
        $s = Invoke-Control -Port $Port -Path '/status' -TimeoutSec 5
        return (Test-RigStageReached -Status $s -Stage $Want)
    }
    catch { return $false }
}

function Invoke-RigClientWait {
    # Read-only, so it is not gated. It does refresh a lock you already hold, because a barrier can
    # legitimately run longer than the TTL (600 s for inWorld against a 10 min default) and losing
    # the rig halfway through a wait would be absurd. Silent no-op when you hold nothing.
    param(
        [string] $As,
        $Entries = @(),
        [ValidateSet('ping', 'modsLoaded', 'menu', 'inWorld')] [string] $Stage = 'menu',
        [int] $WaitSeconds = 0
    )
    if (-not $WaitSeconds) { $WaitSeconds = Get-RigWaitDefaultSeconds }
    $targets = @($Entries)
    if ($targets.Count -eq 0) { Write-Host "[Wait] No client instances selected."; return }
    Update-RigLockIfMine -CallerId $As
    $timeout = $WaitSeconds
    $deadline = (Get-Date).AddSeconds($timeout)

    Write-Host "[Wait] Barrier: $($targets.Count) instance(s) must reach stage '$Stage' within ${timeout}s."
    $pending = @{}
    foreach ($e in $targets) { $pending[$e.instanceName] = [int]$e.port }

    while ($pending.Count -gt 0 -and (Get-Date) -lt $deadline) {
        foreach ($name in @($pending.Keys)) {
            if (Test-InstanceStage -Port $pending[$name] -Want $Stage) {
                Write-Host "[Wait] $name reached '$Stage'."
                $pending.Remove($name)
            }
        }
        if ($pending.Count -eq 0) { break }
        Start-Sleep -Seconds 2
        Update-RigLockIfMine -CallerId $As
    }

    if ($pending.Count -gt 0) {
        # The most common cause is worth naming: a transient Steam Workshop query failure parks
        # StationeersLaunchPad on its own error screen forever with the plugin count stuck at 2.
        foreach ($name in $pending.Keys) {
            $port = $pending[$name]
            $detail = try {
                $s = Invoke-Control -Port $port -Path '/status' -TimeoutSec 5
                "phase=$($s.phase) gameInitialized=$($s.gameInitialized) plugins=$($s.loadedPluginCount)"
            } catch { 'control plane did not answer' }
            Write-Warning "[$name] Did not reach '$Stage': $detail"
        }
        throw "[Wait] $($pending.Count) instance(s) did not reach '$Stage' within ${timeout}s. If plugins is stuck at 2 with gameInitialized false, StationeersLaunchPad hit a transient Steam Workshop failure and is parked on its error screen: stop the instance and start it again, which clears it."
    }
    Write-Host "[Wait] All instances reached '$Stage'."
}

function Invoke-RigClientCall {
    <#
        One HTTP request to each selected instance's control plane.

        This is the old -Call and -Broadcast in one function, because they were one
        operation over a different number of targets and the only real difference
        was that the fan-out had its own, shorter, hardcoded timeout. Naming one
        target prints the parsed answer; naming several prints one line and one
        compact object each, and throws if any of them failed, because a partial
        fan-out leaves the rig in mixed state.

        Gated because it drives LIVE clients: /quit ends one, and /savepath retargets
        where one writes its saves (it refuses the developer's real user-data folder
        only while the caller omits force=true).
    #>
    param(
        [string] $As,
        $Entries = @(),
        [Parameter(Mandatory)] [string] $Path,
        [string] $Body,
        [int] $CallTimeoutSeconds = 0
    )
    Assert-RigClientMutatingAllowed -Action 'call' -As $As
    $targets = @($Entries)
    if ($targets.Count -eq 0) { throw "'call' needs at least one instance. Name one with -Target <name>, or fan out with -Target clients." }
    # Derived from the request rather than pinned, so -Path /connect with a body
    # asking for timeoutMs 300000 actually gets its five minutes.
    $timeoutSec = Get-ControlTimeoutSeconds -Path $Path -BodyJson $Body -Override $CallTimeoutSeconds

    if ($targets.Count -eq 1) {
        $e = $targets[0]
        Write-Host "[Call] $($e.instanceName) $Path (up to ${timeoutSec}s)"
        $r = Invoke-Control -Port ([int]$e.port) -Path $Path -BodyJson $Body -TimeoutSec $timeoutSec
        $r | ConvertTo-Json -Depth 10
        return
    }

    Write-Host "[Call] $Path -> $($targets.Count) instance(s), up to ${timeoutSec}s each"
    $failed = 0
    foreach ($e in $targets) {
        try {
            $r = Invoke-Control -Port ([int]$e.port) -Path $Path -BodyJson $Body -TimeoutSec $timeoutSec
            $ok = if ($null -ne $r.ok) { $r.ok } else { $true }
            if (-not $ok) { $failed++ }
            Write-Host "[$($e.instanceName)] ok=$ok"
            $r | ConvertTo-Json -Depth 6 -Compress
        }
        catch {
            $failed++
            Write-Warning "[$($e.instanceName)] $($_.Exception.Message)"
        }
    }
    if ($failed -gt 0) {
        throw "[Call] $failed of $($targets.Count) instance(s) failed. A partial fan-out leaves the rig in mixed state; fix and re-run before drawing any conclusion from a test."
    }
}

function Test-PathFullyQualified {
    # Fully qualified means the path names its own root: 'C:\x', 'C:/x' or a UNC '\\server\share'.
    # It is NOT the same as rooted. [IO.Path]::IsPathRooted answers true for '\x.json' (rooted at the
    # CURRENT drive) and for 'C:x.json' (drive-relative, resolved against a per-drive working
    # directory), and both of those land wherever the shell happens to be pointing.
    param([Parameter(Mandatory)] [string] $Value)
    $m = [IO.Path].GetMethod('IsPathFullyQualified', [type[]]@([string]))
    if ($m) { return [bool]$m.Invoke($null, @($Value)) }
    # Windows PowerShell 5.1 runs on .NET Framework, which has no such API.
    return ($Value -match '^([A-Za-z]:[\\/]|[\\/][\\/])')
}

function Resolve-RigOutFile {
    # Where a -Snapshot -OutFile actually lands, with the rig folder as the floor for anything
    # relative.
    #
    # A relative -OutFile used to resolve against the shell's working directory, which for an agent
    # is the repository root; rooting it at the rig folder fixed the common case, because the rig
    # folder is gitignored deny-all and a stray snapshot there cannot be committed by accident. It
    # did NOT fix two paths that still walk out: '..\..\before.json' is relative, gets joined, and
    # then climbs straight back out of the rig on the way to GetFullPath; and 'C:before.json' is
    # "rooted" by IsPathRooted, so the old test let it through untouched and it resolved against
    # whatever the current directory on C: happens to be. Both are refused here rather than written.
    #
    # A FULLY QUALIFIED path is the caller saying exactly where they want it, so it is honoured, with
    # a warning when it leaves the rig folder: at that point keeping the file out of a commit is on
    # the person who typed the path.
    param([Parameter(Mandatory)] [string] $Value)
    $qualified = Test-PathFullyQualified $Value
    if (-not $qualified -and $Value.Contains(':')) {
        throw "[Snapshot] -OutFile '$Value' is drive-relative: Windows resolves it against a per-drive working directory, so nothing here can say where it would land. Pass a path relative to the rig folder ($script:CliRoot), or a full path including the leading backslash."
    }
    $target  = if ($qualified) { $Value } else { Join-Path $script:CliRoot $Value }
    $full    = [IO.Path]::GetFullPath($target)
    $rigBase = [IO.Path]::GetFullPath($script:CliRoot).TrimEnd('\', '/') + '\'
    $inside  = $full.StartsWith($rigBase, [StringComparison]::OrdinalIgnoreCase)

    if (-not $qualified -and -not $inside) {
        throw "[Snapshot] -OutFile '$Value' climbs out of the rig folder (it resolves to $full). A relative -OutFile is rooted at $script:CliRoot, which is gitignored deny-all, precisely so a stray snapshot cannot be committed by accident. Drop the '..' segments, or pass a full path if you really mean to write outside the rig."
    }
    if (-not $inside) {
        Write-Warning "[Snapshot] $full is outside the rig folder, so the deny-all gitignore does not cover it. Make sure it is not somewhere that gets committed."
    }
    return $full
}

function Invoke-RigClientSnapshot {
    param($Entries = @(), [string] $OutFile)
    $targets = @($Entries)
    $rows = @()
    foreach ($e in $targets) {
        $row = [ordered]@{ instanceName = $e.instanceName; port = [int]$e.port }
        try   { $row['status'] = Invoke-Control -Port ([int]$e.port) -Path '/status' -TimeoutSec 15 }
        catch { $row['error'] = $_.Exception.Message }
        $rows += [pscustomobject]$row
    }
    $json = ,@($rows) | ConvertTo-Json -Depth 12
    if ($OutFile) {
        $target = Resolve-RigOutFile -Value $OutFile
        $dir = Split-Path -Parent $target
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        Set-Content -Path $target -Value $json -Encoding utf8
        Write-Host "[Snapshot] $($rows.Count) instance(s) -> $target"
    } else {
        $json
    }
}

# ---- status and logs ------------------------------------------------------

function Write-RigClientStatus {
    # Per-instance detail only. The rig-wide lock block is printed once by
    # testrig.ps1, above both halves; printing it here as well is what made "the
    # first line of status" mean something different depending on which launcher an
    # agent happened to ask.
    param($Entries = @())
    $selected = @($Entries)
    if ($selected.Count -eq 0) {
        Write-Host "clients: none provisioned. Create one: testrig create -Target client1 -As <id>"
        return
    }
    Write-Host "clients ($($selected.Count)):"
    $rts = @($selected | ForEach-Object { Get-InstanceRuntime -Entry $_ })
    Set-InstanceRoles -Runtimes $rts | Out-Null

    foreach ($rt in $rts) {
        $e = $rt.Entry
        $line = if ($rt.Alive) { "running (PID $($rt.ProcessId))" } else { 'stopped' }
        Write-Host "$($rt.Name):"
        Write-Host "  process:    $line"
        Write-Host "  role:       $($rt.Role) [$($rt.RoleSource)]"
        Write-Host "  ports:      $($e.port) control plane (TCP), $($rt.GamePort) game (UDP)"
        Write-Host "  identity:   $($e.username) ($($e.clientId))"
        $treeState = if (Test-Path $rt.Paths.Tree) { '' } else { '  MISSING' }
        Write-Host "  tree:       $($rt.Paths.Tree)$treeState  [$($rt.Paths.RootSource)]"

        if ($rt.Alive -and $rt.Answered) {
            $s = $rt.Status
            Write-Host "  phase:      $($s.phase) (gameInitialized=$($s.gameInitialized), plugins=$($s.loadedPluginCount))"

            # -Status used to print no network information at all, which made "is this thing hosting,
            # and did the other instance actually arrive" unanswerable from the launcher.
            $liveRole    = if ($rt.LiveRole) { $rt.LiveRole } else { 'unreported' }
            $hostingText = if ($null -eq $rt.Hosting) { 'unreported' } else { [string]$rt.Hosting }
            $hostPortText = if ($rt.HostPort -gt 0) { [string]$rt.HostPort } else { '-' }
            $joinerText  = if ($null -eq $rt.JoinerCount) { 'n/a' } else { [string]$rt.JoinerCount }
            Write-Host "  network:    liveRole=$liveRole hosting=$hostingText hostPort=$hostPortText connectedClients=$joinerText"
            if ([string]$s.serverAddress) {
                Write-Host "  joined to:  $($s.serverAddress):$($s.serverPort) ($($s.networkRole)/$($s.networkState))"
            }
            if ($null -ne $s.connectedClients) {
                foreach ($c in @($s.connectedClients)) {
                    Write-Host "  client:     $($c.username) ($($c.clientId))"
                }
            }

            Write-Host "  foreground: $($s.foreground.verdict) (ownDesktop=$($s.foreground.ownDesktop))"
            Write-Host "  inputGate:  open=$($s.gameplayInputGateOpen)"
            if ($s.instance.peers.conflictDetected) {
                Write-Warning "  identity conflict: $($s.instance.peers.conflict)"
            }
        }
        elseif ($rt.Alive) {
            Write-Host "  control:    not answering yet ($($rt.Error))"
        }
    }
}

function Get-RigClientListRows {
    # Only instances whose process is alive are probed, so listing a cold rig makes no HTTP call at
    # all and still answers instantly. The live columns are '-' for those.
    param($Entries = @())
    $registry = @($Entries)
    if ($registry.Count -eq 0) { return @() }
    $rts = @($registry | Sort-Object index | ForEach-Object { Get-InstanceRuntime -Entry $_ })
    Set-InstanceRoles -Runtimes $rts | Out-Null

    $rows = @()
    foreach ($rt in $rts) {
        $live = '-'
        if ($rt.Alive) { $live = if ($rt.LiveRole) { $rt.LiveRole } else { 'no answer' } }
        $hosting = if ($null -eq $rt.Hosting)     { '-' } else { [string]$rt.Hosting }
        $clients = if ($null -eq $rt.JoinerCount) { '-' } else { [string]$rt.JoinerCount }
        $rows += [pscustomobject][ordered]@{
            instanceName = $rt.Name
            index        = [int]$rt.Entry.index
            role         = [string](Get-EntryValue $rt.Entry 'role' 'client')
            liveRole     = $live
            hosting      = $hosting
            clients      = $clients
            port         = [int]$rt.Entry.port
            gamePort     = $rt.GamePort
            clientId     = [string]$rt.Entry.clientId
            username     = [string]$rt.Entry.username
            provisionedUtc = [string]$rt.Entry.provisionedUtc
        }
    }
    return $rows
}

function Invoke-RigClientLogs {
    param(
        [Parameter(Mandatory)] [string] $Instance,
        [int] $Tail = 50,
        [string] $Grep
    )
    $p = Get-InstancePaths -Name $Instance
    $log = Join-Path $p.BepInEx 'LogOutput.log'
    Write-Host "== $Instance : $log"
    if (-not (Test-Path $log)) {
        Write-Host "No BepInEx log at $log."
        return
    }
    if ($Grep) { Get-Content $log | Select-String -Pattern $Grep }
    else       { Get-Content -Tail $Tail $log }
}


# ---- update-game / update-mods / deploy -----------------------------------
#
# The three concepts the two halves used to spell differently. On this half they
# were all one flag, -Provision -Force, which is how "refresh the game binaries"
# and "refresh the mod set" became indistinguishable from each other AND from the
# server half's -Bootstrap and -SyncMods. Three verbs now, each meaning the same
# thing on both halves even though the mechanism differs.

function Invoke-RigClientUpdateGame {
    <#
        Re-link every selected instance from the developer's install.

        The mechanism is genuinely different from the server half's (a hard-link
        rebuild here, a SteamCMD download there) and so is the source (the
        developer's already-updated client install, versus Steam app 600760). What is
        the same is the intent, so testrig.ps1 fans one verb out over both.

        A rebuild replaces the TREE and keeps data/<instance>/, so saves, logs and the
        game-written setting.xml survive. Role, ports and identity are kept because
        they come out of the registry entry.
    #>
    param(
        [string] $As,
        $Entries = @(),
        [string] $Desktop = 'StationeersRig'
    )
    $targets = @($Entries)
    if ($targets.Count -eq 0) {
        Write-Host "[UpdateGame] No client instances are provisioned; nothing to re-link."
        return
    }
    $source = Get-RigStationeersPath
    Write-Host "[UpdateGame] Re-linking $($targets.Count) instance(s) from $source (game $(Get-RigInstallVersion -InstallDir $source))."
    # Pre-flight the whole set before rebuilding any of it, for the same reason
    # starting does: a half-updated rig is worse than one that refused.
    foreach ($e in $targets) {
        $name = [string]$e.instanceName
        $p    = Get-InstancePaths -Name $name -Entry $e
        if (Test-RigClientProcessAlive (Get-RigPidFromFile $p.PidFile)) {
            throw "[UpdateGame] Instance '$name' is running. Stop it first: testrig stop -Target $name -As <id>"
        }
    }
    foreach ($e in $targets) {
        $name = [string]$e.instanceName
        Write-Host "[UpdateGame] --- $name"
        Invoke-RigClientCreate -As $As -Instance $name -Force -Desktop $Desktop | Out-Null
    }
    Write-Host "[UpdateGame] $($targets.Count) instance(s) re-linked."
}

function Invoke-RigClientUpdateMods {
    <#
        Re-seed each selected instance's mod set from the developer's mod folder.

        Same concept as the server half's update-mods, different destination: each
        instance gets its own copy under data/<instance>/userdata/mods/ with its own
        modconfig.xml, because StationeersLaunchPad prunes Local entries whose folder
        is not under the active save path.

        THIS WIPES userdata/mods/, so anything a deploy put there goes with it. The
        removal is named rather than left to be discovered.
    #>
    param(
        [string] $As,
        $Entries = @()
    )
    Assert-RigClientMutatingAllowed -Action 'update-mods' -As $As
    $targets = @($Entries)
    if ($targets.Count -eq 0) {
        Write-Host "[UpdateMods] No client instances are provisioned; nothing to seed."
        return
    }
    $repoMods = @(Get-RigDeployableMods)
    foreach ($e in $targets) {
        $name = [string]$e.instanceName
        $p    = Get-InstancePaths -Name $name -Entry $e
        if (Test-RigClientProcessAlive (Get-RigPidFromFile $p.PidFile)) {
            throw "[UpdateMods] Instance '$name' is running and holds its mod files open. Stop it first: testrig stop -Target $name -As <id>"
        }
        $before = @(Get-ChildItem -LiteralPath (Join-Path $p.UserData 'mods') -Directory -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
        Write-Host "[UpdateMods] --- $name"
        Invoke-SeedMods -Paths $p
        $lost = @($before | Where-Object { $_ -match '^Local_(.+)$' -and $repoMods -contains $Matches[1] })
        if ($lost.Count -gt 0) {
            Write-Warning "[UpdateMods] [$name] the re-seed removed $($lost.Count) folder(s) this repository had deployed: $($lost -join ', '). Re-deploy them: testrig deploy <Mod> -Target $name -As <id>"
        }
    }
    Write-Host "[UpdateMods] $($targets.Count) instance(s) re-seeded."
}

function Invoke-RigClientDeploy {
    <#
        Put one of THIS repository's built mods into each selected instance.

        This did not exist. There was no path at all from Mods/<X>/<X>/bin/<C>/<X>.dll
        into an instance: a provision seeded from the developer's OWN mod folder, so a
        driven test measured whatever build happened to be sitting there. A live run
        did exactly that with a weeks-old copy, and the only reason it was caught is
        that the file sizes happened to differ visibly. The playtest harness grew
        Assert-BinaryUnderTest because of it, and that check's remedy text was a
        manual instruction ("copy the build in after provisioning and before start")
        because no command did the copy.

        DESTINATION, and why it differs from the server half's. An instance has the
        same two load paths and the same duplicate-load fatal: BepInEx/plugins/ is
        loaded by the Chainloader, userdata/mods/Local_<X>/ by StationeersLaunchPad,
        and a DLL in both makes Awake fire twice and every Harmony patch register
        twice. ClientDriver takes the plugins path (it has to load before
        StationeersLaunchPad runs), so a repository mod takes the StationeersLaunchPad
        path with an About/ mirror and a Local entry in the instance's own
        modconfig.xml. Any stale copy under BepInEx/plugins/<X>/ is removed, so a tree
        deployed the other way by hand self-heals.
    #>
    param(
        [string] $As,
        $Entries = @(),
        [string[]] $Mods = @(),
        [string] $Configuration = 'Release'
    )
    Assert-RigClientMutatingAllowed -Action 'deploy' -As $As
    $targets = @($Entries)
    if ($targets.Count -eq 0) {
        Write-Host "[Deploy] No client instances selected."
        return [pscustomobject]@{ Deployed = 0; Skipped = 0 }
    }
    $names = @($Mods)
    if ($names.Count -eq 0) { $names = @(Get-RigDeployableMods) }
    if ($names.Count -eq 0) { throw "No mods to deploy: Mods/ has no mod folders other than Template." }

    $deployed = 0
    $skipped  = 0
    foreach ($e in $targets) {
        $name = [string]$e.instanceName
        $p    = Get-InstancePaths -Name $name -Entry $e
        if (Test-RigClientProcessAlive (Get-RigPidFromFile $p.PidFile)) {
            throw "[Deploy] Instance '$name' is running and holds its loaded plugin DLLs open; a deploy would fail or leave a half-written file. Stop it first: testrig stop -Target $name -As <id>"
        }
        foreach ($modName in $names) {
            $build = Get-RigModBuild -Mod $modName -Configuration $Configuration
            if (-not $build) {
                Write-Warning "[$name] '$modName' not found under Mods/, Plans/ or either half's dev-plugins/. Skipping."
                $skipped++
                continue
            }
            if (-not (Test-Path -LiteralPath $build.Dll)) {
                Write-Warning "[$name] the $Configuration build of '$modName' is not at $($build.Dll). Skipping. Build it first."
                $skipped++
                continue
            }
            $localModDir = Join-Path $p.UserData "mods\Local_$modName"
            New-Item -ItemType Directory -Force -Path $localModDir | Out-Null
            if (Test-Path -LiteralPath $build.About) {
                $aboutDst = Join-Path $localModDir 'About'
                if (Test-Path $aboutDst) { Remove-Item -Recurse -Force $aboutDst }
                Copy-Item -Recurse -Path $build.About -Destination $localModDir
            }
            else {
                Write-Warning "[$name] '$modName' has no About/ folder at $($build.About); StationeersLaunchPad may not load it without About.xml."
            }
            Copy-Item -Path $build.Dll -Destination (Join-Path $localModDir "$modName.dll") -Force

            $stale = Join-Path $p.BepInEx "plugins\$modName\$modName.dll"
            if (Test-Path -LiteralPath $stale) {
                Remove-Item -Force $stale
                Write-Host "[$name] removed a stale duplicate at BepInEx/plugins/$modName/$modName.dll (two loaders double every Harmony patch)."
            }

            $added = Add-RigModConfigLocalEntry -Path (Join-Path $p.UserData 'modconfig.xml') -LocalModDir $localModDir
            if ($added) { Write-Host "[$name] added a modconfig.xml Local entry -> $localModDir" }
            Write-Host "[$name] $modName -> $localModDir (StationeersLaunchPad load path)"
            $deployed++
        }
    }
    Write-Host "[Deploy] clients: $deployed deployed, $skipped skipped."
    return [pscustomobject]@{ Deployed = $deployed; Skipped = $skipped }
}

# ---- version and staleness reporting --------------------------------------

function Get-RigClientVersionReport {
    # What game version each instance was built from, against what the developer's
    # install carries now. The provision stamp is the record, and until the version
    # reader was fixed that stamp recorded the Unity engine version, so this
    # comparison was not merely absent: it could not have worked.
    param($Entries = @())
    $source = 'unknown'
    try { $source = Get-RigInstallVersion -InstallDir (Get-RigStationeersPath) } catch { $source = 'unknown' }
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($e in @($Entries)) {
        $name  = [string]$e.instanceName
        $p     = Get-InstancePaths -Name $name -Entry $e
        $stamp = Join-Path $p.Data 'provision.stamp'
        $ver   = 'unknown'
        if (Test-Path -LiteralPath $stamp) {
            try { $ver = [string]((Get-Content -Raw -LiteralPath $stamp | ConvertFrom-Json).sourceVersion) } catch { $ver = 'unknown' }
        }
        if (-not $ver) { $ver = 'unknown' }
        $rows.Add([pscustomobject]@{
            Half    = 'client'
            Name    = $name
            Present = (Test-Path $p.Exe)
            Version = $ver
            Source  = $source
            Stale   = ($ver -ne 'unknown' -and $source -ne 'unknown' -and $ver -ne $source)
            Remedy  = "testrig update-game -Target $name -As <id>"
        })
    }
    return $rows.ToArray()
}

function Get-RigClientModStaleness {
    # Seeded mods older than the developer's source tree, and deployed repository
    # mods older than their build. Reported, never fixed here: the remedy is an
    # update or a deploy, and deleting a payload to signal staleness would break a
    # rig rather than describe it.
    param($Entries = @())
    $srcMods = Join-Path (Get-RigUserDataPath) 'mods'
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($e in @($Entries)) {
        $name = [string]$e.instanceName
        $p    = Get-InstancePaths -Name $name -Entry $e
        foreach ($d in @(Get-ChildItem -LiteralPath (Join-Path $p.UserData 'mods') -Directory -ErrorAction SilentlyContinue)) {
            $bare  = $d.Name -replace '^(Workshop|Local)_', ''
            $build = Get-RigModBuild -Mod $bare -Configuration 'Release'
            if ($build -and (Test-Path -LiteralPath $build.Dll)) {
                $srcTime = (Get-Item -LiteralPath $build.Dll).LastWriteTimeUtc
                $dstTime = Get-RigNewestBuildTime -Path $d.FullName
                if ($dstTime -and $srcTime -gt $dstTime) {
                    $rows.Add([pscustomobject]@{
                        Half = 'client'; Instance = $name; Kind = 'deployed mod'; Name = $d.Name
                        Deployed = $dstTime; Source = $srcTime
                        Remedy = "testrig deploy $bare -Target $name -As <id>"
                    })
                }
                continue
            }
            $src = Join-Path $srcMods $bare
            if (-not (Test-Path -LiteralPath $src)) { continue }
            $srcTime = Get-RigNewestBuildTime -Path $src
            $dstTime = Get-RigNewestBuildTime -Path $d.FullName
            if ($srcTime -and $dstTime -and $srcTime -gt $dstTime) {
                $rows.Add([pscustomobject]@{
                    Half = 'client'; Instance = $name; Kind = 'seeded mod'; Name = $d.Name
                    Deployed = $dstTime; Source = $srcTime
                    Remedy = "testrig update-mods -Target $name -As <id>"
                })
            }
        }
    }
    return $rows.ToArray()
}

function Stop-RigClientInstancesByPid {
    # The lock's reclaim path, deliberately NOT the ordered teardown.
    #
    # That ordering (joiners disconnect, the world holder saves, the host quits last)
    # exists to end a test cleanly and preserve its world. Here the session that owned
    # these instances has been silent for at least the idle ceiling, there is no test
    # left to preserve, and a hung client's control plane is exactly the thing likely
    # not to answer. So this stops them by verified pid and moves on.
    $live = @(Get-RigClientInstanceStates)
    if ($live.Count -eq 0) { return 0 }
    Write-Warning "[Lock] Reclaimed the rig from a session that left $($live.Count) instance(s) running: $(($live | ForEach-Object { $_.Name }) -join ', '). Stopping them, because the restore cannot clear files a running game holds open."
    foreach ($i in $live) {
        try {
            Stop-Process -Id $i.ProcessId -Force -ErrorAction Stop
            Write-Host "[Lock]   stopped $($i.Name) (pid $($i.ProcessId))."
        }
        catch { Write-Warning "[Lock]   could not stop $($i.Name) (pid $($i.ProcessId)): $($_.Exception.Message)" }
    }
    Start-Sleep -Milliseconds 500
    return $live.Count
}
