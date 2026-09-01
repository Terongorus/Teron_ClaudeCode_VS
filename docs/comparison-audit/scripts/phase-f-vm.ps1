# Phase F (FEAT-3) at the view-model level, still without Visual Studio.
#
# phase-f-unit.ps1 covers the reader and the store. What it explicitly could not reach was the part
# that runs in the app: the refresh is kicked off on a background thread and applied back on the
# dispatcher, and the interesting case - the user renaming a row WHILE that read is in flight - is
# a race that reasoning alone does not settle. It turns out none of that needs an IDE. The view
# model constructs on any STA thread, and the dispatcher can be pumped from here, which makes the
# race deterministic rather than merely likely: the apply cannot run until this script pumps.
#
# Two things make this safe to run on a real machine:
#   * SessionHistoryStore.s_path is redirected into TEMP first, so the real history file in
#     %APPDATA% is never read and never written. Assert it, don't assume it.
#   * The transcripts it reads are real, but read-only.
#
# Needs no VS instance, opens no window, and takes no focus.
param(
    [string]$BinDir = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\bin\Debug\net481',
    [string]$Cwd = 'd:\Projects\Visual Studio Projects\Teron_Extensions',
    [string]$SessionId = '1bb4112b-f6a0-4156-8a3f-d540ac208f92'
)
$ErrorActionPreference = 'Stop'

if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'This must run on an STA thread (powershell.exe is STA by default; pwsh is not).'
}

$script:pass = 0
$script:fail = 0
function Check([string]$label, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; "  PASS  $label $detail" }
    else { $script:fail++; "  FAIL  $label $detail" }
}

Add-Type -AssemblyName WindowsBase, PresentationCore, PresentationFramework

$script:resolving = New-Object 'System.Collections.Generic.HashSet[string]'
$onResolve = [System.ResolveEventHandler] {
    param($sender, $e)
    $simple = ($e.Name -split ',')[0]
    if ($simple -like '*.resources') { return $null }
    foreach ($a in [System.AppDomain]::CurrentDomain.GetAssemblies()) {
        if ($a.GetName().Name -eq $simple) { return $a }
    }
    if (-not $script:resolving.Add($simple)) { return $null }
    try {
        $candidate = Join-Path $BinDir "$simple.dll"
        if (Test-Path $candidate) { return [System.Reflection.Assembly]::LoadFrom($candidate) }
        return $null
    }
    finally { [void]$script:resolving.Remove($simple) }
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($onResolve)
$null = [System.Reflection.Assembly]::LoadFrom((Join-Path $BinDir 'Newtonsoft.Json.dll'))

$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $BinDir 'TeronClaudeCodeVS.dll'))
$vmType = $asm.GetType('TeronClaudeCodeVS.ViewModels.ChatSessionViewModel', $true)
$entryType = $asm.GetType('TeronClaudeCodeVS.ViewModels.SessionHistoryEntry', $true)
$storeType = $asm.GetType('TeronClaudeCodeVS.ViewModels.SessionHistoryStore', $true)
$readerType = $asm.GetType('TeronClaudeCodeVS.ViewModels.SessionTitleReader', $true)
"loaded: $($asm.GetName().Name) $($asm.GetName().Version)"

$NP = [System.Reflection.BindingFlags]'NonPublic,Instance'
$NS = [System.Reflection.BindingFlags]'NonPublic,Public,Static'
function Field($obj, [string]$name) { return $vmType.GetField($name, $NP).GetValue($obj) }
function EntryProp($e, [string]$name) { return $entryType.GetProperty($name).GetValue($e) }
function SetEntryProp($e, [string]$name, $value) { $entryType.GetProperty($name).SetValue($e, $value) }

# ─── Redirect the history file away from the user's real one ────────────────────────────────────
$sandbox = Join-Path $env:TEMP 'teron-phase-f-vm'
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
$sandboxJson = Join-Path $sandbox 'sessions.json'
$pathField = $storeType.GetField('s_path', $NS)
$realPath = $pathField.GetValue($null)
$pathField.SetValue($null, $sandboxJson)
Check 'the history store is pointed at a sandbox, not the real file' ($pathField.GetValue($null) -eq $sandboxJson) "(real one is $realPath)"
$realBefore = if (Test-Path $realPath) { (Get-Item $realPath).LastWriteTimeUtc } else { $null }

# The generated title this session actually has on disk - the value the refresh has to arrive at.
$expected = $readerType.GetMethod('Read', $NS).Invoke($null, @([string]$Cwd, [string]$SessionId))
if ($null -eq $expected) { throw "no title on disk for $SessionId under $Cwd - pick another fixture session" }
$expectedTitle = $expected.GetType().GetProperty('Title').GetValue($expected)
"fixture session $SessionId -> '$expectedTitle'"

# Four rows, each a different branch of the apply.
$seed = @(
    @{ id = $SessionId; title = 'Read the meta-procedure file and tell me what…'; cwd = $Cwd; userTitle = $false }
    @{ id = $SessionId + '-renamed'; title = 'A name I typed myself'; cwd = $Cwd; userTitle = $true }
    @{ id = '00000000-0000-0000-0000-000000000000'; title = 'No transcript for this one'; cwd = $Cwd; userTitle = $false }
)
$rows = $seed | ForEach-Object {
    [pscustomobject][ordered]@{ id = $_.id; title = $_.title; lastUsed = '2026-08-29T18:46:19Z'; cwd = $_.cwd; userTitle = $_.userTitle; titleStamp = '' }
}
Set-Content -Path $sandboxJson -Value (ConvertTo-Json @($rows) -Depth 4) -Encoding UTF8

# ─── Dispatcher pump ────────────────────────────────────────────────────────────────────────────
# The apply is posted with Dispatcher.BeginInvoke, so it cannot run until a frame is pumped here.
# That is what makes the rename race below deterministic instead of a coin flip.
$dispatcher = [System.Windows.Threading.Dispatcher]::CurrentDispatcher
function Pump([int]$milliseconds = 1500) {
    $frame = New-Object System.Windows.Threading.DispatcherFrame
    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds($milliseconds)
    $timer.Add_Tick({ $timer.Stop(); $frame.Continue = $false }.GetNewClosure())
    $timer.Start()
    [System.Windows.Threading.Dispatcher]::PushFrame($frame)
}

""
"=== the constructor's own refresh ==="
$vm = [Activator]::CreateInstance($vmType)
$history = $vmType.GetProperty('SessionHistory').GetValue($vm)   # the collection the overlay binds to
Check 'the seeded rows loaded' ($history.Count -eq 3) "$($history.Count) row(s)"

$stale = $history | Where-Object { (EntryProp $_ 'SessionId') -eq $SessionId }
$renamed = $history | Where-Object { (EntryProp $_ 'SessionId') -eq ($SessionId + '-renamed') }
$orphan = $history | Where-Object { (EntryProp $_ 'SessionId') -eq '00000000-0000-0000-0000-000000000000' }

# Before pumping: the background read may well have finished, but its result is queued on the
# dispatcher and cannot have been applied. If this check ever fails, the refresh is mutating the
# list off the UI thread, which is the bug this design exists to avoid.
Check 'nothing is applied before the dispatcher runs' ((EntryProp $stale 'Title') -cne $expectedTitle) "still '$(EntryProp $stale 'Title')'"

Pump
"  after pumping: '$(EntryProp $stale 'Title')'"
Check 'the truncated title is replaced by the generated one' ((EntryProp $stale 'Title') -ceq $expectedTitle)
Check 'the row the user renamed is untouched' ((EntryProp $renamed 'Title') -ceq 'A name I typed myself')
Check 'a row with no transcript keeps its title' ((EntryProp $orphan 'Title') -ceq 'No transcript for this one')
Check 'the refreshed row is stamped' ((EntryProp $stale 'TitleStamp') -match '^\d+:\d+$') "stamp '$(EntryProp $stale 'TitleStamp')'"
Check 'the refresh flag is released, so history can refresh again' ((Field $vm '_titleRefreshRunning') -eq $false)

$onDisk = Get-Content $sandboxJson -Raw
Check 'the change was persisted, not just shown' ($onDisk -match [regex]::Escape($expectedTitle))
Check 'and the persisted row carries the stamp' ($onDisk -match '"titleStamp": "\d+:\d+"')

""
"=== a rename typed while a refresh is in flight ==="
# Put the row back to a stale title with no stamp, so a fresh refresh WILL want to change it, then
# rename it after the refresh starts but before the apply is pumped. The user's typing must win.
SetEntryProp $stale 'Title' 'stale again'
SetEntryProp $stale 'TitleStamp' ''
SetEntryProp $stale 'HasUserTitle' $false

# Called through PowerShell's own adapter rather than reflection: ChatSessionViewModel and its
# entries are public types, and the adapter unwraps the PSObject that Where-Object put around the
# entry - which an object[] handed to Reflection.Invoke does not.
$vm.OpenSessionHistory()
Check 'the overlay is open' (($vmType.GetProperty('IsSessionHistoryVisible').GetValue($vm)) -eq $true)

# The rename the user commits mid-flight, through the same path the UI uses.
$vm.CommitSessionEntryTitle($stale, 'Typed while it was loading')
Check 'committing a title marks the row as user-named' ((EntryProp $stale 'HasUserTitle') -eq $true)

Pump
"  after the in-flight refresh was applied: '$(EntryProp $stale 'Title')'"
Check 'the rename survives the refresh landing on top of it' ((EntryProp $stale 'Title') -ceq 'Typed while it was loading')
Check 'and the row was not stamped, so a later un-rename still re-reads' ((EntryProp $stale 'TitleStamp') -eq '')

""
"=== the same sequence without the rename (control for the check above) ==="
# Without this, the race check could be passing vacuously: a refresh that computed no update at all
# would also leave the title alone. Same starting state, same call, no rename - the title must move.
SetEntryProp $stale 'Title' 'stale again'
SetEntryProp $stale 'TitleStamp' ''
SetEntryProp $stale 'HasUserTitle' $false
$vm.OpenSessionHistory()
Pump
"  after pumping: '$(EntryProp $stale 'Title')'"
Check 'the identical refresh does change the title when nothing was typed' ((EntryProp $stale 'Title') -ceq $expectedTitle)

""
"=== the real history file was never touched ==="
$realAfter = if (Test-Path $realPath) { (Get-Item $realPath).LastWriteTimeUtc } else { $null }
Check 'the user''s own sessions.json is byte-for-byte as it was' ($realBefore -eq $realAfter) "$realBefore -> $realAfter"

$vm.Dispose()
$pathField.SetValue($null, $realPath)
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
