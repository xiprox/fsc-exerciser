<#
.SYNOPSIS
  Copies a built FS Copilot into fsc/, builds the relay into relay/, and rebuilds the
  exerciser against the copy.

.DESCRIPTION
  The copy lives here and not in the tool, because the tool cannot do it: it has
  fsc/FsCopilot.dll and its dependencies mapped, and Windows does not let a process
  replace a file it has loaded. The Re-sync FSC source button therefore starts this script with
  -AfterPid and -Relaunch and then closes the window, so the copy happens in the gap.

  The first sync has to happen out here in any case, since the tool will not compile
  without fsc/FsCopilot.dll and so has no button to press yet.

  The exerciser is rebuilt every time, not just the first: it compiles against the copy, so
  a copy whose API moved would otherwise start an exerciser built for the one before it.

.EXAMPLE
  .\sync-and-rebuild.ps1 -Fsc C:\dev\FsCopilot

.EXAMPLE
  .\sync-and-rebuild.ps1
  Every sync after the first: the checkout is the one recorded in fsc-exerciser.json.
#>
param(
    # An FS Copilot checkout: the directory holding FsCopilot.sln. Needed once - asked for
    # if it is missing - and after that it defaults to the one the last sync recorded.
    [string]$Fsc,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    # Wait for this process to exit before touching anything. The exerciser passes its own.
    [int]$AfterPid = 0,
    # Start this once the copy is done. The exerciser passes its own path.
    [string]$Relaunch
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Three ways in, and they want different endings. Started from File Explorer, this has a
# window of its own that goes when the script does, taking whatever it said with it - so it
# waits for a key. Started by the button, it should get out of the way: the exerciser
# reopening is the answer, and only a failure is worth stopping for, since then nothing
# reopens and the reason would vanish with the window. From a terminal there is nothing to
# hold open, and a prompt would hang a scripted run.
$fromButton = $AfterPid -gt 0
$fromExplorer = $false
try {
    $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId
    $fromExplorer = (Get-Process -Id $parent -ErrorAction Stop).ProcessName -eq 'explorer'
} catch { }

function Hold($message) {
    Write-Host ''
    Write-Host ("$message Press any key to close...".Trim())
    try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { }
}

function Read-FscPath {
    # Nothing recorded yet and no -Fsc. The two ways in that bring a window of their own -
    # Explorer, and the button - have no way to pass one, so ask instead of turning them away.
    # A run with nothing on the other end gets $null back, and with it the throw it would
    # have had anyway.
    Write-Host 'Which FS Copilot checkout should this sync from?'
    Write-Host 'The directory holding FsCopilot.sln - drop the folder in here, or type the path.'
    while ($true) {
        try { $answer = Read-Host '  path' } catch { return $null }
        # A dropped folder arrives quoted, and sometimes with a trailing separator.
        $answer = "$answer".Trim().Trim('"', "'").TrimEnd('\', '/')
        if (-not $answer) { return $null }
        $found = $false
        try { $found = Test-Path (Join-Path $answer 'FsCopilot.sln') } catch { }
        if ($found) { return $answer }
        Write-Host '  No FsCopilot.sln in there - that is the directory to point at. Enter on its own gives up.' -ForegroundColor Yellow
    }
}

trap {
    Write-Host ''
    Write-Host "Sync failed: $_" -ForegroundColor Red
    if ($fromExplorer -or $fromButton) { Hold '' }
    exit 1
}

if (-not $Fsc) {
    $recorded = Join-Path $root 'fsc-exerciser.json'
    if (Test-Path $recorded) { $Fsc = (Get-Content $recorded -Raw | ConvertFrom-Json).FscPath }
    if (-not $Fsc) { $Fsc = Read-FscPath }
    if (-not $Fsc) { throw 'No FS Copilot checkout given - pass -Fsc <path to an FsCopilot checkout> this once.' }
}

if ($AfterPid -gt 0) {
    Write-Host "Waiting for pid $AfterPid to exit"
    try { Wait-Process -Id $AfterPid -Timeout 30 -ErrorAction Stop } catch { }
}

# Anything still running out of this repository has fsc/ or relay/ open, and a directory
# cannot be replaced while a file in it is mapped. The exerciser waits for itself above;
# these are the two it starts - FS Copilot, which is the whole point of the copy, and the
# relay, which it may not have taken with it.
$mine = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.Id -ne $PID -and $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
}
foreach ($p in $mine) {
    Write-Host "  closing $($p.ProcessName) ($($p.Id)), which is running from here"
    # Its own close path first: FS Copilot says goodbye to its panels and its peer before it
    # goes, and a killed one leaves the other pilot waiting out a five-minute outage.
    try { $null = $p.CloseMainWindow() } catch { }
}
foreach ($p in $mine) {
    if (-not $p.WaitForExit(5000)) {
        Write-Host "  $($p.ProcessName) would not close; killing it"
        # Not Kill($true): Windows PowerShell has no such overload, and the catch that used to
        # sit here hid that, so nothing was killed and the swap below failed on a locked relay/.
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        if (-not $p.WaitForExit(5000)) {
            throw "$($p.ProcessName) ($($p.Id)) is still running from here - close it, then sync again."
        }
    }
}
if ($mine) { Start-Sleep -Milliseconds 500 }

if (-not (Test-Path (Join-Path $Fsc 'FsCopilot.sln'))) {
    throw "No FsCopilot.sln in $Fsc - point -Fsc at an FS Copilot checkout."
}

$app = Join-Path $Fsc "FsCopilot\bin\$Configuration\net9.0\win-x64"
if (-not (Test-Path (Join-Path $app 'FsCopilot.dll'))) {
    throw "No FS Copilot build at $app - build FS Copilot first, then run this again."
}

# A checkout can be the right one and its build still wrong. Nothing here builds FS Copilot -
# that is the developer's own loop - so a bin/ left over from before the last few commits
# copies in looking perfectly valid. The exerciser then compiles against an API that this
# checkout's source no longer describes, and the first symptom is a missing member with
# nothing pointing back to the build that supplied it.
$builtAt = (Get-Item (Join-Path $app 'FsCopilot.dll')).LastWriteTime
$newer = Get-ChildItem $Fsc -Recurse -File -Include *.cs, *.csproj, *.axaml -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -notlike '*\bin\*' -and $_.FullName -notlike '*\obj\*' -and
        $_.FullName -notlike '*\.git\*' -and $_.LastWriteTime -gt $builtAt
    }
if ($newer) {
    $names = ($newer | Sort-Object LastWriteTime -Descending | Select-Object -First 3 |
        ForEach-Object { $_.Name }) -join ', '
    throw ("The FS Copilot build at $app is older than the checkout it sits in: " +
        "$($newer.Count) source file(s) have changed since it was built on " +
        "$($builtAt.ToString('yyyy-MM-dd HH:mm')) - most recently $names.`n`n" +
        "Build FS Copilot in $Fsc first, then run this again. Syncing it as it stands would " +
        "copy an API that this checkout's source no longer describes.")
}

function Mirror($from, $to, $drop) {
    # Staged, then swapped. A direct delete-then-copy that fails partway leaves nothing:
    # RemoveDirectoryRecursive deletes what it can reach before it throws, so one locked
    # assembly costs the whole copy. Here a failure leaves the old one untouched.
    $staged = "$to.new"
    $old = "$to.old"
    if (Test-Path $staged) { Remove-Item $staged -Recurse -Force }
    New-Item -ItemType Directory -Path $staged -Force | Out-Null
    Copy-Item "$from\*" $staged -Recurse -Force

    foreach ($d in $drop) {
        $path = Join-Path $staged $d
        if (Test-Path $path) { Remove-Item $path -Recurse -Force; Write-Host "  left behind: $d/" }
    }

    if (Test-Path $old) { Remove-Item $old -Recurse -Force }
    if (Test-Path $to) { Rename-Item $to $old }
    Rename-Item $staged (Split-Path $to -Leaf)
    Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue

    $n = (Get-ChildItem $to -Recurse -File | Measure-Object).Count
    Write-Host "  $n files -> $(Split-Path $to -Leaf)/"
}

Write-Host "Syncing FS Copilot from $Fsc"
# Community/ holds the panel package, and FS Copilot deploys whatever is in it to the
# simulator on startup. Syncing it would mean this tool decides what the sim runs, using a
# copy of a package built at some point in the past - and quietly replacing the one the
# developer just built in the Project Editor. Left behind, the app it starts finds nothing
# to deploy, says so, and the sim keeps what is already mounted.
Mirror $app (Join-Path $root 'fsc') @('Community')

# The discovery project is pinned linux-x64 self-contained, because that is what the server
# runs, and that build cannot start on this machine at all. Built for here instead; nothing
# is written into the FS Copilot checkout that a normal build there would not also write.
Write-Host 'Building the relay for this machine'
$project = Join-Path $Fsc 'FsCopilot.Discovery\FsCopilot.Discovery.csproj'
& dotnet build $project -c $Configuration -r win-x64 --self-contained false --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'The relay would not build.' }

$relay = Join-Path $Fsc "FsCopilot.Discovery\bin\$Configuration\net9.0\win-x64"
if (-not (Test-Path (Join-Path $relay 'p2p_serv.exe'))) {
    throw "The relay built but produced no p2p_serv.exe in $relay."
}
Mirror $relay (Join-Path $root 'relay') @()

$commit = $null
try { $commit = (& git -C $Fsc rev-parse --short HEAD 2>$null).Trim() } catch { }
$hash = (Get-FileHash (Join-Path $root 'fsc\FsCopilot.dll') -Algorithm SHA256).Hash.Substring(0, 16)

# What the tool compares against to know the copy has gone stale.
[ordered]@{
    FscPath = (Resolve-Path $Fsc).Path
    Commit  = $commit
    Hash    = $hash
    When    = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json | Set-Content (Join-Path $root 'fsc-exerciser.json') -Encoding utf8

# The build that has to fail when the checkout is the wrong one, and the reason the
# StampSyncedBuild target exists: Copy-Item above preserves the source's last-write time, so
# a copy from an older checkout lands looking older than the last output and MSBuild would
# otherwise skip the compiler and report success over an exerciser built for another API.
Write-Host 'Building the exerciser against the copy'
& dotnet build (Join-Path $root 'src\FsCopilot.Exerciser.csproj') -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    throw ("The copy from $Fsc is in place, but the exerciser will not build against it.`n`n" +
        "If the errors above name missing types or members, that checkout does not have the " +
        "FS Copilot work this exerciser drives. Sync one that does:`n`n" +
        "    .\sync-and-rebuild.ps1 -Fsc <checkout>`n`n" +
        "Until then fsc/ holds that copy and run.cmd will refuse to start on the same errors.")
}

Write-Host ''
Write-Host "Synced $(if ($commit) { $commit } else { '(not a git checkout)' })."

if ($Relaunch) {
    if (Test-Path $Relaunch) {
        Write-Host 'Restarting the exerciser'
        Start-Process $Relaunch
    }
    else {
        Write-Host "Nothing to restart at $Relaunch"
    }
}
else {
    Write-Host 'Now: run.cmd'
}

if ($fromExplorer) { Hold 'Done.' }
