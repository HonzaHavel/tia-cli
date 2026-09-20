<#
.SYNOPSIS
    Removes tia-cli.

.DESCRIPTION
    Stops any running session, takes the tool off your PATH and deletes it. Run it from the folder
    tia was installed into - install.ps1 leaves a copy of this script there - or point it somewhere
    else with -InstallDir.

    Stopping the session first is not a nicety. A background session owns the TIA Portal it started,
    and deleting the tool without stopping it leaves that portal running with nothing left to
    close it.

    -All is for a machine that has collected copies over time: it finds every install from your
    PATH, the machine PATH, the Openness whitelist, any running tia.exe and the default location,
    and clears all of it, registry included.

    A folder is only deleted outright when everything in it came from the zip. Somewhere shared -
    a drive root, your profile, Desktop - only the files tia put there are removed and the folder
    is left alone, because "the install folder" can perfectly well be C:\.

.PARAMETER InstallDir
    The folder to remove. Defaults to the folder this script is in.

.PARAMETER All
    Find and remove every install on this machine, and every Openness whitelist entry for tia.exe.
    Implies -RemoveWhitelist.

.PARAMETER KeepData
    Leave %LOCALAPPDATA%\tia-cli (the daemon state file and log) in place.

.PARAMETER RemoveWhitelist
    Also drop this install's entries from TIA Portal's Openness whitelist under HKLM. That needs
    administrator rights, so it asks for them; without it the entry is harmless and goes unused.

.PARAMETER Yes
    Do not ask for confirmation.

.PARAMETER MachineStepOnly
    Internal. Does the HKLM half of the work and nothing else - this is what the elevated helper
    runs.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File uninstall.ps1
    powershell -ExecutionPolicy Bypass -File uninstall.ps1 -All
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$All,
    [switch]$KeepData,
    [switch]$RemoveWhitelist,
    [switch]$Yes,
    [switch]$MachineStepOnly,
    [string[]]$Targets
)

$ErrorActionPreference = 'Stop'

function Write-Step($text) { Write-Host "  $text" }
function Write-Warn($text) { Write-Host "  ! $text" -ForegroundColor Yellow }

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Everything the zip installs. Anything else in the folder belongs to somebody else.
$script:OurFiles = @(
    'tia.exe', 'tia.exe.config', 'tia.pdb',
    'install.ps1', 'uninstall.ps1', 'repair.ps1',
    'INSTALL.txt', 'README.md', 'CHANGELOG.md', 'LICENSE', 'LICENSE.txt', 'LICENSE.md'
)

# The user PATH must be read and written unexpanded, or writing it back freezes %USERPROFILE% and
# friends into literal paths.
function Get-UserPathRaw {
    $key = Get-Item 'HKCU:\Environment'
    [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
}

function Set-UserPathRaw([string]$value) {
    $kind = if ($value -match '%') { 'ExpandString' } else { 'String' }
    Set-ItemProperty -Path 'HKCU:\Environment' -Name Path -Value $value -Type $kind
    if (-not ('Win32.NativeMethods' -as [type])) {
        Add-Type -Namespace Win32 -Name NativeMethods -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam,
    string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
    }
    $result = [UIntPtr]::Zero
    [void][Win32.NativeMethods]::SendMessageTimeout([IntPtr]0xFFFF, 0x1A, [UIntPtr]::Zero,
        'Environment', 2, 5000, [ref]$result)
}

$script:MachineEnv = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment'

function Get-MachinePathRaw {
    $key = Get-Item $script:MachineEnv -ErrorAction SilentlyContinue
    if (-not $key) { return '' }
    [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
}

# PATH collects junk over the years - stray quotes, entries that are not paths at all. An entry that
# will not normalize is not an install of ours, so it becomes '' and is skipped rather than throwing.
function Normalize([string]$path) {
    if (-not $path) { return '' }
    try { return ([IO.Path]::GetFullPath($path.Trim().Trim('"'))).TrimEnd('\') } catch { return '' }
}

# ------------------------------------------------------------------ whitelist / machine PATH
# Both live under HKLM, so they are the only part that needs administrator rights.

function Remove-WhitelistEntries([string[]]$dirs) {
    $root = 'HKLM:\SOFTWARE\Siemens\Automation\Openness'
    $removed = 0
    foreach ($version in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
        $entriesKey = Join-Path $version.PSPath 'Whitelist\tia.exe'
        foreach ($entry in (Get-ChildItem $entriesKey -ErrorAction SilentlyContinue)) {
            $path = (Get-ItemProperty $entry.PSPath -Name Path -ErrorAction SilentlyContinue).Path
            # No dirs means "every entry for tia.exe", which is what -All asks for. An entry whose
            # Path is unreadable is ours to drop too: it names a tia.exe and nothing else does.
            $match = (-not $dirs) -or (-not $path) -or
                     ($dirs | Where-Object { (Normalize (Split-Path -Parent $path)) -ieq $_ })
            if ($match) {
                Remove-Item $entry.PSPath -Recurse -Force -ErrorAction SilentlyContinue
                $removed++
            }
        }
    }
    Write-Step "Removed $removed Openness whitelist entr$(if ($removed -eq 1) {'y'} else {'ies'})"
}

function Remove-FromMachinePath([string[]]$dirs) {
    if (-not $dirs) { return }
    $raw = Get-MachinePathRaw
    if (-not $raw) { return }
    $entries = $raw -split ';' | Where-Object { $_ -ne '' }
    $kept = $entries | Where-Object { $d = Normalize ([Environment]::ExpandEnvironmentVariables($_)); -not ($dirs -contains $d) }
    if (@($kept).Count -ne @($entries).Count) {
        $kind = if (($kept -join ';') -match '%') { 'ExpandString' } else { 'String' }
        Set-ItemProperty -Path $script:MachineEnv -Name Path -Value ($kept -join ';') -Type $kind
        Write-Step 'Removed from the machine PATH'
    }
}

function Invoke-MachineStep([string[]]$dirs, [bool]$everything) {
    if (Test-Admin) {
        Remove-WhitelistEntries $(if ($everything) { $null } else { $dirs })
        Remove-FromMachinePath $dirs
        return
    }

    Write-Step 'The whitelist and machine PATH live under HKLM - answer the UAC prompt.'
    function Quote($s) { "'" + ($s -replace "'", "''") + "'" }
    $self = $PSCommandPath
    $log = Join-Path $env:TEMP "tia-cli-uninstall-$PID.log"

    $call = "& $(Quote $self) -MachineStepOnly"
    if ($everything) { $call += ' -All' }
    if ($dirs)       { $call += ' -Targets ' + (($dirs | ForEach-Object { Quote $_ }) -join ',') }

    $child = @"
try { $call *>&1 | Out-File -LiteralPath $(Quote $log) -Encoding utf8; exit `$LASTEXITCODE }
catch { `$_ | Out-String | Out-File -LiteralPath $(Quote $log) -Encoding utf8 -Append; exit 1 }
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($child))
    $powershell = Join-Path $env:SystemRoot 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path $powershell)) {
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    }

    try {
        Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Hidden -Wait `
            -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) | Out-Null
        if (Test-Path $log) {
            Get-Content -LiteralPath $log | ForEach-Object { Write-Host "  $_" }
            Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Warn 'The UAC prompt was declined. The whitelist entries are still there - harmless,'
        Write-Warn 'since the exe they name is gone, but an elevated -RemoveWhitelist run clears them.'
    }
}

# The elevated helper does its half and stops.
if ($MachineStepOnly) {
    if (-not (Test-Admin)) { Write-Warn 'Not elevated.'; exit 1 }
    Remove-WhitelistEntries $(if ($All) { $null } else { $Targets })
    Remove-FromMachinePath $Targets
    exit 0
}

# ------------------------------------------------------------------ what to remove
# $PSScriptRoot is empty inside a param() default under Windows PowerShell 5.1, so this lives here.
$here = $PSScriptRoot
if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Path }

function Find-Installs {
    $found = @()
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\tia-cli')
        $here
    )

    # PATH entries, both scopes: an install that is on PATH is one somebody meant to use.
    foreach ($raw in @((Get-UserPathRaw), (Get-MachinePathRaw))) {
        foreach ($entry in ($raw -split ';' | Where-Object { $_ -ne '' })) {
            $candidates += [Environment]::ExpandEnvironmentVariables($entry)
        }
    }

    # Whitelist entries record the exe's full path, which is how a forgotten install is found.
    $root = 'HKLM:\SOFTWARE\Siemens\Automation\Openness'
    foreach ($version in (Get-ChildItem $root -ErrorAction SilentlyContinue)) {
        $entriesKey = Join-Path $version.PSPath 'Whitelist\tia.exe'
        foreach ($entry in (Get-ChildItem $entriesKey -ErrorAction SilentlyContinue)) {
            $path = (Get-ItemProperty $entry.PSPath -Name Path -ErrorAction SilentlyContinue).Path
            if ($path) { $candidates += (Split-Path -Parent $path) }
        }
    }

    foreach ($proc in (Get-Process -Name 'tia' -ErrorAction SilentlyContinue)) {
        if ($proc.Path) { $candidates += (Split-Path -Parent $proc.Path) }
    }

    $seen = @()
    foreach ($c in $candidates) {
        if (-not $c) { continue }
        $n = Normalize $c
        if (-not $n -or ($seen -contains $n)) { continue }
        $seen += $n
        try { if (Test-Path -LiteralPath (Join-Path $n 'tia.exe')) { $found += $n } } catch { }
    }
    return $found
}

if ($All) {
    $RemoveWhitelist = $true
    $dirs = @(Find-Installs)
} else {
    if (-not $InstallDir) { $InstallDir = $here }
    if (-not $InstallDir) { throw 'Could not work out which folder to remove. Pass -InstallDir.' }
    $dirs = @(Normalize $InstallDir) | Where-Object { $_ }
    if (-not $dirs) { throw "Not a usable folder path: $InstallDir" }
}

# ------------------------------------------------------------------ removing one folder

# A folder is ours to delete only when nothing else lives in it. "The install folder" can be C:\,
# a profile root or a Desktop - somewhere a recursive delete would take the rest of the machine
# with it - so those keep their folder and lose only the files tia put there.
function Get-FolderKind([string]$dir) {
    $full = Normalize $dir
    $root = (Normalize ([IO.Path]::GetPathRoot($full + '\')))
    if (-not $full -or $full -ieq $root) { return 'shared' }

    $protected = @(
        $env:SystemRoot, (Join-Path $env:SystemRoot 'System32'), $env:ProgramFiles,
        ${env:ProgramFiles(x86)}, $env:ProgramData, $env:USERPROFILE, $env:APPDATA,
        $env:LOCALAPPDATA, (Join-Path $env:LOCALAPPDATA 'Programs'), (Split-Path -Parent $env:USERPROFILE),
        (Join-Path $env:USERPROFILE 'Desktop'), (Join-Path $env:USERPROFILE 'Documents'),
        (Join-Path $env:USERPROFILE 'Downloads')
    ) | Where-Object { $_ } | ForEach-Object { Normalize $_ }

    if ($protected -contains $full) { return 'shared' }
    if (Get-ChildItem -LiteralPath $full -Directory -ErrorAction SilentlyContinue) { return 'shared' }

    $strangers = Get-ChildItem -LiteralPath $full -File -Force -ErrorAction SilentlyContinue |
        Where-Object { ($script:OurFiles -notcontains $_.Name) -and ($_.Extension -ine '.dll') }

    if ($strangers) { return 'shared' }
    return 'exclusive'
}

# Whatever this script is running from outlives it, so deleting it is handed to a detached process
# that starts once we have exited. It retries: a virus scanner or the search indexer can hold a
# freshly written file or folder for several seconds.
function Remove-Later([string]$path) {
    $literal = $path -replace "'", "''"
    $cleanup = @"
for (`$i = 0; `$i -lt 30; `$i++) {
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath '$literal' -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath '$literal')) { break }
}
"@
    # EncodedCommand sidesteps every layer of command-line quoting for the path.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($cleanup))
    Start-Process -WindowStyle Hidden -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
}

function Remove-InstallAt([string]$dir) {
    $exe = Join-Path $dir 'tia.exe'
    Write-Host ''
    Write-Host $dir

    if (Test-Path -LiteralPath $exe) {
        try {
            $status = & $exe session stop 2>&1 | Out-String
            if ($status -match 'Session stopped') {
                Write-Step 'Stopped the running session'
                if ($status -match 'closing') { Write-Step 'Its TIA Portal was closed with it' }
            } else {
                Write-Step 'No session was running'
            }
        } catch {
            Write-Warn "Could not ask tia to stop its session: $($_.Exception.Message)"
        }
    }

    Get-Process -Name 'tia' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and (Normalize (Split-Path -Parent $_.Path)) -ieq $dir } |
        ForEach-Object {
            Write-Step "Stopping leftover process $($_.Id)"
            try { $_.Kill(); $_.WaitForExit(5000) } catch { }
        }

    # $PSCommandPath, not $MyInvocation - inside a function the latter describes the call, not the
    # script, and deleting the file we are running from would end the run mid-way.
    $self = $PSCommandPath
    $kind = Get-FolderKind $dir
    $deferred = $false

    if ($kind -eq 'exclusive') {
        foreach ($file in (Get-ChildItem -LiteralPath $dir -File -Force -ErrorAction SilentlyContinue)) {
            if ($self -and $file.FullName -ieq $self) { $deferred = $true; continue }
            try { Remove-Item -LiteralPath $file.FullName -Force } catch { Write-Warn "Could not delete $($file.Name)" }
        }
        Write-Step 'Removed the installed files'
    } else {
        # Shared with something else, so only what tia put here goes, by name.
        $removed = 0
        foreach ($name in $script:OurFiles) {
            $path = Join-Path $dir $name
            if (-not (Test-Path -LiteralPath $path)) { continue }
            if ($self -and $path -ieq $self) { $deferred = $true; continue }
            try { Remove-Item -LiteralPath $path -Force; $removed++ } catch { Write-Warn "Could not delete $name" }
        }
        if ($deferred) {
            # Only this script is left, and it cannot delete itself while it is running.
            Remove-Later $self
            $removed++
        }
        Write-Step "Removed $removed file$(if ($removed -eq 1) {''} else {'s'}); the folder is shared, so it stays"
        $leftovers = Get-ChildItem -LiteralPath $dir -File -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Extension -ieq '.dll' }
        if ($leftovers) {
            Write-Warn "Left $(@($leftovers).Count) .dll file(s) behind - in a shared folder they may not be ours:"
            $leftovers | ForEach-Object { Write-Warn "    $($_.FullName)" }
        }
        return
    }

    if ($deferred) {
        Remove-Later $dir
        Write-Step 'The empty folder is removed a moment from now'
    } else {
        try {
            Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
            Write-Step 'Removed the install folder'
        } catch { }
    }
}

# ------------------------------------------------------------------ confirm
# Only now, because saying what will happen to a folder means having looked inside it first.
Write-Host ''
$everything = $All.IsPresent

if ($All) {
    Write-Host 'Removing every tia-cli on this machine'
    Write-Host ''
    if (-not $dirs) { Write-Step 'No installed tia.exe found - clearing the leftovers anyway.' }

    $confirmed = @()
    foreach ($d in $dirs) {
        $fate = if ((Get-FolderKind $d) -eq 'exclusive') { 'the whole folder goes' }
                else { "only tia's own files go - the folder holds other things too" }
        Write-Host "  $d"
        Write-Host "      $fate" -ForegroundColor DarkGray
        # A build tree is somebody's working copy, not an install, and it comes back with a rebuild.
        if ($d -match '\\(bin|obj|dist)(\\|$)') {
            Write-Warn '    this looks like a build output folder, not an install'
        }

        if ($Yes) { $confirmed += $d; continue }
        $answer = Read-Host '      Remove it? [y/N]'
        if ($answer -match '^(y|yes)$') { $confirmed += $d } else { Write-Host '      skipped'; $everything = $false }
    }
    $dirs = @($confirmed)
} else {
    Write-Host "Removing tia-cli from $($dirs[0])"
    if (-not (Test-Path -LiteralPath (Join-Path $dirs[0] 'tia.exe'))) {
        Write-Host ''
        Write-Warn "No tia.exe in $($dirs[0]) - nothing to remove there."
        Write-Warn 'Pass -InstallDir if tia is installed somewhere else, or -All to find every copy.'
        if (-not $Yes) { return }
    }
    if ((Get-FolderKind $dirs[0]) -ne 'exclusive') {
        Write-Warn 'That folder holds things that did not come from the zip, so only the files tia'
        Write-Warn 'put there are removed and the folder itself stays.'
    }
    Write-Host ''
    if (-not $Yes) {
        $answer = Read-Host 'This stops any running session and deletes the tool. Continue? [y/N]'
        if ($answer -notmatch '^(y|yes)$') {
            Write-Host 'Nothing was changed.'
            return
        }
    }
}

foreach ($dir in $dirs) { Remove-InstallAt $dir }

# ------------------------------------------------------------------ PATH
Write-Host ''
$raw = Get-UserPathRaw
$entries = $raw -split ';' | Where-Object { $_ -ne '' }
$kept = $entries | Where-Object {
    $expanded = Normalize ([Environment]::ExpandEnvironmentVariables($_))
    -not ($dirs -contains $expanded)
}

if (@($kept).Count -ne @($entries).Count) {
    Set-UserPathRaw ($kept -join ';')
    Write-Step 'Removed from your user PATH'
} else {
    Write-Step 'Was not on your user PATH'
}

# ------------------------------------------------------------------ data
if ($KeepData) {
    Write-Step 'Kept the daemon state file and log'
} else {
    $data = Join-Path $env:LOCALAPPDATA 'tia-cli'
    if (Test-Path $data) {
        try {
            Remove-Item -Path $data -Recurse -Force
            Write-Step 'Removed the daemon state file and log'
        } catch {
            Write-Warn "Could not remove $data - $($_.Exception.Message)"
        }
    }
}

# ------------------------------------------------------------------ HKLM
# Every whitelist entry goes only when every install did: one you kept should keep its approval.
if ($RemoveWhitelist) { Invoke-MachineStep $dirs $everything }

Write-Host ''
Write-Host 'tia-cli removed.' -ForegroundColor Green
if (-not $RemoveWhitelist) {
    Write-Host ''
    Write-Host '  TIA Portal still has this build in its Openness whitelist under HKLM. It is harmless'
    Write-Host '  and unused; -RemoveWhitelist clears it, and -All clears every copy on the machine.'
}
Write-Host ''
