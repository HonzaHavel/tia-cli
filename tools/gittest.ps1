<#
.SYNOPSIS
    Tests the git workflow that would replace VCI: the PLC program lives in a repository as text,
    is edited outside TIA Portal, and goes back into the project only to compile.

.DESCRIPTION
    The loop under test is the one a VS Code user would run:

      export   every block to program/<folder>/<name>.scl|.db|.awl (or .xml when it is LAD/FBD),
               every tag table to tags/<table>.json, and commit that as the baseline
      edit     change files in the working tree, the way an editor would
      sync     import only what git says changed, then compile
      commit   only when the compile is clean - a broken edit must never reach history

    and then checks the things that decide whether that loop is usable day to day:

      - exporting twice gives identical files, so diffs show edits and not noise
      - what TIA writes back after an import matches what was edited
      - broken SCL is caught by the sync, and nothing is committed
      - a fresh project rebuilt from nothing but the repository exports to the same files

    Like fieldtest.ps1 it builds a throwaway project in a temp folder, runs headless, and deletes
    everything afterwards. Nothing touches a PLC, and the repository is local - nothing is pushed.

    Needs git on PATH as well as an installed tia.exe.

.PARAMETER ExePath
    Installed tia.exe. Defaults to the install.ps1 location.

.PARAMETER ProjectDir
    Where the throwaway projects and the repository are made. Defaults to a temp folder.

.PARAMETER KeepProject
    Keep the projects and the repository to look at afterwards.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File gittest.ps1
    powershell -ExecutionPolicy Bypass -File gittest.ps1 -KeepProject
#>

[CmdletBinding()]
param(
    [string] $ExePath,
    [string] $ProjectDir,
    [string] $ReportDir,
    [string] $OpennessVersion,
    [switch] $KeepProject
)

$ErrorActionPreference = 'Stop'

if (-not $ExePath)   { $ExePath   = Join-Path $env:LOCALAPPDATA 'Programs\tia-cli\tia.exe' }
if (-not $ReportDir) { $ReportDir = Join-Path ([Environment]::GetFolderPath('Desktop')) 'tia-cli-fieldtest' }
if (-not $ProjectDir) {
    $ProjectDir = Join-Path ([IO.Path]::GetTempPath()) ('tia-gittest-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}

$RepoDir    = Join-Path $ProjectDir 'repo'
$RebuildDir = Join-Path $ProjectDir 'rebuild'

# TIA Portal refuses a project folder longer than 143 characters; the rebuild's is the deepest.
$deepest = Join-Path $RebuildDir 'GitRebuild'
if ($deepest.Length -gt 143) {
    throw "The rebuilt project would end up at $deepest ($($deepest.Length) characters), and TIA Portal allows at most 143. Pass a shorter -ProjectDir, such as C:\tia-git."
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "tia.exe not found at $ExePath. Install it first, or pass -ExePath."
}
$ExePath = (Get-Item -LiteralPath $ExePath).FullName

$gitCommand = Get-Command git -ErrorAction SilentlyContinue
if (-not $gitCommand) {
    throw 'git is not on PATH. Install Git for Windows (https://git-scm.com/download/win) - the portable one is fine - then open a new shell and run this again.'
}
$Git = $gitCommand.Source

# The confidential-configuration password of the throwaway project. A current CPU will not compile
# without one. It protects nothing and exists nowhere else.
$Secret = 'GitTest_1234!'
$Cpu    = 'OrderNumber:6ES7 214-1BG40-0XB0/V4.6'

# Languages TIA can write out as text. Everything else - LAD, FBD, GRAPH - goes as Openness XML.
$TextLanguages = @('SCL', 'STL', 'DB', 'F_SCL', 'F_STL', 'F_DB')

$script:Results = New-Object Collections.ArrayList
$script:Env     = [ordered]@{}

function Head ($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Note ($t) { Write-Host "   $t" -ForegroundColor DarkGray }

function Record ($Name, $Command, $Expected, $Exit, $Verdict, $Seconds, $Output, $Note) {
    [void]$script:Results.Add([pscustomobject]@{
        Name = $Name; Command = $Command; Expected = $Expected; Exit = $Exit; Verdict = $Verdict
        Seconds = $Seconds; Output = $Output; Note = $Note
    })
    $colour = switch ($Verdict) { 'pass' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    $exitText = if ($null -ne $Exit) { "exit $Exit, " } else { '' }
    Write-Host ("   [{0}] {1}  ({2}{3}s)" -f $Verdict, $Name, $exitText, $Seconds) -ForegroundColor $colour
}

<#  Runs one tia or git command and records it. -AnyExit records the exit code without judging it,
    for the steps whose failure is the point. $script:LastStdout holds stdout alone, since tia
    writes warnings to stderr and those would break the JSON the export steps parse.  #>
function Case {
    param(
        [string]   $Name,
        [string[]] $TiaArgs,
        [int[]]    $Expect = @(0),
        [string]   $Note,
        [ValidateSet('tia', 'git')] [string] $Tool = 'tia',
        [switch]   $AnyExit
    )

    if ($Tool -eq 'git') {
        $exe  = $Git
        # Bytes stay exactly as TIA wrote them: no line-ending conversion, and an identity of its own
        # so the run needs nothing from the machine's git config.
        $argv = @('-C', $RepoDir, '-c', 'core.autocrlf=false', '-c', 'core.safecrlf=false',
                  '-c', 'user.name=tia gittest', '-c', 'user.email=gittest@localhost') + $TiaArgs
        $shown = 'git ' + ($TiaArgs -join ' ')
    } else {
        $exe  = $ExePath
        $argv = $TiaArgs
        if ($OpennessVersion -and -not $script:InSession) { $argv = @($argv) + @('--openness-version', $OpennessVersion) }
        $shown = 'tia ' + ($TiaArgs -join ' ')
    }

    $started  = Get-Date
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $lines = @(& $exe @argv 2>&1)
        $code  = $LASTEXITCODE
        $script:LastStdout = ($lines | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | Out-String)
        $output = ($lines | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { $_ }
        } | Out-String)
    } catch {
        $output = $_.Exception.Message
        $script:LastStdout = ''
        $code = -1
    } finally {
        $ErrorActionPreference = $previous
    }
    $elapsed = [Math]::Round(((Get-Date) - $started).TotalSeconds, 1)

    $pass = $AnyExit -or ($Expect -contains $code)
    if ($pass -and $Tool -eq 'tia' -and $TiaArgs[0] -eq 'session') {
        if ($TiaArgs[1] -eq 'start') { $script:InSession = $true }
        if ($TiaArgs[1] -eq 'stop')  { $script:InSession = $false }
    }

    $verdict  = if ($AnyExit) { 'info' } elseif ($pass) { 'pass' } else { 'FAIL' }
    $expected = if ($AnyExit) { 'any' } else { $Expect -join ' or ' }
    Record $Name $shown $expected $code $verdict $elapsed $output.TrimEnd() $Note

    $script:LastExit   = $code
    $script:LastPassed = ($code -eq 0) -or (-not $AnyExit -and $pass)
}

# A verdict the script reaches itself rather than one a command returned.
function Check {
    param([string]$Name, [bool]$Ok, [string]$Detail, [string]$Note)
    Record $Name '' '' $null $(if ($Ok) { 'pass' } else { 'FAIL' }) 0 $Detail $Note
    $script:LastPassed = $Ok
}

function Skip ([string]$Name, [string]$Why) { Record $Name '' '' $null 'skipped' 0 '' $Why }

function Parse-Json ([string]$Text) {
    if (-not $Text.Trim()) { return @() }
    # Piped so an array comes out as its items: ConvertFrom-Json in 5.1 emits it as one object.
    return @(ConvertFrom-Json $Text | ForEach-Object { $_ })
}

# 'Program blocks/Drives' -> 'Drives'; the root itself -> ''.
function Repo-Folder ([string]$BlockPath) {
    $segments = @($BlockPath -split '/' | Where-Object { $_ })
    if ($segments.Count -and $segments[0] -eq 'Program blocks') { $segments = @($segments | Select-Object -Skip 1) }
    return ($segments -join '/')
}

function Write-Text ([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($false)))
}

<#  Replaces text in a file the way an editor saving it would: same encoding, BOM or not, and the
    line endings it already had.  #>
function Edit-File ([string]$Path, [string]$Find, [string]$Replace) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $bom   = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text  = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Find)) { return $false }
    [IO.File]::WriteAllText($Path, $text.Replace($Find, $Replace), (New-Object Text.UTF8Encoding($bom)))
    return $true
}

<#  Writes the whole program and every tag table into the repository, replacing what is there, so
    a block that has gone from the project shows up in git as a deleted file.  #>
function Export-Program ([string]$Label) {
    $program = Join-Path $RepoDir 'program'
    $tags    = Join-Path $RepoDir 'tags'
    foreach ($dir in @($program, $tags)) {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    $ok = $true
    Case -Name "$Label - list blocks" -TiaArgs @('blocks', 'PLC_1', '--json')
    if (-not $script:LastPassed) { return $false }

    foreach ($b in (Parse-Json $script:LastStdout)) {
        $folder = Repo-Folder $b.path
        $dir    = if ($folder) { Join-Path $program ($folder -replace '/', '\') } else { $program }
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $ref    = if ($folder) { "$folder/$($b.name)" } else { $b.name }

        if ($b.isKnowHowProtected) {
            Skip "$Label - $ref" 'know-how protected: TIA exports nothing for it'
            continue
        }

        if ($TextLanguages -contains $b.language) {
            $ext = if ($b.language -match 'DB') { '.db' } elseif ($b.language -match 'STL') { '.awl' } else { '.scl' }
            Case -Name "$Label - $ref as text" -TiaArgs @('block', 'source', 'PLC_1', $ref, '--out', (Join-Path $dir ($b.name + $ext)))
        } else {
            Case -Name "$Label - $ref as XML ($($b.language))" -TiaArgs @('block', 'export', 'PLC_1', $ref, '--out', (Join-Path $dir ($b.name + '.xml')))
        }
        if (-not $script:LastPassed) { $ok = $false }
    }

    Case -Name "$Label - list tag tables" -TiaArgs @('tables', 'PLC_1', '--json')
    if (-not $script:LastPassed) { return $false }
    foreach ($t in (Parse-Json $script:LastStdout)) {
        Case -Name "$Label - tags of '$($t.name)'" -TiaArgs @('tags', 'PLC_1', '--table', $t.name, '--json')
        if ($script:LastPassed) {
            Write-Text (Join-Path $tags ($t.name + '.json')) ($script:LastStdout.TrimEnd() + "`n")
        } else { $ok = $false }
    }
    return $ok
}

# What git says changed, as @{ State; Path } with repository-relative paths. Callers wrap it in
# @(): a single change would otherwise come back as a bare hashtable, whose Count is its key count.
function Get-Changes ([string]$Label, [string[]]$Paths = @('program')) {
    Case -Name "$Label - git status" -Tool git -TiaArgs (@('status', '--porcelain', '--untracked-files=all', '--') + $Paths)
    $changes = @()
    foreach ($line in ($script:LastStdout -split "`r?`n" | Where-Object { $_.Trim() })) {
        $changes += @{ State = $line.Substring(0, 2).Trim(); Path = $line.Substring(3).Trim('"') }
    }
    return $changes
}

<#  The step a CD pipeline would run: push the changed files into the project and compile. Text
    goes in through 'source add', which replaces a source of the same name and regenerates its
    blocks; XML through 'block import --overwrite'; a deleted file deletes its block. Returns
    true only when every step and the compile exited 0.  #>
function Sync-Changes ([string]$Label, $Changes, [switch]$ExpectBroken, [switch]$NoCompile) {
    $ok = $true
    foreach ($c in $Changes) {
        $inProgram = $c.Path -replace '^program/', ''
        $folder    = if ($inProgram.Contains('/')) { $inProgram.Substring(0, $inProgram.LastIndexOf('/')) } else { '' }
        $file      = Split-Path $inProgram -Leaf
        $name      = [IO.Path]::GetFileNameWithoutExtension($file)
        $ext       = [IO.Path]::GetExtension($file).ToLowerInvariant()
        $full      = Join-Path $RepoDir ($c.Path -replace '/', '\')
        $ref       = if ($folder) { "$folder/$name" } else { $name }

        if ($c.State -eq 'D') {
            $tiaArgs = @('block', 'delete', 'PLC_1', $ref, '--force')
        } elseif ($ext -eq '.xml') {
            $tiaArgs = @('block', 'import', 'PLC_1', $full, '--overwrite')
            if ($folder) { $tiaArgs += @('--folder', $folder) }
        } else {
            $tiaArgs = @('source', 'add', 'PLC_1', $file, '--file', $full)
            if ($folder) { $tiaArgs += @('--folder', $folder) }
        }
        Case -Name "$Label - $($c.State) $($c.Path)" -TiaArgs $tiaArgs -AnyExit:$ExpectBroken
        if ($script:LastExit -ne 0) { $ok = $false }
    }
    if ($NoCompile) { return $ok }
    Case -Name "$Label - compile" -TiaArgs @('compile', 'PLC_1') -AnyExit:$ExpectBroken
    return ($ok -and $script:LastExit -eq 0)
}

# ---------------------------------------------------------------- environment
Head 'Environment'

$script:Env['when']     = (Get-Date).ToString('s')
$script:Env['machine']  = $env:COMPUTERNAME
$script:Env['exe']      = $ExePath
$script:Env['exeBuilt'] = (Get-Item $ExePath).LastWriteTime.ToString('s')
$script:Env['tia']      = (& $ExePath --version 2>&1 | Out-String).Trim()
$script:Env['git']      = (& $Git --version 2>&1 | Out-String).Trim()
foreach ($k in $script:Env.Keys) { Note ("{0,-10} {1}" -f $k, $script:Env[$k]) }

$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try { $sessionState = (& $ExePath session list 2>&1 | Out-String) }
finally { $ErrorActionPreference = $previous }
if ($sessionState -notmatch 'No tia session is running') {
    throw "A tia session is already running. This test needs one of its own. Save anything open in it, run 'tia session stop', then run this again."
}

New-Item -ItemType Directory -Path $RepoDir -Force | Out-Null

# ---------------------------------------------------------------- seed project
$sessionArgs = @('session', 'start', '--new', '--headless')
if ($OpennessVersion) { $sessionArgs += @('--openness-version', $OpennessVersion) }

Head 'Project to start from'
Case -Name 'session start' -TiaArgs $sessionArgs

try {
    if (-not $script:LastPassed) { throw 'stop' }

    Case -Name 'no project already open' -TiaArgs @('project', 'info') -Expect @(4)
    if (-not $script:LastPassed) { throw 'stop' }
    Case -Name 'project new'       -TiaArgs @('project', 'new', $ProjectDir, 'GitTest')
    if (-not $script:LastPassed) { throw 'stop' }
    Case -Name 'device add'        -TiaArgs @('device', 'add', $Cpu, 'PLC_1')
    if (-not $script:LastPassed) { throw 'stop' }
    Case -Name 'device protection' -TiaArgs @('device', 'protection', 'PLC_1', '--level', 'FullAccess', '--secret', $Secret)

    Case -Name 'table add' -TiaArgs @('table', 'add', 'PLC_1', 'IO')
    Case -Name 'tag add'   -TiaArgs @('tag', 'add', 'PLC_1', 'MotorRun', '--table', 'IO', '--type', 'Bool', '--address', '%Q0.0')
    Case -Name 'tag add without address' -TiaArgs @('tag', 'add', 'PLC_1', 'Spare', '--table', 'IO', '--type', 'Int')

    # Motor calls Twice and sits in a folder, so the rebuild has to get both the folder and the
    # order right. Settings is a global DB, the third kind of text file. Main stays the LAD OB1
    # every new project has, which is the XML path.
    $seed = Join-Path $ProjectDir 'seed'
    New-Item -ItemType Directory -Path $seed -Force | Out-Null
    Write-Text (Join-Path $seed 'Twice.scl') @'
FUNCTION "Twice" : Int
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      value : Int;
   END_VAR

BEGIN
   #Twice := #value * 2;
END_FUNCTION
'@
    Write-Text (Join-Path $seed 'Motor.scl') @'
FUNCTION_BLOCK "Motor"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
   VAR_INPUT
      start : Bool;
      stop  : Bool;
   END_VAR
   VAR_OUTPUT
      running : Bool;
      doubled : Int;
   END_VAR

BEGIN
   IF #stop THEN
      #running := FALSE;
   ELSIF #start THEN
      #running := TRUE;
   END_IF;
   #doubled := "Twice"(value := 21);
END_FUNCTION_BLOCK
'@
    Write-Text (Join-Path $seed 'Settings.db') @'
DATA_BLOCK "Settings"
{ S7_Optimized_Access := 'TRUE' }
VERSION : 0.1
NON_RETAIN
   VAR
      speed : Int := 100;
   END_VAR

BEGIN
END_DATA_BLOCK
'@

    Case -Name 'seed Twice'    -TiaArgs @('source', 'add', 'PLC_1', 'Twice.scl', '--file', (Join-Path $seed 'Twice.scl'))
    Case -Name 'seed Motor'    -TiaArgs @('source', 'add', 'PLC_1', 'Motor.scl', '--file', (Join-Path $seed 'Motor.scl'), '--folder', 'Drives')
    Case -Name 'seed Settings' -TiaArgs @('source', 'add', 'PLC_1', 'Settings.db', '--file', (Join-Path $seed 'Settings.db'))
    Case -Name 'seed compiles' -TiaArgs @('compile', 'PLC_1')
    if (-not $script:LastPassed) { throw 'stop' }
    Case -Name 'project save'  -TiaArgs @('project', 'save')

    # ------------------------------------------------------------ baseline
    Head 'Export and commit the baseline'
    Case -Name 'git init' -Tool git -TiaArgs @('init')
    $exported = Export-Program 'export'
    Check -Name 'everything exported' -Ok $exported
    Case -Name 'files in the repository' -Tool git -TiaArgs @('status', '--porcelain', '--untracked-files=all') -AnyExit
    Case -Name 'git add'     -Tool git -TiaArgs @('add', '-A')
    Case -Name 'commit baseline' -Tool git -TiaArgs @('commit', '-q', '-m', 'baseline exported from TIA Portal')
    if (-not $script:LastPassed) { throw 'stop' }

    # Export again, change nothing: if anything moved, every commit would carry noise.
    Export-Program 'export again' | Out-Null
    $noise = @(Get-Changes 'export again' @('program', 'tags'))
    Case -Name 'export again - diff' -Tool git -TiaArgs @('diff', '--stat') -AnyExit
    Check -Name 'exporting twice changes nothing' -Ok ($noise.Count -eq 0) `
        -Detail (($noise | ForEach-Object { "$($_.State) $($_.Path)" }) -join "`n") `
        -Note 'A file listed here changes on every export, so every commit would show it as edited.'
    Case -Name 'reset after noise check' -Tool git -TiaArgs @('checkout', '-q', '--', '.')

    # ------------------------------------------------------------ edit, sync, commit
    Head 'Edit outside TIA, sync, compile, commit'
    $motor    = Join-Path $RepoDir 'program\Drives\Motor.scl'
    $settings = Join-Path $RepoDir 'program\Settings.db'

    $editedMotor = (Test-Path $motor) -and (Edit-File $motor '#running := TRUE;' '#running := NOT #stop;')
    Check -Name 'edit Motor.scl' -Ok $editedMotor -Detail $(if (Test-Path $motor) { Get-Content $motor -Raw } else { "$motor was not exported" }) `
        -Note 'Replaces one statement. The file content is shown when the statement could not be found.'
    # TIA writes a DB's start values as assignments after BEGIN, not in the declaration it was given.
    $editedSettings = (Test-Path $settings) -and (Edit-File $settings 'speed := 100;' 'speed := 150;')
    Check -Name 'edit Settings.db' -Ok $editedSettings -Detail $(if (Test-Path $settings) { Get-Content $settings -Raw } else { "$settings was not exported" })

    $changes = @(Get-Changes 'after edit')
    Check -Name 'git sees exactly the two edits' -Ok ($changes.Count -eq 2) -Detail (($changes | ForEach-Object { "$($_.State) $($_.Path)" }) -join "`n")
    Case -Name 'the diff a reviewer would see' -Tool git -TiaArgs @('diff') -AnyExit

    $synced = Sync-Changes 'sync' $changes
    Check -Name 'sync and compile succeeded' -Ok $synced
    if ($synced) {
        Case -Name 'git add edit'   -Tool git -TiaArgs @('add', '-A')
        Case -Name 'commit the edit' -Tool git -TiaArgs @('commit', '-q', '-m', 'Motor stops on stop; speed 150')
    } else {
        Skip 'commit the edit' 'the sync or compile failed, so the edit stays uncommitted - which is the rule'
    }

    # What TIA holds now, written back out, should be byte for byte what was edited. Anything else
    # means TIA reformats code on the way in, and the next export would show it as a change.
    Export-Program 'export after sync' | Out-Null
    $rewritten = @(Get-Changes 'export after sync' @('program', 'tags'))
    Case -Name 'export after sync - diff' -Tool git -TiaArgs @('diff') -AnyExit
    Check -Name 'TIA gives back what was edited' -Ok ($rewritten.Count -eq 0) `
        -Detail (($rewritten | ForEach-Object { "$($_.State) $($_.Path)" }) -join "`n") `
        -Note 'Listed files came back from TIA different from how they went in; the diff above shows how.'
    Case -Name 'reset after round-trip check' -Tool git -TiaArgs @('checkout', '-q', '--', '.')

    # ------------------------------------------------------------ broken edit
    Head 'A broken edit is caught before commit'
    $head = (& $Git -C $RepoDir rev-parse HEAD 2>$null | Out-String).Trim()
    $twice = Join-Path $RepoDir 'program\Twice.scl'
    $broke = (Test-Path $twice) -and (Edit-File $twice '#value * 2;' '#value * ;')
    Check -Name 'break Twice.scl' -Ok $broke -Detail $(if (Test-Path $twice) { Get-Content $twice -Raw } else { "$twice was not exported" })

    if ($broke) {
        $brokenSynced = Sync-Changes 'broken sync' @(Get-Changes 'broken edit') -ExpectBroken
        Check -Name 'the broken edit fails the sync' -Ok (-not $brokenSynced) `
            -Note 'source add or compile has to exit non-zero, or a pipeline would commit broken code. Their output above is what a developer gets to see.'
        $headAfter = (& $Git -C $RepoDir rev-parse HEAD 2>$null | Out-String).Trim()
        Check -Name 'nothing was committed' -Ok ($head -eq $headAfter)

        # Put the good version back, in git and in the project.
        Case -Name 'git checkout Twice.scl' -Tool git -TiaArgs @('checkout', '-q', '--', 'program/Twice.scl')
        $restored = Sync-Changes 'restore' @(@{ State = 'M'; Path = 'program/Twice.scl' })
        Check -Name 'project is healthy again' -Ok $restored
    }
    Case -Name 'project save after edits' -TiaArgs @('project', 'save')

    # ------------------------------------------------------------ rebuild
    Head 'Rebuild a project from the repository alone'
    Case -Name 'project close' -TiaArgs @('project', 'close')
    Case -Name 'project new (rebuild)' -TiaArgs @('project', 'new', $RebuildDir, 'GitRebuild')
    if (-not $script:LastPassed) { throw 'stop' }

    # The hardware is not in the repository - that stays in the project or its archive - so the
    # rebuild makes the same station by hand.
    Case -Name 'rebuild - device add'        -TiaArgs @('device', 'add', $Cpu, 'PLC_1')
    Case -Name 'rebuild - device protection' -TiaArgs @('device', 'protection', 'PLC_1', '--level', 'FullAccess', '--secret', $Secret)

    Case -Name 'rebuild - existing tables' -TiaArgs @('tables', 'PLC_1', '--json')
    $existing = @(Parse-Json $script:LastStdout | ForEach-Object { $_.name })
    foreach ($file in (Get-ChildItem (Join-Path $RepoDir 'tags') -Filter '*.json' | Sort-Object Name)) {
        $table = $file.BaseName
        if ($existing -notcontains $table) { Case -Name "rebuild - table $table" -TiaArgs @('table', 'add', 'PLC_1', $table) }
        foreach ($tag in (Parse-Json (Get-Content $file.FullName -Raw))) {
            $tagArgs = @('tag', 'add', 'PLC_1', $tag.name, '--table', $table, '--type', $tag.dataType)
            if ($tag.logicalAddress) { $tagArgs += @('--address', $tag.logicalAddress) }
            Case -Name "rebuild - tag $($tag.name)" -TiaArgs $tagArgs
        }
    }

    # Sorted by path, so Drives/Motor.scl goes in before the Twice it calls. Whatever fails is tried
    # once more at the end - the report then shows whether import order matters to TIA at all.
    $program = Join-Path $RepoDir 'program'
    $files = @(Get-ChildItem $program -Recurse -File | Sort-Object FullName | ForEach-Object {
        @{ State = 'A'; Path = 'program/' + $_.FullName.Substring($program.Length + 1).Replace('\', '/') }
    })
    $textFiles = @($files | Where-Object { $_.Path -notmatch '\.xml$' })
    $xmlFiles  = @($files | Where-Object { $_.Path -match '\.xml$' })

    $retry = @()
    foreach ($f in ($textFiles + $xmlFiles)) {
        if (-not (Sync-Changes 'rebuild' @($f) -ExpectBroken -NoCompile)) { $retry += $f }
    }
    if ($retry.Count) {
        Note "$($retry.Count) file(s) failed on the first pass; trying them again now the rest is in."
        foreach ($f in $retry) { Sync-Changes 'rebuild retry' @($f) -NoCompile | Out-Null }
    }
    Check -Name 'import order did not matter' -Ok ($retry.Count -eq 0) `
        -Detail (($retry | ForEach-Object { $_.Path }) -join "`n") `
        -Note 'Files listed here only went in on the second pass, so a real sync has to order or retry.'
    Case -Name 'rebuild compiles' -TiaArgs @('compile', 'PLC_1')

    Export-Program 'export rebuild' | Out-Null
    $drift = @(Get-Changes 'export rebuild' @('program', 'tags'))
    Case -Name 'export rebuild - diff' -Tool git -TiaArgs @('diff') -AnyExit
    Check -Name 'rebuilt project matches the repository' -Ok ($drift.Count -eq 0) `
        -Detail (($drift | ForEach-Object { "$($_.State) $($_.Path)" }) -join "`n")
    Case -Name 'reset after rebuild check' -Tool git -TiaArgs @('checkout', '-q', '--', '.')

    # ------------------------------------------------------------ history
    Head 'History'
    $count = (& $Git -C $RepoDir rev-list --count HEAD 2>$null | Out-String).Trim()
    Check -Name 'two commits: baseline and the edit' -Ok ($count -eq '2') -Detail "rev-list --count HEAD = $count"
    Case -Name 'git log' -Tool git -TiaArgs @('log', '--stat', '-p') -AnyExit
} catch {
    if ($_.Exception.Message -ne 'stop') { throw }
    Note 'Stopped early: a step the rest depends on failed. The report says which.'
} finally {
    Head 'Cleanup'
    Case -Name 'session stop' -TiaArgs @('session', 'stop')

    if (-not $KeepProject) {
        foreach ($attempt in 1..10) {
            try { Remove-Item -LiteralPath $ProjectDir -Recurse -Force -ErrorAction SilentlyContinue } catch { }
            if (-not (Test-Path -LiteralPath $ProjectDir)) { break }
            Start-Sleep -Seconds 3
        }
        if (Test-Path -LiteralPath $ProjectDir) { Note "Could not delete $ProjectDir - still locked. It is safe to delete by hand." }
    } else {
        Note "Projects and repository kept at $ProjectDir"
    }

    # ------------------------------------------------------------ report
    if (-not (Test-Path $ReportDir)) { New-Item -ItemType Directory -Path $ReportDir -Force | Out-Null }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $md    = Join-Path $ReportDir "gittest-$stamp.md"
    $json  = Join-Path $ReportDir "gittest-$stamp.json"

    $pass = @($script:Results | Where-Object Verdict -eq 'pass').Count
    $fail = @($script:Results | Where-Object Verdict -eq 'FAIL').Count
    $skip = @($script:Results | Where-Object Verdict -eq 'skipped').Count

    $sb = New-Object Text.StringBuilder
    [void]$sb.AppendLine("# tia-cli git round trip - $stamp")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("**$pass passed, $fail failed, $skip skipped**")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Machine')
    [void]$sb.AppendLine()
    foreach ($k in $script:Env.Keys) { [void]$sb.AppendLine("- **$k**: $($script:Env[$k])") }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Results')
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('| Step | Verdict | Exit | Expected | Seconds |')
    [void]$sb.AppendLine('| --- | --- | --- | --- | --- |')
    foreach ($r in $script:Results) {
        [void]$sb.AppendLine("| $($r.Name) | $($r.Verdict) | $($r.Exit) | $($r.Expected) | $($r.Seconds) |")
    }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Transcript')
    foreach ($r in $script:Results) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("### $($r.Name) [$($r.Verdict)]")
        [void]$sb.AppendLine()
        if ($r.Command) { [void]$sb.AppendLine('`' + $r.Command + '`'); [void]$sb.AppendLine() }
        if ($r.Note)    { [void]$sb.AppendLine("_$($r.Note)_"); [void]$sb.AppendLine() }
        if ($r.Output)  {
            [void]$sb.AppendLine('```')
            [void]$sb.AppendLine($r.Output)
            [void]$sb.AppendLine('```')
        }
    }

    Set-Content -Path $md -Value $sb.ToString() -Encoding utf8
    [pscustomobject]@{ environment = $script:Env; results = $script:Results } |
        ConvertTo-Json -Depth 6 | Set-Content -Path $json -Encoding utf8

    Write-Host ''
    Write-Host "$pass passed, $fail failed, $skip skipped" -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
    Write-Host ''
    Write-Host 'Send these two files back:'
    Write-Host "  $md"
    Write-Host "  $json"
    Write-Host ''
}
