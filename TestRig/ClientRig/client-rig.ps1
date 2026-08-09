<#
.SYNOPSIS
    Provisions, launches and fans out across N isolated Stationeers game-client instances.

.DESCRIPTION
    The launcher half of the client rig. The other half is the ClientDriver plugin, which is the
    control plane inside each instance.

    The boundary between the two is process creation. This script owns everything outside a game
    process, and everything that must keep working when a process is dead or wedged: provisioning
    an instance tree, creating the isolated Win32 desktop, starting and stopping, PID files, and
    fanning one command out across the rig. The plugin owns everything inside a process, which is
    everything that needs the Unity main thread or the game's own types. There is no third category.

    An instance is a hard-linked copy of the developer's real install on the same NTFS volume, so it
    costs a few megabytes instead of seven gigabytes. Nothing the game or a mod writes to is ever a
    hard link, because a hard link shares the file data and a write would reach back into the
    developer's install.

    Every instance runs on a separate Win32 desktop that is created but never switched to. That is
    what stops the game taking the developer's foreground: the no-activate show flag alone loses
    (measured 40 focus steals out of 40 samples), a separate desktop wins (0 out of 55).

    The source install is treated as strictly read-only.

    Every mutating action needs the RIG SESSION LOCK. One lock at TestRig/session.lock covers this
    half and the dedicated server together, because the two share the developer's one game install
    and per-Windows-user Unity state that nothing separates (PlayerCookie-v2.xml, the PlayerPrefs
    registry key). Acquire once with -Lock, then pass -As <id> on every mutating command, on either
    launcher. Rules: TestRig/session.lock.template.

    Operating manual: README.md next to this script.
    Durable internals:  RESEARCH.md next to this script.
    Rig conventions: TestRig/CLAUDE.md.
    Repository conventions: CLAUDE.md (root).
    Developer environment: DEV.md.

.PARAMETER Provision
    Build or rebuild an instance: hard-link the game tree, point it at its own save root, seed its
    mod set, write its manifest and a provision stamp.

    A rebuild (-Force) replaces the instance TREE. It does NOT reset data/<instance>/: the save root,
    the logs, the PID file and the game-written setting.xml all survive, and only userdata/mods is
    rewritten. That is deliberate (a staged save must not evaporate on a plugin rebuild) but it does
    mean a rebuild is not a clean slate. -Stop clears StartLocalHost out of setting.xml for the one
    case where a stale value would silently change what the next run is.

.PARAMETER Instance
    Instance name, or a comma-separated list where an action accepts several.

.PARAMETER Port
    Control-plane TCP port for the instance. Defaults to 27700 plus the instance's index.

.PARAMETER Role
    What the instance is for: 'client' (joins a session someone else hosts, the default) or 'host'
    (a listen host that runs the world in its own process and that other instances join). It is
    advisory for the plugin and load-bearing here: a host is saved before it is stopped, is stopped
    after every joiner, and refuses to be stopped or removed while a joiner is attached. On a
    rebuild (-Provision -Force) the existing role is kept unless -Role is given.

.PARAMETER GamePort
    UDP port a listen host binds RakNet to, and the port a joiner reaches it on. Defaults to 27800
    plus the instance's index, so it never collides with the control plane at 27700 plus index.
    Refused when it collides with another instance, with this repository's dedicated server
    (28015/28016), or with the Stationeers client's own defaults (27015/27016). That is not
    fussiness: two RakNet bindings coexist happily on one port and route by destination address, so
    a joiner connects to something and the test is confidently wrong.

.PARAMETER ClientId
    Decimal ulong this instance presents as its player identity. Must be non-zero and unique across
    the rig. Defaults to 900000000000 plus the instance's index.

.PARAMETER Username
    Player name this instance presents. Defaults to the instance name.

.PARAMETER Width
    Window width in pixels. Default 800.

.PARAMETER Height
    Window height in pixels. Default 600.

.PARAMETER ForceGameplayInput
    Provision the instance with the cursor gate held open, so synthetic input reaches the game's
    per-frame consumers. Correct only for an instance nobody is sitting at, which is every instance
    this script creates, so it defaults on.

.PARAMETER SeedMods
    Copy the developer's local mod folders into the instance and repoint its modconfig.xml at the
    copy. On by default; the instance loads no local mods without it.

.PARAMETER Force
    Rebuild an instance that already exists, or override a refusal that is safe to override inside
    your own session. It is routine and it NEVER touches the rig lock: taking a lock off another
    session is -BreakLock, which is human-gated. Both launchers agree on both meanings.

    On -Stop and -Remove it overrides the host-safety refusals: tearing down or deleting a host
    while a joiner is attached, tearing down a live instance whose control plane cannot be reached
    and which therefore cannot be ruled out as a host, and continuing past a save that was not
    confirmed. Each of those loses a world if the guess was wrong, so -Force there means "I accept
    that", not "try harder".

.PARAMETER All
    Apply the action to every provisioned instance.

.PARAMETER Start
    Launch the instance on the rig's isolated desktop. Hosts are launched before joiners, and
    starting an instance that is already running is an error rather than a skip: a silent no-op
    leaves a host in whatever world it was already in and every later assertion runs against the
    wrong state.

.PARAMETER Stop
    Terminate the instance and clean up its PID file. Host-aware: joiners are disconnected first
    and confirmed, then any instance holding a world saves and is confirmed, then the host quits and
    is killed if it outlasts -TimeoutSeconds. A failure at any step stops the sequence rather than
    tearing the rest of the rig down on top of it.

.PARAMETER Save
    Ask an instance to write its world to disk through POST /save, then wait for the plugin's
    confirmation. Same contract as dedicated-server.ps1 -Save: on timeout it WARNS rather than
    claiming success, because "the request was accepted" and "the world is on disk" are different
    facts and only the second one is worth anything after a teardown.

.PARAMETER Name
    With -Save: the save name to write. Omit to let the instance save the world under its current
    name.

.PARAMETER Status
    Report each instance: provisioned, running, control-plane answering, phase, identity, role,
    hosting state, game port and connected clients.

.PARAMETER List
    List provisioned instances with their registry entry, plus live role, hosting state and
    connected-client count for the ones that are running.

.PARAMETER Remove
    Delete an instance tree and its save root. Refuses while the instance is running, and refuses to
    delete a host's world while another instance is joined to it (-Force overrides).

.PARAMETER Desktop
    Name of the Win32 desktop to run instances on. Pass an empty string to run on the developer's
    desktop, which reintroduces the focus theft and is for debugging only.

.PARAMETER Wait
    Block until every selected instance reaches a readiness stage. The barrier across the rig.

.PARAMETER Stage
    Readiness stage for -Wait: ping, modsLoaded, menu, or inWorld.

.PARAMETER Broadcast
    Send one HTTP request to every selected instance and report each answer. The fan-out.

.PARAMETER Call
    Send one HTTP request to one instance.

.PARAMETER Path
    Control-plane path for -Broadcast or -Call, for example /config/set.

.PARAMETER Body
    JSON request body for -Broadcast or -Call. Omit for a GET.

.PARAMETER Snapshot
    Fetch /status from every selected instance in one go.

.PARAMETER OutFile
    Write the -Snapshot result to this path instead of the console. A relative path is rooted at the
    rig folder, which is gitignored deny-all, not at the shell's working directory, which for an
    agent is the repository root where nothing would catch a stray snapshot. A relative path that
    climbs back out of the rig folder with '..' is REFUSED rather than rooted, since it would land
    somewhere no rule catches. An absolute path is the caller's explicit choice and is honoured,
    with a warning when it falls outside the rig folder.

.PARAMETER TimeoutSeconds
    Process-teardown grace for -Stop: how long a client gets to quit cleanly before it is killed.
    Default 30, the same meaning and the same default as on dedicated-server.ps1. It used to double
    as the -Wait barrier timeout, which made one flag name mean two different things across the two
    launchers; the barrier is -WaitSeconds now.

.PARAMETER WaitSeconds
    How long a blocking wait waits, which is the same meaning it has on dedicated-server.ps1. Three
    actions take it: -Wait (the readiness barrier, default 300), -Save (how long to wait for the
    save confirmation, default 300), and -Lock (how long to queue for a rig held by another session,
    default 0, meaning do not queue at all).

.PARAMETER CallTimeoutSeconds
    How long ONE -Call or -Broadcast request may take before the HTTP client gives up. This is a
    third, separate flag on purpose: -TimeoutSeconds is process-teardown grace and -WaitSeconds is
    how long a blocking wait waits, and overloading either one with a transport timeout is how a
    flag name comes to mean two things.

    Default 0, meaning "work it out from the request", which is what a caller wants nearly always:
    the endpoint's own timeoutMs (from the body or the query string) plus a margin, floored at 120 s
    and at 300 s for the endpoints that block for minutes (/host, /connect, /save, /load, /newworld,
    /waitfor). Pass a number only to override that. The old fixed 120 s meant a body asking for
    timeoutMs 300000 was cut off by the launcher at 120 s, so every long endpoint was unusable
    through -Call and the plugin's own answer (its refusal, or its confirmation) was never seen.

.PARAMETER Lock
    Acquire the RIG session lock for this whole test session. Requires -Purpose. Prints a short owner
    id to reuse via -As. The lock covers TestRig/DedicatedServer/ too, so the id works on both
    launchers. Pass -WaitSeconds N to queue for up to N seconds when another session holds it; the
    default of 0 keeps the immediate refusal. A queue promises no ordering fairness. Rules:
    TestRig/session.lock.template.

.PARAMETER RefreshLock
    Bump the lock timer while actively driving a test. Requires -As.

.PARAMETER Unlock
    Release the rig session lock. Requires -As, or human-authorized -BreakLock.

.PARAMETER Purpose
    With -Lock: short human-readable reason, e.g. "Two-client paint check for SprayPaintPlus". Shown
    to the user when another session is blocked.

.PARAMETER As
    The owner id printed by -Lock. Pass it on every mutating command.

.PARAMETER BreakLock
    Break a LIVE lock held by another session (with -Lock / -Unlock / -Stop). Agents may use this
    ONLY when the user explicitly authorizes it. Deliberately not spelled -Force.

.PARAMETER TtlMinutes
    With -Lock / -RefreshLock: inactivity window before the lock timer lapses. Default 10. A running
    client instance keeps the lock live regardless of the timer, so stop the rig before releasing.

.PARAMETER KeepState
    With -Lock: do NOT reset the rig's between-session state on acquisition. Taking a NEW lock
    normally clears what the last session left behind (per-instance settings, worlds, logs, BepInEx
    config, InspectorPlus request and snapshot files, stale pid files) so a test cannot fail on an
    unrelated test's leftovers. Pass this only when something was staged on purpose. It is loud: the
    launcher prints exactly what it skipped. Note the limit either way, the reset is BETWEEN
    sessions, and a session spans many start/stop cycles, so two unrelated tests under one lock get
    no reset between them. Release and re-take the lock when the subject changes.

.PARAMETER Release
    With -Stop: also release the rig session lock after stopping.

.PARAMETER Logs
    Tail the instance's BepInEx log.

.PARAMETER Tail
    Lines for -Logs. Default 50.

.PARAMETER Grep
    Regex filter for -Logs, applied to the whole file.

.PARAMETER InstancesRoot
    Where the hard-linked instance trees live. MUST be on the same NTFS volume as the game install,
    because hard links cannot cross volumes. For a NEW instance the order is -InstancesRoot, then the
    STATIONEERS_CLIENTRIG_ROOT environment variable, then instances/ beside this script. Set the
    environment variable in DEV.md when the repository and the game install are on different drives,
    which is the common case.

    An instance that already exists uses the root RECORDED IN ITS REGISTRY ENTRY at provision time,
    so the flag does not have to be re-passed on every later command. -InstancesRoot still overrides
    that when it is typed, which is how a tree gets moved. Before the root was recorded, -Provision
    honoured the flag and every later action silently fell back to instances/ beside this script:
    -Start reported a provisioned instance as having no tree, and the state reset could not find the
    instance's BepInEx config and skipped re-copying it (half of what the reset is for) while
    reporting only that there was no tree.

    An entry written before the field existed still works: it falls back to today's order and says
    so once, naming -Provision -Force as the fix.

.EXAMPLE
    A listen host plus one joiner, in the only order that works. The constraint runs the other way
    from teardown: the HOST must be in its world before any joiner connects, because /connect has
    nothing to reach until the host is hosting.

    client-rig.ps1 -Lock -Purpose "Two-client paint check for SprayPaintPlus"
    client-rig.ps1 -Provision -As <id> -Instance host1   -Role host
    client-rig.ps1 -Provision -As <id> -Instance client1 -Role client

    # 1. the host first, all the way into its world
    client-rig.ps1 -Start -As <id> -Instance host1
    client-rig.ps1 -Wait  -Instance host1 -Stage menu
    client-rig.ps1 -Call  -As <id> -Instance host1 -Path /host -Body '{"world":"Lunar"}'
    client-rig.ps1 -Wait  -Instance host1 -Stage inWorld -WaitSeconds 600

    # 2. only now the joiner, at the host's game port (-Status prints it)
    client-rig.ps1 -Start -As <id> -Instance client1
    client-rig.ps1 -Wait  -Instance client1 -Stage menu
    client-rig.ps1 -Call  -As <id> -Instance client1 -Path /connect -Body '{"address":"127.0.0.1","port":27801}'
    client-rig.ps1 -Wait  -Instance client1 -Stage inWorld -WaitSeconds 600
    client-rig.ps1 -Status -As <id> -All          # host1 should report 1 connected client

    # 3. teardown, which -Stop already orders for you
    client-rig.ps1 -Stop -As <id> -All -Release
#>
[CmdletBinding()]
param(
    [switch] $Provision,
    [string] $Instance,
    [int]    $Port = 0,
    [ValidateSet('client', 'host')]
    [string] $Role = 'client',
    [int]    $GamePort = 0,
    [string] $ClientId,
    [string] $Username,
    [int]    $Width  = 800,
    [int]    $Height = 600,
    [bool]   $ForceGameplayInput = $true,
    [bool]   $SeedMods = $true,
    [switch] $Force,

    [switch] $All,

    [switch] $Start,
    [string] $Desktop = 'StationeersRig',

    [switch] $Stop,

    [switch] $Save,
    [string] $Name,

    [switch] $Status,
    [switch] $List,
    [switch] $Remove,

    [switch] $Wait,
    [ValidateSet('ping', 'modsLoaded', 'menu', 'inWorld')]
    [string] $Stage = 'menu',

    [switch] $Broadcast,
    [switch] $Call,
    [string] $Path,
    [string] $Body,

    [switch] $Snapshot,
    [string] $OutFile,

    [int]    $TimeoutSeconds = 30,
    [int]    $WaitSeconds    = 300,
    [int]    $CallTimeoutSeconds = 0,

    [switch] $Logs,
    [int]    $Tail = 50,
    [string] $Grep,

    [string] $InstancesRoot,

    [switch] $Lock,
    [switch] $RefreshLock,
    [switch] $Unlock,
    [string] $Purpose,
    [string] $As,
    [switch] $BreakLock,
    [int]    $TtlMinutes = 10,
    [switch] $Release,
    [switch] $KeepState
)

$ErrorActionPreference = 'Stop'

# $PSBoundParameters is per-scope: a function gets its OWN, so a function body reading it sees an
# empty dictionary and every ContainsKey test there silently answers false. Anything below that
# needs to know whether a switch was actually TYPED (as opposed to sitting on its default) reads
# this capture instead. -Role and -GamePort need it so a rebuild does not silently demote a host,
# and -TtlMinutes / -WaitSeconds need it so an untyped default is not mistaken for a real value.
$ScriptBoundParams = $PSBoundParameters

$RigRoot       = $PSScriptRoot
# <repo>/TestRig/ClientRig -> <repo>/TestRig -> <repo>
$TestRigRoot   = Split-Path -Parent $RigRoot
$RepoRoot      = Split-Path -Parent $TestRigRoot
$BuildPropsXml = Join-Path $RepoRoot 'Directory.Build.props'

# The session lock is rig-wide (it covers TestRig/DedicatedServer/ too) and its whole mechanism lives
# in one shared file, so the two halves cannot drift apart on the timer, ownership or break-lock
# rules. Rules: TestRig/session.lock.template.
$RigLockLib = Join-Path $TestRigRoot 'rig-lock.ps1'
if (-not (Test-Path $RigLockLib)) {
    throw "Shared rig-lock implementation not found at $RigLockLib. It is committed alongside this launcher; restore it before driving the rig."
}
. $RigLockLib

# State hygiene, dot-sourced AFTER the lock library because it extends it: a new
# lock resets what the previous session left behind. It also owns
# Set-RigSavePathOverride, which this launcher calls from -Provision. That
# function is not duplicated here on purpose: it writes the one setting standing
# between a driven instance and the developer's tier-1 save folder, and two
# copies of a safety write is how one of them stops matching the other.
$RigResetLib = Join-Path $TestRigRoot 'rig-reset.ps1'
if (-not (Test-Path $RigResetLib)) {
    throw "Shared rig-reset implementation not found at $RigResetLib. It is committed alongside this launcher and carries the SavePathOverride write that keeps a driven instance out of the developer's saves; restore it before driving the rig."
}
. $RigResetLib

# The instance trees are hard links into the game install, so they must sit on the install's
# volume. The repository frequently does not, so this is relocatable and the volume check below
# turns a wrong setting into a clear message rather than a 7 GB copy.
#
# This is the root a NEW instance is built in. An instance that already exists uses the root
# recorded in its registry entry instead (Resolve-InstanceRoot), because -InstancesRoot used to be
# honoured by -Provision and forgotten by everything after it: -Start reported a provisioned
# instance as having no tree, and the state reset could not find its BepInEx config.
$InstancesRootTyped = $ScriptBoundParams.ContainsKey('InstancesRoot') -and $InstancesRoot
$InstancesDir  = if ($InstancesRoot) { $InstancesRoot }
                 elseif ($env:STATIONEERS_CLIENTRIG_ROOT) { $env:STATIONEERS_CLIENTRIG_ROOT }
                 else { Join-Path $RigRoot 'instances' }
$InstancesDirSource = if ($InstancesRoot) { '-InstancesRoot' }
                      elseif ($env:STATIONEERS_CLIENTRIG_ROOT) { '$env:STATIONEERS_CLIENTRIG_ROOT' }
                      else { 'the default instances/ folder beside this script' }

# Per-instance state (manifest, settings, save root, logs, PID file) is ordinary files, not links,
# so it stays beside the script regardless of which volume the trees are on.
$DataDir       = Join-Path $RigRoot 'data'
$RigRegistry   = Join-Path $DataDir 'rig.json'

# Dev-plugin layout, identical to the dedicated server's: dev-plugins/<Name>/<Name>.sln beside
# dev-plugins/<Name>/<Name>/ source. See TestRig/CLAUDE.md.
$PluginSln     = Join-Path $RigRoot 'dev-plugins\ClientDriver\ClientDriver.sln'
$PluginDll     = Join-Path $RigRoot 'dev-plugins\ClientDriver\ClientDriver\bin\Release\ClientDriver.dll'

# Default port bands. Two per instance, both derived from the index so a rig provisioned with no
# flags never collides with itself: control plane on TCP 27700+index, RakNet game port on UDP
# 27800+index. Both bands are clear of Steam (27000-27050), of the Stationeers client's own
# defaults, and of this repository's dedicated server.
$ControlPortBase = 27700
$GamePortBase    = 27800

# What Get-Process reports for a running instance (rocketstation.exe, minus the extension). Used to
# tell a live instance from a pid file whose number was recycled; see Test-PidAlive. The lock library
# and the state reset use the same name for the same check.
$GameImageName   = 'rocketstation'

# Control-plane HTTP timeouts for -Call and -Broadcast.
#
# Invoke-RestMethod defaults to 100 s and the launcher used to pin 120 s (-Call) and 60 s
# (-Broadcast), which meant the CALLER's own timeoutMs was ignored: -Path /connect with
# timeoutMs 300000 died at 120 s with a client-side timeout, and the plugin's answer, which is the
# only thing that says WHY a join or a host attempt failed, was never read. So the timeout is
# derived from the request instead, and these are the bounds it moves between.
$ControlTimeoutFloorSeconds  = 120   # short endpoints: no shorter than the old fixed value
$ControlTimeoutMarginSeconds = 30    # the launcher must outlive the plugin's own deadline, not race it
$ControlTimeoutCeilingSeconds = 3600 # a typo in timeoutMs must not wedge the launcher for days
# Endpoints that legitimately block for minutes. Their plugin-side default timeoutMs is 120000 to
# 300000 (Routes.Session.cs, Routes.Host.cs), and a caller that names none gets the plugin's default
# rather than the launcher's, so the floor here has to cover the LARGEST of them.
$ControlLongPathSeconds = 300
$ControlLongPaths = @('/host', '/connect', '/save', '/load', '/newworld', '/waitfor')

# Ports a rig instance's game port must never take. A second RakNet socket on an already-bound
# port does not fail: both bindings coexist and traffic is routed by destination address, so the
# joiner reaches SOMETHING and the test is confidently wrong. The refusal is the only warning
# there will ever be.
$ReservedGamePorts = @{
    27015 = "the Stationeers client's own default UpdatePort"
    27016 = "the Stationeers client's own default GamePort"
    28015 = "this repository's dedicated server UpdatePort (TestRig/DedicatedServer)"
    28016 = "this repository's dedicated server GamePort (TestRig/DedicatedServer)"
}

# ---- environment helpers --------------------------------------------------

function Get-StationeersPath {
    if (-not (Test-Path $BuildPropsXml)) {
        throw "Directory.Build.props not found at repo root. Copy Directory.Build.props.template to Directory.Build.props and set <StationeersPath>. See DEV.md."
    }
    $xml  = [xml](Get-Content -Raw $BuildPropsXml)
    $path = $xml.Project.PropertyGroup.StationeersPath
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "<StationeersPath> in Directory.Build.props is empty. Set it to your Stationeers client install. See DEV.md."
    }
    if (-not (Test-Path (Join-Path $path 'rocketstation.exe'))) {
        throw "<StationeersPath>=$path does not contain rocketstation.exe. This rig links the CLIENT install, not the dedicated server. See DEV.md."
    }
    return $path
}

function Get-UserDataPath {
    # The game's own user-data root: Documents\My Games\Stationeers. Resolved from the Windows
    # shell folder rather than hardcoded, so nothing here is tied to one developer's layout.
    return (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers')
}

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

function Get-PidFromFile {
    param([string] $File)
    if (-not (Test-Path $File)) { return $null }
    $raw = (Get-Content -Raw -ErrorAction SilentlyContinue $File)
    if (-not $raw) { return $null }
    $val = $raw.Trim()
    if (-not $val) { return $null }
    [int]$val
}

function Test-PidAlive {
    # Is the process this pid file names really THIS instance's game client.
    #
    # The image name is checked, not just the number, and that is load bearing in both directions.
    # Windows recycles process ids and these pid files outlive their processes on a force-kill, a
    # crash or a reboot, so a bare Get-Process answers "alive" for whatever unrelated program
    # inherited the number. Every caller here then draws the wrong conclusion: -Start THROWS rather
    # than launching ("already running"), -Provision and -Remove refuse, and -Status reports a
    # stopped instance as up. The same reasoning, and the same helper, as rig-lock.ps1 and the
    # state reset, which is why this calls Get-RigLiveProcess rather than repeating it.
    param([Nullable[int]] $TargetPid)
    return ($null -ne (Get-RigLiveProcess -TargetPid $TargetPid -ImageName $GameImageName))
}

# ---- session lock ---------------------------------------------------------
#
# Rules: TestRig/session.lock.template. Implementation: TestRig/rig-lock.ps1,
# dot-sourced above and shared with dedicated-server.ps1.
#
# Every action that changes rig state goes through this gate, for the same reason the dedicated
# server has one. Without it, -Stop -All tears down another agent's live test with no trace, -Remove
# deletes an instance's save root out from under a run, and two concurrent -Provision calls read the
# registry before either writes it, pick the same free index, and hand two instances one ClientId.
# That last one is the failure this script already refuses to allow within a single call, and for
# the same stated reason: the server keys a player's body on ClientId, so a test that believes it
# has two players actually has one, and the results look plausible and mean nothing.

function Assert-MutatingAllowed {
    param([Parameter(Mandatory)] [string] $Action)
    Assert-RigLockHeld -Action $Action -CallerId $As -Tool 'client-rig.ps1'
}

# ---- the rig registry -----------------------------------------------------
#
# One file listing every instance. It is what makes -All work, and it is where each instance's
# manifest gets its peerPorts list, which is what lets an instance notice a sibling claiming the
# same ClientId.

function Read-Registry {
    if (-not (Test-Path $RigRegistry)) { return @() }
    try {
        $json = Get-Content -Raw $RigRegistry | ConvertFrom-Json
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
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $tmp = "$RigRegistry.tmp"
    ,@($Entries) | ConvertTo-Json -Depth 8 | Set-Content -Path $tmp -Encoding utf8
    Move-Item -Path $tmp -Destination $RigRegistry -Force
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

function Resolve-Targets {
    # Which instances an action applies to: -All, an explicit -Instance list, or a refusal.
    #
    # The local is deliberately NOT named $all. PowerShell variable names are case-insensitive, so
    # `$all = Read-Registry` silently overwrites the -All switch parameter with a non-empty array,
    # and every action then behaves as though -All had been passed. That bug shipped once and made
    # `-Stop -Instance nope` stop the whole rig instead of refusing.
    $registry = Read-Registry
    if ($All) {
        if ($registry.Count -eq 0) { throw "No instances are provisioned. Run: client-rig.ps1 -Provision -Instance client1" }
        return $registry
    }
    if (-not $Instance) {
        throw "Specify -Instance <name[,name]> or -All. Run -List to see what is provisioned."
    }
    $wanted = $Instance.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    $hits = @()
    foreach ($w in $wanted) {
        $e = $registry | Where-Object { $_.instanceName -eq $w } | Select-Object -First 1
        if (-not $e) { throw "Instance '$w' is not provisioned. Run -List, or provision it with -Provision -Instance $w." }
        $hits += $e
    }
    return $hits
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
    if ($InstancesRootTyped) {
        return [pscustomobject]@{ Root = $InstancesDir; Source = '-InstancesRoot (typed on this command)' }
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
        Write-Host "[Rig] Instance '$Name' was provisioned before the instances root was recorded; using $InstancesDirSource ($InstancesDir). Re-record it with: client-rig.ps1 -Provision -Force -As <id> -Instance $Name"
    }
    return [pscustomobject]@{ Root = $InstancesDir; Source = $InstancesDirSource }
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
        Data       = Join-Path $DataDir $Name
        Manifest   = Join-Path $DataDir "$Name\instance.json"
        PidFile    = Join-Path $DataDir "$Name\game.pid"
        Settings   = Join-Path $DataDir "$Name\setting.xml"
        UserData   = Join-Path $DataDir "$Name\userdata"
        LogDir     = Join-Path $DataDir "$Name\logs"
    }
}

# Both shared libraries default to the rig root, which is right for everything except the instance
# tree location: -InstancesRoot is a launcher flag neither of them can see. Re-point them here, once
# the registry helpers above exist, so the reset looks inside the trees this rig actually has and the
# lock's orphan scan watches the same ones. Initialize-RigResetPaths re-points the lock library too.
#
# A recorded root wins over the launcher default here, and only here: the shared libraries take ONE
# root, and a rig whose instances were built under -InstancesRoot has its trees there whether or not
# this shell happens to have the environment variable set. $InstancesDir itself is deliberately NOT
# touched, so the launcher's own fallback for an entry that records nothing stays exactly what it was
# and the note it prints names the real source rather than a sibling's root.
#
# The reset resolves each instance's tree from the registry itself, so a rig split across two roots
# (only reachable by moving one instance with -InstancesRoot) still resets correctly; what the single
# value costs there is the orphan scan missing a stray process out of the second root, which is a
# reporting gap rather than a safety one.
$LibInstanceRoot = $InstancesDir
if (-not $InstancesRootTyped) {
    $recordedRoots = @(Read-Registry |
        ForEach-Object { [string](Get-EntryValue $_ 'instancesRoot' '') } |
        Where-Object { $_ } | Select-Object -Unique)
    if ($recordedRoots.Count -ge 1) { $LibInstanceRoot = $recordedRoots[0] }
}
Initialize-RigResetPaths -RigHome $TestRigRoot -InstanceRoot $LibInstanceRoot

# ---- provisioning ---------------------------------------------------------

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
        throw "-GamePort $Candidate is out of range. Use 1024-65535; the rig's own band is $GamePortBase plus the instance index."
    }
    if ($ReservedGamePorts.ContainsKey($Candidate)) {
        throw "-GamePort $Candidate is $($ReservedGamePorts[$Candidate]). Two RakNet sockets on one port do not conflict, they coexist and route by destination address, so a joiner would reach whichever one won and the test would be wrong with no error anywhere. Pick another port; the rig's own band is $GamePortBase plus the instance index."
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

function Invoke-Provision {
    if (-not $Instance) { throw "-Provision requires -Instance <name>." }
    if ($Instance.Contains(',')) { throw "-Provision takes one instance at a time." }
    # Held across the whole read-modify-write of the registry below, which is what stops two
    # concurrent provisions from selecting the same index and therefore the same ClientId.
    Assert-MutatingAllowed -Action 'Provision'

    $source = Get-StationeersPath

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
    $effRoot = if ($InstancesRootTyped) { $InstancesDir }
               elseif ($recordedRoot)   { $recordedRoot }
               else                     { $InstancesDir }
    if ($InstancesRootTyped -and $recordedRoot -and $recordedRoot -ne $effRoot) {
        Write-Warning "[Provision] '$Instance' was built under $recordedRoot and -InstancesRoot moves it to $effRoot. The old tree at $(Join-Path $recordedRoot $Instance) is NOT deleted (this launcher only ever removes the tree it is about to rebuild); delete it by hand once the rebuild succeeds."
    }

    $p = Get-InstancePaths -Name $Instance -Root $effRoot
    if ((Test-Path $p.Tree) -and -not $Force) {
        throw "Instance '$Instance' already exists at $($p.Tree). Pass -Force to rebuild it, or -Remove -Instance $Instance to delete it first."
    }
    if (Test-PidAlive (Get-PidFromFile $p.PidFile)) {
        throw "Instance '$Instance' is running. Stop it first: client-rig.ps1 -Stop -Instance $Instance"
    }

    $effPort = if ($Port -gt 0) { $Port } else { $ControlPortBase + $index }
    $effId   = if ($ClientId) { $ClientId } else { (900000000000 + $index).ToString() }
    $effName = if ($Username) { $Username } else { $Instance }

    # Role and game port are KEPT across a rebuild unless they are typed again. -Provision -Force is
    # the routine way to pick up a new plugin build, and silently demoting a host to a client (or
    # moving its game port out from under a joiner's -Body) on the way through would be a trap.
    $effRole = if ($ScriptBoundParams.ContainsKey('Role')) { $Role }
               elseif ($existing) { [string](Get-EntryValue $existing 'role' $Role) }
               else { $Role }
    $effGamePort = if ($GamePort -gt 0) { $GamePort }
                   elseif ($existing) { [int](Get-EntryValue $existing 'gamePort' ($GamePortBase + $index)) }
                   else { $GamePortBase + $index }

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
    Write-AllManifests
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
        Write-Host "[Provision] Next: -Start, -Wait -Stage menu, then -Call -Path /host. Joiners reach it at 127.0.0.1:$effGamePort."
        Write-Host "[Provision]       The host must be in its world BEFORE any joiner connects."
    }
    else {
        Write-Host "[Provision] Next: client-rig.ps1 -Start -Instance $Instance"
    }
}

function Invoke-DeployPlugin {
    param([Parameter(Mandatory)] $Paths)
    if (-not (Test-Path $PluginDll)) {
        Write-Warning "[$($Paths.Name)] ClientDriver.dll not found at $PluginDll. Build it first: dotnet build $PluginSln -c Release. The instance will run without a control plane."
        return
    }
    $dst = Join-Path $Paths.BepInEx 'plugins\ClientDriver'
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item $PluginDll (Join-Path $dst 'ClientDriver.dll') -Force
    # BepInEx/plugins/ is loaded by the Chainloader directly, before StationeersLaunchPad runs.
    # The DLL must not ALSO sit under a StationeersLaunchPad mod folder: two loaders means Awake
    # twice and every Harmony patch registered twice.
    Write-Host "[Provision] ClientDriver -> $dst"
}

function Invoke-SeedMods {
    param([Parameter(Mandatory)] $Paths)

    $userData = Get-UserDataPath
    $srcMods  = Join-Path $userData 'mods'
    $srcCfg   = Join-Path $userData 'modconfig.xml'
    if (-not (Test-Path $srcCfg)) {
        Write-Warning "[$($Paths.Name)] No modconfig.xml at $srcCfg; skipping the mod seed. The instance will load Workshop mods only."
        return
    }

    Write-Host "[Provision] Seeding mods from the user data folder (read-only source) ..."
    $dstMods = Join-Path $Paths.UserData 'mods'
    if (Test-Path $dstMods) { Remove-Item $dstMods -Recurse -Force }
    if (Test-Path $srcMods) { Copy-Item $srcMods $dstMods -Recurse -Force }
    else { New-Item -ItemType Directory -Force -Path $dstMods | Out-Null }

    # Local mod entries are absolute paths, and StationeersLaunchPad prunes entries whose folder is
    # not under the active save path, so each instance needs its own copy and its own modconfig.
    $xml = Get-Content $srcCfg -Raw
    if (Test-Path $srcMods) { $xml = $xml.Replace($srcMods, $dstMods) }
    Set-Content -Path (Join-Path $Paths.UserData 'modconfig.xml') -Value $xml -Encoding utf8 -NoNewline

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

function Get-SourceInstallVersion {
    # The game version the instance was linked from. version.txt is the same string the repository
    # keys .work/decomp/<game-version>/ off; the executable's own version is the fallback.
    param([Parameter(Mandatory)] [string] $SourceInstall)
    try {
        $versionTxt = Join-Path $SourceInstall 'version.txt'
        if (Test-Path $versionTxt) {
            $v = (Get-Content -Raw -ErrorAction Stop $versionTxt).Trim()
            if ($v) { return $v }
        }
    } catch { }
    try {
        $exe = Join-Path $SourceInstall 'rocketstation.exe'
        if (Test-Path $exe) {
            $v = (Get-Item $exe).VersionInfo.FileVersion
            if ($v) { return $v.Trim() }
        }
    } catch { }
    return 'unknown'
}

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
        if (Test-Path $PluginDll) {
            $pluginBuilt = (Get-Item $PluginDll).LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
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
        sourceVersion   = (Get-SourceInstallVersion -SourceInstall $SourceInstall)
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
            rigRoot       = $RigRoot
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

function Format-Arg {
    param([string] $Value)
    if ($Value -match '[\s"]') { return '"' + ($Value -replace '"', '\"') + '"' }
    return $Value
}

function Invoke-Start {
    Assert-MutatingAllowed -Action 'Start'
    Add-LauncherType
    $targets = Resolve-Targets

    # Pre-flight the whole set BEFORE launching anything, and refuse rather than skip.
    #
    # Both of these used to be a warning and a `continue`. A skipped start is the worst possible
    # outcome: -Start -All comes back looking successful, the instance that was skipped is still in
    # whatever world it was already in (or is not there at all), and every assertion afterwards runs
    # against a rig that is not the one the caller asked for. dedicated-server.ps1 -Start throws on
    # an already-running server for exactly this reason; this half now agrees.
    foreach ($e in $targets) {
        $p = Get-InstancePaths -Name $e.instanceName -Entry $e
        if (-not (Test-Path $p.Exe)) {
            # The root is named along with WHERE IT CAME FROM, because the usual cause of this
            # message is that the tree is somewhere else entirely: an instance built under
            # -InstancesRoot used to be looked for under instances/ beside this script, and the
            # message read as "unprovisioned" when the tree was sitting on another volume.
            throw "[Start] Instance '$($e.instanceName)' is in the registry but has no tree at $($p.Exe). That location came from $($p.RootSource). Rebuild it there (client-rig.ps1 -Provision -Force -As <id> -Instance $($e.instanceName)), or name the root the tree actually has with -InstancesRoot <root>, which also records it for every later command."
        }
        $running = Get-PidFromFile $p.PidFile
        if (Test-PidAlive $running) {
            throw "[Start] Instance '$($e.instanceName)' is already running (PID $running). Nothing was started. Stop it first (client-rig.ps1 -Stop -As <id> -Instance $($e.instanceName)) or check -Status. A start that silently skipped would leave it in whatever world it is already in."
        }
        if ($null -ne $running) {
            # A pid file whose process is gone, or whose number now belongs to something that is not
            # the game. Test-PidAlive checks the process image for exactly this case: refusing to
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
        Write-AllManifests

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

        $commandLine = ((@($p.Exe) + $argv) | ForEach-Object { Format-Arg ([string]$_) }) -join ' '

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
    Write-Host "[Start]   client-rig.ps1 -Wait -All -Stage menu"

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
        Write-Host "[Start]   client-rig.ps1 -Wait -Instance $($h.instanceName) -Stage menu"
        Write-Host "[Start]   client-rig.ps1 -Call -As <id> -Instance $($h.instanceName) -Path /host -Body '{`"world`":`"Lunar`"}'"
        Write-Host "[Start]   client-rig.ps1 -Wait -Instance $($h.instanceName) -Stage inWorld -WaitSeconds 600"
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
    $procId = Get-PidFromFile $paths.PidFile
    $alive  = Test-PidAlive $procId
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
    # Same contract as dedicated-server.ps1 -Save, for the same reason: "the request was accepted"
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
        Write-Warning "[$($Runtime.Name)] Treat this world as NOT saved. client-rig.ps1 -Logs -Instance $($Runtime.Name), or GET /console/log?contains=Saved, shows what the game actually did."
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
    $procId = Get-PidFromFile $Runtime.Paths.PidFile
    if (-not (Test-PidAlive $procId)) {
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
    while ((Get-Date) -lt $deadline -and (Test-PidAlive $procId)) { Start-Sleep -Milliseconds 500 }

    if (Test-PidAlive $procId) {
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

function Invoke-Stop {
    # -Stop is gated exactly like the rest. It is the single most destructive action here: -Stop -All
    # ends every instance in the rig, and a torn-down client cannot report afterwards that its run
    # was interrupted, so the results of the interrupted test simply look wrong.
    $st = Get-RigLockState -CallerId $As
    if ($st.State -eq 'LiveForeign') {
        if (-not $BreakLock) {
            throw "[Stop] Refusing to stop clients held by another live session.`n$(Format-ForeignRigLock $st)`nReport to the user. Only the user may authorize -BreakLock. See TestRig/session.lock.template."
        }
        Write-Warning "[Stop] -BreakLock: stopping clients held by another live session ('$($st.Lock['purpose'])')."
    }

    $targets = Resolve-Targets
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
            throw "$text`nNothing was stopped. Take the joiners down too (-All, or name them with -Instance), or pass -Force to end the world under them. -Force is the same-session override and never touches the rig lock; taking a lock off another session is -BreakLock."
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
            if (-not (Save-InstanceWorld -Runtime $rt -SaveName $Name -WaitSec $WaitSeconds)) {
                if (-not $Force) {
                    throw "[Stop] '$($rt.Name)' holds a world and its save was not confirmed. Stopping the sequence here rather than quitting on top of it. Retry, save it by hand (client-rig.ps1 -Save -As <id> -Instance $($rt.Name)), or pass -Force to quit and accept the loss."
                }
                Write-Warning "[$($rt.Name)] Save not confirmed; -Force, quitting anyway. Treat that world as lost."
            }
        }

        Stop-InstanceProcess -Runtime $rt -TimeoutSec $timeout
        Reset-StartLocalHostFlag -Runtime $rt
    }

    if ($Release) {
        $lock = Read-RigLock
        if (-not $lock) {
            Write-Host "[Stop] No rig session lock to release."
        }
        elseif (($As -and $lock['owner'] -eq $As) -or $BreakLock -or (Test-RigLockTimerExpired $lock)) {
            Remove-Item -Force -ErrorAction SilentlyContinue (Get-RigLockFilePath)
            Write-Host "[Stop] Rig session lock released."
            # -Stop -Release is a session end too, so it gets the same shared-state
            # drift report -Unlock prints.
            Write-RigSharedStateDrift
        }
        else {
            Write-Warning "[Stop] -Release ignored: lock held by '$($lock['owner'])', not you. Use -Unlock -As <id>, or get user authorization for -BreakLock."
        }
    }
}

function Invoke-Save {
    # The client rig's answer to dedicated-server.ps1 -Save, and until now the biggest guarantee gap
    # between the two halves: this half could create a world and had no way to persist one.
    Assert-MutatingAllowed -Action 'Save'
    $targets = Resolve-Targets
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
        if (-not (Save-InstanceWorld -Runtime $rt -SaveName $Name -WaitSec $WaitSeconds)) { $failed++ }
    }
    if ($failed -gt 0) {
        Write-Warning "[Save] $failed of $($targets.Count) instance(s) did not confirm a save. Do not treat those worlds as persisted: -Logs and /console/log show what each instance actually did."
    }
}

function Invoke-Remove {
    if (-not $Instance) { throw "-Remove requires -Instance <name>." }
    if ($Instance.Contains(',')) { throw "-Remove takes one instance at a time." }
    # -Remove deletes the instance's own save root under data/<name>/userdata/ along with the tree.
    # That is tier 3 (agent-managed) by design, but it belongs to whoever holds the lock.
    Assert-MutatingAllowed -Action 'Remove'
    $p = Get-InstancePaths -Name $Instance
    if (Test-PidAlive (Get-PidFromFile $p.PidFile)) {
        throw "Instance '$Instance' is running. Stop it first: client-rig.ps1 -Stop -Instance $Instance"
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
    Write-AllManifests
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
    param([string] $Path, [string] $BodyJson)
    if ($CallTimeoutSeconds -gt 0) { return $CallTimeoutSeconds }

    $bare  = ([string]$Path).Split('?')[0].TrimEnd('/')
    $floor = if ($ControlLongPaths -contains $bare.ToLowerInvariant()) { $ControlLongPathSeconds }
             else { $ControlTimeoutFloorSeconds }

    $asked = Get-RequestedTimeoutMs -Path $Path -BodyJson $BodyJson
    if ($asked -gt 0) {
        $derived = [int][Math]::Min($ControlTimeoutCeilingSeconds,
                                    [Math]::Ceiling($asked / 1000.0) + $ControlTimeoutMarginSeconds)
        if ($derived -ge $ControlTimeoutCeilingSeconds) {
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
    try {
        if ($Want -eq 'ping') { Invoke-Control -Port $Port -Path '/ping' -TimeoutSec 3 | Out-Null; return $true }
        $s = Invoke-Control -Port $Port -Path '/status' -TimeoutSec 5
        switch ($Want) {
            'modsLoaded' { return ([int]$s.loadedPluginCount -gt 10) }
            'menu'       { return ($s.gameInitialized -eq $true -and $s.phase -eq 'menu') }
            'inWorld'    { return ($s.phase -eq 'inWorld') }
        }
        return $false
    }
    catch { return $false }
}

function Invoke-Wait {
    $targets = Resolve-Targets
    # Read-only, so it is not gated. It does refresh a lock you already hold, because a barrier can
    # legitimately run longer than the TTL (600 s for inWorld against a 10 min default) and losing
    # the rig halfway through a wait would be absurd. Silent no-op when you hold nothing.
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

function Invoke-Broadcast {
    if (-not $Path) { throw "-Broadcast requires -Path <control-plane path>, for example -Path /config/set." }
    # Gated because it drives LIVE clients. /quit ends one, and /savepath retargets where one writes
    # its saves (it only refuses the real user-data folder, and only without force=true).
    Assert-MutatingAllowed -Action 'Broadcast'
    $targets = Resolve-Targets
    # Same derived timeout as -Call, and for the same reason: the fixed 60 s here was even shorter
    # than -Call's 120 s, so a fan-out of anything that blocks gave up on every instance at once.
    $timeoutSec = Get-ControlTimeoutSeconds -Path $Path -BodyJson $Body
    Write-Host "[Broadcast] $Path -> $($targets.Count) instance(s), up to ${timeoutSec}s each"
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
        throw "[Broadcast] $failed of $($targets.Count) instance(s) failed. A partial broadcast leaves the rig in mixed state; fix and re-run before drawing any conclusion from a test."
    }
}

function Invoke-Call {
    if (-not $Path) { throw "-Call requires -Path <control-plane path>." }
    if (-not $Instance) { throw "-Call requires -Instance <name>." }
    Assert-MutatingAllowed -Action 'Call'
    $e = Get-InstanceEntry -Name $Instance
    if (-not $e) { throw "Instance '$Instance' is not provisioned. Run -List." }
    # Derived from the request rather than pinned, so -Path /connect -Body '{"timeoutMs":300000}'
    # actually gets its five minutes. See Get-ControlTimeoutSeconds.
    $timeoutSec = Get-ControlTimeoutSeconds -Path $Path -BodyJson $Body
    Write-Host "[Call] $Instance $Path (up to ${timeoutSec}s)"
    $r = Invoke-Control -Port ([int]$e.port) -Path $Path -BodyJson $Body -TimeoutSec $timeoutSec
    $r | ConvertTo-Json -Depth 10
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
        throw "[Snapshot] -OutFile '$Value' is drive-relative: Windows resolves it against a per-drive working directory, so nothing here can say where it would land. Pass a path relative to the rig folder ($RigRoot), or a full path including the leading backslash."
    }
    $target  = if ($qualified) { $Value } else { Join-Path $RigRoot $Value }
    $full    = [IO.Path]::GetFullPath($target)
    $rigBase = [IO.Path]::GetFullPath($RigRoot).TrimEnd('\', '/') + '\'
    $inside  = $full.StartsWith($rigBase, [StringComparison]::OrdinalIgnoreCase)

    if (-not $qualified -and -not $inside) {
        throw "[Snapshot] -OutFile '$Value' climbs out of the rig folder (it resolves to $full). A relative -OutFile is rooted at $RigRoot, which is gitignored deny-all, precisely so a stray snapshot cannot be committed by accident. Drop the '..' segments, or pass a full path if you really mean to write outside the rig."
    }
    if (-not $inside) {
        Write-Warning "[Snapshot] $full is outside the rig folder, so the deny-all gitignore does not cover it. Make sure it is not somewhere that gets committed."
    }
    return $full
}

function Invoke-Snapshot {
    $targets = Resolve-Targets
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

function Invoke-Status {
    # The lock is rig-wide, so this is the same block dedicated-server.ps1 -Status prints.
    Write-RigLockStatus -CallerId $As
    Write-Host ""

    $registry = Read-Registry
    if ($registry.Count -eq 0) {
        Write-Host "No instances are provisioned. Create one: client-rig.ps1 -Provision -Instance client1"
        return
    }
    $wanted   = if ($Instance) { @($Instance.Split(',') | ForEach-Object { $_.Trim() }) } else { $null }
    $selected = @($registry | Where-Object { -not $wanted -or ($wanted -contains $_.instanceName) })
    if ($selected.Count -eq 0) {
        Write-Host "No provisioned instance matches -Instance '$Instance'. Run -List to see what is provisioned."
        return
    }
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

function Invoke-List {
    $registry = Read-Registry
    if ($registry.Count -eq 0) {
        Write-Host "No instances are provisioned."
        return
    }
    # Only instances whose process is alive are probed, so -List on a cold rig makes no HTTP call at
    # all and still answers instantly. The live columns are '-' for those.
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
    $rows | Format-Table -AutoSize
}

function Invoke-Logs {
    if (-not $Instance) { throw "-Logs requires -Instance <name>." }
    $p = Get-InstancePaths -Name $Instance
    $log = Join-Path $p.BepInEx 'LogOutput.log'
    if (-not (Test-Path $log)) {
        Write-Host "No BepInEx log at $log."
        return
    }
    if ($Grep) { Get-Content $log | Select-String -Pattern $Grep }
    else       { Get-Content -Tail $Tail $log }
}

# ---- session lock actions -------------------------------------------------

function Invoke-Lock {
    if (-not $Purpose) {
        throw "-Lock requires -Purpose `"<short reason>`", e.g. -Purpose `"Two-client paint check for SprayPaintPlus`". See TestRig/session.lock.template."
    }
    # No -OnReclaim: a running instance keeps the lock LIVE, so a lock can never be reclaimable while
    # this half still has processes up. Reclaiming here therefore never has an orphan to clean.
    # -KeepState is forwarded because a NEW lock resets the rig's between-session state
    # (TestRig/rig-reset.ps1). Opting out is loud on purpose: staging a save or a config value
    # deliberately has to stay possible without becoming the silent default.
    $lockArgs = @{
        Purpose    = $Purpose
        CallerId   = $As
        TtlMinutes = $TtlMinutes
        BreakLock  = [bool]$BreakLock
        KeepState  = [bool]$KeepState
        Tool       = 'client-rig.ps1'
    }
    if ($ScriptBoundParams.ContainsKey('WaitSeconds')) {
        # Queueing is opt-in and lives in the shared implementation, so -WaitSeconds is forwarded
        # only when it was actually typed: its default here is 300 (the readiness barrier), while
        # the lock's default must stay 0, meaning refuse immediately. Forwarding the default would
        # silently turn every -Lock into a five-minute wait.
        #
        # The two files are versioned together but not written atomically, so a copy of
        # rig-lock.ps1 without queueing gets a message that names the problem instead of a
        # parameter-binding error about a switch nobody typed.
        $newLock = Get-Command New-RigLock -ErrorAction SilentlyContinue
        if (-not $newLock -or -not $newLock.Parameters.ContainsKey('WaitSeconds')) {
            throw "-Lock -WaitSeconds needs queueing support in the shared lock implementation at $RigLockLib, and this copy does not have it. Drop -WaitSeconds for the immediate refusal, or update TestRig/rig-lock.ps1."
        }
        $lockArgs['WaitSeconds'] = $WaitSeconds
    }
    New-RigLock @lockArgs | Out-Null
}

function Invoke-RefreshLock {
    if (-not $As) { throw "-RefreshLock requires -As <id> (the owner id printed by -Lock)." }
    # $ScriptBoundParams, not $PSBoundParameters: the latter is per-scope and a function gets its
    # own (empty) copy, so the test here silently answered false and -RefreshLock -TtlMinutes N
    # never actually changed the TTL.
    if ($ScriptBoundParams.ContainsKey('TtlMinutes')) { Update-RigLock -CallerId $As -TtlMinutes $TtlMinutes }
    else                                              { Update-RigLock -CallerId $As }
}

function Invoke-Unlock {
    $lock = Read-RigLock
    if ($lock -and ($As -and $lock['owner'] -eq $As)) {
        $busy = Get-RigBusySignal
        if ($busy.Busy) {
            Write-Warning "[Unlock] Releasing while the rig is still busy ($($busy.Detail)). Stop it first: client-rig.ps1 -Stop -All -As $As"
        }
    }
    # -Force is forwarded so the host refusal inside Remove-RigLock can be
    # overridden from THIS launcher too. Without it the refusal was only
    # escapable from dedicated-server.ps1, which is the half that has no hosts.
    Remove-RigLock -CallerId $As -BreakLock:$BreakLock -Force:$Force

    # Only after a SUCCESSFUL release, because Remove-RigLock throws on a refusal
    # and a drift report on a lock that is still held would be reporting on a
    # session that is not over. The shared per-user state (PlayerCookie-v2.xml,
    # PlayerPrefs, Blueprints) cannot be isolated and is never restored, so naming
    # what moved at the session boundary is all this can honestly do.
    Write-RigSharedStateDrift
}

# ---- dispatch -------------------------------------------------------------

if ($Lock)        { Invoke-Lock;        return }
if ($RefreshLock) { Invoke-RefreshLock; return }
if ($Unlock)      { Invoke-Unlock;      return }
if ($Provision)   { Invoke-Provision;   return }
if ($Start)       { Invoke-Start;       return }
if ($Stop)        { Invoke-Stop;        return }
if ($Save)        { Invoke-Save;        return }
if ($Remove)      { Invoke-Remove;      return }
if ($Wait)        { Invoke-Wait;        return }
if ($Broadcast)   { Invoke-Broadcast;   return }
if ($Call)        { Invoke-Call;        return }
if ($Snapshot)    { Invoke-Snapshot;    return }
if ($Status)      { Invoke-Status;      return }
if ($List)        { Invoke-List;        return }
if ($Logs)        { Invoke-Logs;        return }

@"
Stationeers client rig. Provisions and drives N isolated game clients.

Rig conventions:    $(Join-Path $TestRigRoot 'CLAUDE.md')
Operating manual:   $(Join-Path $RigRoot 'README.md')
Durable internals:  $(Join-Path $RigRoot 'RESEARCH.md')
Session-lock rules: $(Join-Path $TestRigRoot 'session.lock.template') (READ FIRST)

Session lock (acquire before ANY mutating command; pass -As <id> thereafter).
ONE lock covers BOTH TestRig halves, so this id is also what dedicated-server.ps1 expects:
    client-rig.ps1 -Lock -Purpose "<what you are testing>" [-TtlMinutes 10] [-WaitSeconds N]
    client-rig.ps1 -RefreshLock -As <id>                        (while actively testing)
    client-rig.ps1 -Unlock -As <id>                             (release when done)
    Gated: -Provision, -Start, -Stop, -Save, -Remove, -Broadcast, -Call.
    Free:  -Status, -List, -Logs, -Snapshot, -Wait.
    -Lock -WaitSeconds N queues for up to N seconds when another session holds the rig. Default 0,
    which is today's immediate refusal. It is a queue, not a reservation: no fairness is promised.
    Breaking another session's LIVE lock (-BreakLock) is human-gated: only on the user's say-so.
    -BreakLock is NOT -Force. -Force overrides refusals inside your own session and never a lock.

State hygiene: taking a NEW lock RESETS what the previous session left behind, so a test cannot
fail on an unrelated test's leftovers. Per instance that is setting.xml (it carries StartLocalHost),
data/<instance>/userdata/saves/, the logs, imgui.ini, a stale game.pid, BepInEx config (re-copied
from the source install, with SavePathOverride re-applied), LogOutput.log, the assembly cache and
the InspectorPlus request and snapshot folders. Kept: rig.json, instance.json, provision.stamp,
userdata/mods (staleness is REPORTED, the fix is -Provision -Force) and the hard links.
    -Lock -KeepState   skip the reset, loudly, when something was staged on purpose.
    The reset is refused while the rig is in use, and never happens when you re-assert a lock you
    already hold, so an agent refreshing mid-test cannot wipe its own run.
    IT RESETS BETWEEN SESSIONS ONLY. A session spans many start/stop cycles by design, so two
    unrelated tests under ONE lock get no reset between them. Release and re-take the lock when
    the subject changes.

Instance trees are hard links into the game install, so they must be on the install's volume.
Set this once per shell (or record it in DEV.md) when the repository is on a different drive:
    `$env:STATIONEERS_CLIENTRIG_ROOT = '<drive of the game install>\StationeersRig'
Current instances root: $InstancesDir
    ($InstancesDirSource)
    -Provision records the resolved root in the registry entry, so -InstancesRoot is typed once and
    every later command finds the tree without it. Typing it again overrides the recorded value,
    which is how a tree is moved. An instance provisioned before the root was recorded falls back to
    the order above and says so; -Provision -Force records it.

Build the plugin first (the instances get whatever is in bin/Release):
    dotnet build $PluginSln -c Release

Provision (once per instance; ports and identity default off the instance index):
    client-rig.ps1 -Provision -As <id> -Instance client1
    client-rig.ps1 -Provision -As <id> -Instance host1 -Role host        (a listen host; -Role client is the default)
    client-rig.ps1 -Provision -As <id> -Instance client1 -Force          (rebuild, picks up a new plugin build)
    Defaults per instance index: control plane $ControlPortBase+i (TCP), game port $GamePortBase+i (UDP),
    clientId 900000000000+i. Override with -Port / -GamePort / -ClientId / -Username.
    A game port colliding with another instance, with the dedicated server (28015/28016) or with the
    Stationeers client's own defaults (27015/27016) is refused: two RakNet sockets on one port do not
    conflict, they coexist and route by destination address, and the test comes out wrong silently.
    -Force rebuilds the instance TREE. Everything under data/<instance>/ except userdata/mods is kept,
    including its saves, its logs and its setting.xml.

Lifecycle:
    client-rig.ps1 -Start  -As <id> -All                         (isolated desktop, never takes focus)
    client-rig.ps1 -Wait   -All -Stage menu                      (barrier; roughly 100 s from cold)
    client-rig.ps1 -Status -As <id> -All                         (role, hosting, game port, connected clients)
    client-rig.ps1 -Save   -As <id> -Instance host1 [-Name <SaveName>]   (waits for the plugin's confirmation)
    client-rig.ps1 -Stop   -As <id> -All [-Release]
    client-rig.ps1 -Remove -As <id> -Instance client1
    -Start refuses to run over an instance that is already up, rather than skipping it.
    -Stop is host-aware: joiners leave first (confirmed), then a world holder saves (confirmed), then
    the host quits. Stopping or removing a host while a joiner is attached is refused; -Force overrides.

Hosting a world from a driven client (a listen host), in the only order that works.
The host must be IN ITS WORLD before any joiner connects:
    client-rig.ps1 -Start -As <id> -Instance host1
    client-rig.ps1 -Wait  -Instance host1 -Stage menu
    client-rig.ps1 -Call  -As <id> -Instance host1 -Path /host -Body '{"world":"Lunar"}'
    client-rig.ps1 -Wait  -Instance host1 -Stage inWorld -WaitSeconds 600
    client-rig.ps1 -Start -As <id> -Instance client1
    client-rig.ps1 -Wait  -Instance client1 -Stage menu
    client-rig.ps1 -Call  -As <id> -Instance client1 -Path /connect -Body '{"address":"127.0.0.1","port":$($GamePortBase + 1)}'
    client-rig.ps1 -Wait  -Instance client1 -Stage inWorld -WaitSeconds 600

Fan-out:
    client-rig.ps1 -Broadcast -As <id> -All -Path /config/set -Body '{"guid":"net.example","section":"S","key":"K","value":"true"}'
    client-rig.ps1 -Call -As <id> -Instance client1 -Path /input/scroll -Body '{"notches":1}'
    client-rig.ps1 -Snapshot -All -OutFile before.json
    client-rig.ps1 -Wait -All -Stage inWorld -WaitSeconds 600

Timeouts (the first two mean exactly what they mean on dedicated-server.ps1):
    -WaitSeconds N     how long a blocking wait waits: the -Wait barrier (default 300), the -Save
                       confirmation (default 300), the -Lock queue (default 0, meaning no queue).
    -TimeoutSeconds N  process-teardown grace for -Stop before a kill. Default 30.
    -CallTimeoutSeconds N  how long ONE -Call or -Broadcast request may take. Default 0, meaning
                       derive it from the request: its own timeoutMs (body or query) plus $ControlTimeoutMarginSeconds s,
                       floored at $ControlTimeoutFloorSeconds s and at $ControlLongPathSeconds s for $($ControlLongPaths -join ', ').
                       It was a fixed $ControlTimeoutFloorSeconds s, which cut off every long endpoint before the
                       instance could answer, so -Path /host and -Path /connect were unusable here.

Diagnosis:
    client-rig.ps1 -Call -As <id> -Instance client1 -Path /diag/input
    client-rig.ps1 -Logs -Instance client1 -Grep 'ClientDriver'

Traps this script already handles for you, documented in README.md:
    -settings SavePath is never passed (it makes StationeersLaunchPad delete the developer's local mods)
    -logFile is always unique (otherwise the developer's Player-prev.log is zeroed)
    -nographics is never passed (Unity refuses it without -batchmode and pops a modal error)
    SavePathOverride is written on EVERY provision, never behind the mod seed, so an instance can
      never come up writing into the developer's own save folder. A host refuses to provision at all
      without it; a client warns, and that warning is a stop rather than a note.
    A relative -Snapshot -OutFile is rooted at the rig folder, which is gitignored deny-all, rather
      than at the shell's working directory, which for an agent is the repository root. One that
      climbs back out with '..', or a drive-relative 'C:name.json', is refused rather than written.
    -Call and -Broadcast take their HTTP timeout from the request's own timeoutMs, so a long
      endpoint is no longer cut off by the launcher before the instance can answer.
    -InstancesRoot is recorded at provision time, so -Start, -Stop, -Call and the state reset find
      an instance built on another volume instead of reporting it as unprovisioned.
    A pid file whose process is dead, or whose number now belongs to something that is not the game,
      does not make -Start refuse: the process image is checked, not just the number.
"@
