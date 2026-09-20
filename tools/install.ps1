<#
.SYNOPSIS
    Installs tia-cli for the current user.

.DESCRIPTION
    Copies the tool to a stable folder and puts it on your PATH, then does the machine-wide part
    that decides whether tia can talk to TIA Portal at all: it writes the Openness whitelist entry
    for this exact tia.exe, adds you to the Siemens TIA Openness group if you are not in it, and
    clears a machine-wide forced-elevation flag. That part needs an administrator, so from a normal
    prompt it asks once through UAC; from an elevated prompt it just runs. It is repair.ps1 doing
    the work, which install leaves next to tia.exe for later.

    Without that step, every install and every upgrade - which changes the exe's hash - leaves tia
    unapproved, and the first background session times out against an approval window nobody sees.

    The install folder matters more than it looks. TIA Portal's Openness whitelist records the
    approved exe's path *and* a hash of its bytes, so a tool that moves between runs asks for
    approval again every time. Installing to one fixed place and leaving it there is the point.

.PARAMETER InstallDir
    Where to install. Defaults to %LOCALAPPDATA%\Programs\tia-cli. Anywhere works, and so does
    running this script from anywhere - unpack the zip where you like. What matters is that the
    installed folder stays put afterwards, since the approval is bound to the exe's path. repair.ps1
    and uninstall.ps1 are copied in beside tia.exe and act on the folder they are in, so a
    non-default install needs no arguments later either.

.PARAMETER NoPath
    Skip the PATH change. You will have to call tia.exe by its full path.

.PARAMETER NoElevate
    Skip the machine-wide step and stay inside your own profile. You then answer TIA Portal's
    approval window yourself, or run repair.ps1 later.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\tia-cli'),
    [switch]$NoPath,
    [switch]$NoElevate
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot

function Write-Step($text) { Write-Host "  $text" }
function Write-Warn($text) { Write-Host "  ! $text" -ForegroundColor Yellow }

# The user PATH must be read and written unexpanded. Reading it through the normal environment API
# expands %USERPROFILE% and friends, and writing that back would silently freeze those entries.
function Get-UserPathRaw {
    $key = Get-Item 'HKCU:\Environment'
    [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
}

function Set-UserPathRaw([string]$value) {
    $kind = if ($value -match '%') { 'ExpandString' } else { 'String' }
    Set-ItemProperty -Path 'HKCU:\Environment' -Name Path -Value $value -Type $kind
    # Without this broadcast, nothing started from Explorer sees the new PATH until you sign out.
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

Write-Host ''
Write-Host 'Installing tia-cli'
Write-Host ''

# ---------------------------------------------------------------- checks
Write-Host 'Checking this machine'

if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'Openness is 64-bit only, and this is a 32-bit Windows.'
}

$ndp = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
if (-not $ndp -or $ndp.Release -lt 528040) {
    throw '.NET Framework 4.8 is required and was not found. It ships with current Windows; install it and run this again.'
}
Write-Step '.NET Framework 4.8 present'

$openness = Get-ChildItem 'HKLM:\SOFTWARE\Siemens\Automation\Openness' -ErrorAction SilentlyContinue
if ($openness) {
    Write-Step ("TIA Portal Openness present (" + (($openness.PSChildName | Sort-Object) -join ', ') + ')')
} else {
    Write-Warn 'No TIA Portal Openness installation found. tia installs fine, but every command will'
    Write-Warn 'fail until TIA Portal is installed with the Openness option.'
}

# Membership is read at logon, so a freshly added account still fails until it signs out and back in.
$inGroup = $false
try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    foreach ($group in $identity.Groups) {
        try {
            if ($group.Translate([Security.Principal.NTAccount]).Value -match 'Siemens TIA Openness') {
                $inGroup = $true
                break
            }
        } catch { }
    }
} catch { }

if ($inGroup) {
    Write-Step 'Your account is in the Siemens TIA Openness group'
} elseif (-not $NoElevate) {
    Write-Step 'Your account is not in the Siemens TIA Openness group yet - the machine-wide step adds it'
} else {
    Write-Warn 'Your account is not in the local "Siemens TIA Openness" group, so Openness calls will'
    Write-Warn 'fail with access_denied. Add it, then sign out and back in - membership is read at logon.'
}

$exeSource = Join-Path $source 'tia.exe'
if (-not (Test-Path $exeSource)) {
    throw "tia.exe is not next to this script. Run install.ps1 from the folder you unpacked the zip into."
}

# ---------------------------------------------------------------- stop anything running
$existing = Join-Path $InstallDir 'tia.exe'
if (Test-Path $existing) {
    Write-Host ''
    Write-Host 'Replacing an existing install'
    # A running daemon holds tia.exe open and keeps a TIA Portal alive; both have to go first.
    try {
        & $existing session stop 2>&1 | Out-Null
        Write-Step 'Stopped the running session'
    } catch { }

    Get-Process -Name 'tia' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, 'OrdinalIgnoreCase') } |
        ForEach-Object {
            Write-Step "Stopping leftover process $($_.Id)"
            try { $_.Kill(); $_.WaitForExit(5000) } catch { }
        }
}

# ---------------------------------------------------------------- copy
Write-Host ''
Write-Host "Installing to $InstallDir"

if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }

$payload = Get-ChildItem -Path $source -File |
    Where-Object { $_.Name -ne 'install.ps1' }

foreach ($file in $payload) {
    Copy-Item -Path $file.FullName -Destination (Join-Path $InstallDir $file.Name) -Force
}
Write-Step "Copied $($payload.Count) files"

# A zip downloaded with a browser carries the internet zone in an alternate data stream, and every
# file unpacked from it inherits the mark. On an unsigned exe that makes ShellExecute fail with
# ERROR_CANCELLED - which is how the daemon comes to report "the operation was canceled by the user"
# about something nobody was asked. Clearing the stream does not change the file's bytes, so the
# Openness whitelist hash is unaffected.
$blocked = Get-ChildItem -Path $InstallDir -File |
    Where-Object { Get-Item $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue }

if ($blocked) {
    $blocked | Unblock-File
    Write-Step "Unblocked $(@($blocked).Count) files marked as downloaded from the internet"
}

# A RUNASADMIN layer on tia.exe turns the daemon's hidden ShellExecute into a UAC prompt with
# nowhere to appear, which Windows answers for you - the same ERROR_CANCELLED, from a different
# cause. HKCU is ours to fix; HKLM needs an administrator and is only reported.
$layers = 'HKCU:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
if (Test-Path $layers) {
    $props = Get-ItemProperty -Path $layers
    foreach ($name in $props.PSObject.Properties.Name) {
        if ($name -notlike '*\tia.exe') { continue }
        if ($props.$name -notmatch 'RUNASADMIN') { continue }
        Remove-ItemProperty -Path $layers -Name $name -ErrorAction SilentlyContinue
        Write-Step 'Cleared a forced-elevation flag on tia.exe'
    }
}

# The machine-wide step clears the HKLM one itself; this report is for when it is skipped.
$machineLayers = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
if ($NoElevate -and (Test-Path $machineLayers)) {
    $machineProps = Get-ItemProperty -Path $machineLayers
    foreach ($name in $machineProps.PSObject.Properties.Name) {
        if ($name -notlike '*\tia.exe') { continue }
        if ($machineProps.$name -notmatch 'RUNASADMIN') { continue }
        Write-Warn 'tia.exe is set to always run as administrator, machine-wide. The daemon cannot'
        Write-Warn 'start that way. Clear it from an elevated prompt:'
        Write-Warn "  Remove-ItemProperty -Path '$machineLayers' -Name '$name'"
    }
}

$exe = Join-Path $InstallDir 'tia.exe'
if (-not (Test-Path $exe)) { throw "Install failed: $exe does not exist." }

# ---------------------------------------------------------------- PATH
if (-not $NoPath) {
    $raw = Get-UserPathRaw
    $entries = $raw -split ';' | Where-Object { $_ -ne '' }
    $already = $entries | Where-Object { $_.TrimEnd('\') -ieq $InstallDir.TrimEnd('\') }

    if ($already) {
        Write-Step 'Already on your PATH'
    } else {
        $updated = (@($entries) + $InstallDir) -join ';'
        Set-UserPathRaw $updated
        Write-Step 'Added to your user PATH'
    }
}

# ---------------------------------------------------------------- machine-wide
# The Openness whitelist and the Siemens group are both machine state, so this is the one part that
# needs an administrator. repair.ps1 does it; everything is passed in explicitly because an elevated
# process may belong to a different administrator, whose LOCALAPPDATA and USERNAME are not ours.
$repair = Join-Path $InstallDir 'repair.ps1'
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$machineDone = $false
$repairCmd = "powershell -ExecutionPolicy Bypass -File `"$repair`" -AddToGroup"

if ($NoElevate) {
    # reported with the next steps below
} elseif (-not (Test-Path $repair)) {
    Write-Warn 'repair.ps1 is not in the zip, so the machine-wide step was skipped.'
} else {
    Write-Host ''
    Write-Host 'Machine-wide setup: Openness approval and group membership'

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if ($isAdmin) {
        & $repair -ExePath $exe -User $me -AddToGroup -NoNextSteps
        $machineDone = $LASTEXITCODE -eq 0
    } else {
        Write-Step 'This needs administrator rights - answer the UAC prompt.'

        function Quote($s) { "'" + ($s -replace "'", "''") + "'" }
        $log = Join-Path $env:TEMP "tia-cli-install-$PID.log"
        # The elevated window is hidden and gone when it finishes, so its output comes back through
        # a file. Write-Host is the information stream, which *>&1 catches along with the rest.
        $child = @"
try {
    & $(Quote $repair) -ExePath $(Quote $exe) -User $(Quote $me) -AddToGroup -NoNextSteps *>&1 |
        Out-File -LiteralPath $(Quote $log) -Encoding utf8
    exit `$LASTEXITCODE
} catch {
    `$_ | Out-String | Out-File -LiteralPath $(Quote $log) -Encoding utf8 -Append
    exit 1
}
"@
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($child))

        # A 32-bit PowerShell would start a 32-bit child, which writes the whitelist into the
        # Wow6432Node view where tia never looks. Sysnative exists only for 32-bit callers.
        $powershell = Join-Path $env:SystemRoot 'Sysnative\WindowsPowerShell\v1.0\powershell.exe'
        if (-not (Test-Path $powershell)) {
            $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        }

        try {
            $proc = Start-Process -FilePath $powershell -Verb RunAs -WindowStyle Hidden -Wait -PassThru `
                -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
            if (Test-Path $log) {
                Get-Content -LiteralPath $log | ForEach-Object { Write-Host "  $_" }
                Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
            }
            $machineDone = $proc.ExitCode -eq 0
        } catch {
            Write-Warn 'The UAC prompt was declined, so the machine-wide step did not run.'
        }
    }

    if (-not $machineDone) {
        Write-Warn 'Machine-wide setup did not finish. tia is installed, but may not be approved yet.'
    }
}

# ---------------------------------------------------------------- done
# ProductVersion carries a "+<commit sha>" suffix once the tree is a git repository.
$version = ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0]
Write-Host ''
Write-Host "Installed tia $version" -ForegroundColor Green
Write-Host ''
Write-Host 'Next:'
if (-not $NoPath) {
    Write-Host '  Open a new terminal (this one has the old PATH), then run: tia help'
} else {
    Write-Host "  Run: $exe help"
}
Write-Host ''

# Is this exact file already approved? Mirrors OpennessWhitelist.IsApproved: an entry matches only
# when its Path equals ours and its FileHash is the base64 SHA-256 of these bytes.
$approved = $false
try {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($exe)
    try { $hash = [Convert]::ToBase64String($sha.ComputeHash($stream)) }
    finally { $stream.Dispose(); $sha.Dispose() }

    foreach ($v in (Get-ChildItem 'HKLM:\SOFTWARE\Siemens\Automation\Openness' -ErrorAction SilentlyContinue)) {
        foreach ($entry in (Get-ChildItem (Join-Path $v.PSPath 'Whitelist\tia.exe') -ErrorAction SilentlyContinue)) {
            $props = Get-ItemProperty $entry.PSPath -ErrorAction SilentlyContinue
            if ($props.Path -ieq $exe -and $props.FileHash -ceq $hash) { $approved = $true }
        }
    }
} catch { }

if ($approved) {
    Write-Host '  TIA Portal has approved this exact file for Openness access.'
    if (-not $inGroup) {
        Write-Host ''
        Write-Host '  Sign out of Windows and back in before using tia: group membership is read at logon,' -ForegroundColor Yellow
        Write-Host '  so until then every command fails with access_denied (exit 7).' -ForegroundColor Yellow
    }
} else {
    Write-Host '  Before anything else, run one command in the foreground, against a TIA Portal you'
    Write-Host '  have open on screen:'
    Write-Host ''
    Write-Host '    tia devices --no-daemon'
    Write-Host ''
    Write-Host '  TIA Portal answers that with an "Openness access" window and waits until you say yes.'
    Write-Host '  Approve it once and it is remembered until tia.exe is replaced by a new version.'
    Write-Host ''
    Write-Host '  Do this before "tia session start". A session runs its portal in the background, so'
    Write-Host '  that window has nowhere to appear and the call times out as an access error instead.'
}
Write-Host ''
if (-not $approved) {
    Write-Host '  Or skip the window: repair.ps1 writes the approval itself (it asks for UAC):'
    Write-Host "    $repairCmd"
    Write-Host ''
}
Write-Host '  If the daemon will not start, repair.ps1 is also the place to look - it re-checks all of'
Write-Host '  the above. -DryRun reports without changing anything.'
Write-Host ''
Write-Host "  To remove: powershell -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'uninstall.ps1')`""
Write-Host ''
