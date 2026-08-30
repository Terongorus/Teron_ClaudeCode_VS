# FEAT-1 end to end against a REAL running Visual Studio experimental instance.
#
# This is the phase's must-pass check and it cannot be met headlessly: the plan asks that "rewind
# restores real file contents on a scratch file, verified by reading it back, and fork produces a
# genuinely different sessionId". Both halves are driven here through the real UI - the picker, the
# confirmation, and the per-message menu - not through the protocol.
#
# It costs a small amount of quota, and says so up front: it sends TWO short turns on Haiku, one to
# create a scratch file and one to change it, because a rewind with nothing to restore proves
# nothing. Everything else - the empty state, the dry-run preview, the enabled/disabled states, the
# cancel path - is free.
#
# SIDE EFFECTS, all restored or removed before it returns:
#   * the model chip is set to Haiku and the permission chip to Accept Edits, then put back;
#   * a scratch file is created under the solution directory and deleted at the end;
#   * the chat session is forked, which leaves one extra session in the CLI's own history. That is
#     the feature working, not a leak - it is left in place so the fork can be inspected afterwards.
#
# ── Three UIA facts this is built on, each paid for on an earlier run ────────────────────────────
#
#  1. Popup content lives outside the main window's subtree, so it is found from the desktop root
#     by process id (Phase C).
#  2. A `Popup` itself has NO automation peer. Asking whether "RewindPopup" exists is asking about
#     something that never exists, and a first version of this script got a passing "and it closes"
#     out of exactly that. Openness is asked of an element INSIDE the popup.
#  3. The palette button is a TOGGLE, so a retry loop that clicks again to "try harder" simply
#     closes what it just opened, and alternates forever. Every surface here is therefore opened by
#     first LOOKING at whether it is already open and clicking only if it is not - and the looking
#     is a single whole-tree snapshot, measured at ~1s, which reliably catches the popup content the
#     click produced. That snapshot is then reused by every assertion about that surface, which is
#     both cheaper than a lookup per assertion and honest about asserting one moment in time.
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [string]$SolutionDir = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS',
    [string]$ScratchName = 'rewind-live-scratch.txt'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'uia-lib.ps1')
. (Join-Path $here 'vs-menu.ps1')

$script:pass = 0
$script:fail = 0
function Check([string]$label, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; "  PASS  $label $detail" }
    else { $script:fail++; "  FAIL  $label $detail" }
}

$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)

function Snapshot { return @($desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond)) }
function SnapById($snap, [string]$id) {
    return @($snap | Where-Object { $_.Current.AutomationId -eq $id })
}
function SnapOne($snap, [string]$id) {
    $m = SnapById $snap $id
    if ($m.Count -gt 0) { return $m[0] }
    return $null
}
function SnapHas($snap, [string]$needle) {
    return @($snap | Where-Object { $_.Current.Name -like "*$needle*" }).Count -gt 0
}
function ByIdAnywhere([string]$id, [int]$ms = 4000) {
    Find-ByAutomationId -Root $desktop -AutomationId $id -TimeoutMs $ms
}

# Opens a surface by looking first and clicking only if it is shut - see fact 3 above.
function Ensure-Open([string]$toggleId, [string]$innerId, [int]$attempts = 5) {
    for ($i = 0; $i -lt $attempts; $i++) {
        $snap = Snapshot
        if (SnapOne $snap $innerId) { return $snap }
        $toggle = SnapOne $snap $toggleId
        if ($toggle) { Invoke-UiaClick -Element $toggle }
        Start-Sleep -Milliseconds 600
    }
    return $null
}
function Ensure-Closed([string]$innerId, [int]$attempts = 4) {
    for ($i = 0; $i -lt $attempts; $i++) {
        if (-not (SnapOne (Snapshot) $innerId)) { return $true }
        Start-Sleep -Milliseconds 700
    }
    return $false
}

function Set-Input([string]$text) {
    $box = ByIdAnywhere 'InputBox'
    $vp = $null
    if ($box.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $vp.SetValue($text) }
}
function Get-Input {
    $box = ByIdAnywhere 'InputBox'
    $vp = $null
    if ($box -and $box.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { return $vp.Current.Value }
    return $null
}
function Wait-Ready([int]$timeoutSec = 90) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (SnapHas (Snapshot) 'Ready') { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

# Waits for a turn to actually run and finish.
#
# The obvious version - "wait until the status says Ready" - passes instantly, because Ready is
# also what the status says BEFORE anything is sent. The first run of this script did exactly that
# and reported a finished turn while the turn was still in flight. So this waits for the send
# button to be replaced by the stop button first (the control swaps them on IsBusy), and only then
# waits for it to come back.
function Send-Turn([string]$text, [int]$timeoutSec = 240) {
    Set-Input $text
    Invoke-UiaClick -Element (ByIdAnywhere 'SendButton')

    $startedBy = (Get-Date).AddSeconds(30)
    $started = $false
    while ((Get-Date) -lt $startedBy -and -not $started) {
        Start-Sleep -Milliseconds 500
        if (ByIdAnywhere 'StopButton' 400) { $started = $true }
    }
    if (-not $started) { return $false }

    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $snap = Snapshot
        if ((SnapOne $snap 'SendButton') -and -not (SnapOne $snap 'StopButton')) { return $true }
    }
    return $false
}

# The CLI process the IDE itself spawned - by PARENT pid, never by name. A claude.exe matched by
# name once turned out to belong to the operator's real VS Code.
function Get-SessionProcess {
    $p = Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId AND Name='claude.exe'" -ErrorAction SilentlyContinue
    if ($p) { return @($p)[0] }
    return $null
}
function Get-TranscriptDir {
    $folder = ($SolutionDir.ToCharArray() | ForEach-Object {
        if ($_ -eq ':' -or $_ -eq '\' -or $_ -eq '/' -or $_ -eq '_' -or $_ -eq ' ') { '-' } else { $_ }
    }) -join ''
    return Join-Path "$env:USERPROFILE\.claude\projects" $folder
}
function Pick-ChipOption([string]$chipId, [string]$optionText) {
    Invoke-UiaClick -Element (ByIdAnywhere $chipId)
    Start-Sleep -Milliseconds 900
    $opt = Find-InvokableByName -ProcessId $ProcessId -Label $optionText -TimeoutMs 6000
    if ($opt) { Invoke-UiaClick -Element $opt; Start-Sleep -Milliseconds 900; return $true }
    return $false
}

$scratch = Join-Path $SolutionDir $ScratchName
if (Test-Path $scratch) { Remove-Item $scratch -Force }

$root = Get-MainWindowByPid -ProcessId $ProcessId
"main window: $($root.Current.Name)"
$panel = Find-ByAutomationId -Root $root -AutomationId 'InputBox' -TimeoutMs 2000
if (-not $panel) {
    "tool window not open - invoking View > Other Windows > Claude Code"
    Invoke-VsMenuPath -ProcessId $ProcessId -Path @('View', 'Other Windows', 'Claude Code')
    Start-Sleep -Seconds 3
    $root = Get-MainWindowByPid -ProcessId $ProcessId
    $panel = Find-ByAutomationId -Root $root -AutomationId 'InputBox' -TimeoutMs 25000
}
if (-not $panel) { throw 'FAIL: InputBox never appeared - the chat control did not load.' }
Check 'the chat control loads at all (InputBox present)' $true

# ════════════════════════════════════════════════════════════════════════════════════════════════
"" ; "=== the picker exists, and says the right thing with nothing to rewind to ==="
# ════════════════════════════════════════════════════════════════════════════════════════════════
# Start clean. The empty state is only the empty state in a session with no transcript, and a
# re-run would otherwise be looking at the conversation the previous run left behind.
Check 'the session is up before anything is driven' (Wait-Ready)
Invoke-UiaClick -Element (ByIdAnywhere 'NewSessionButton')
Start-Sleep -Seconds 6
Check 'the new session is up' (Wait-Ready)

$snap = Ensure-Open 'PaletteButton' 'RewindRow'
Check 'the palette carries a Rewind entry' ($null -ne $snap)
if ($snap) {
    Check "with baseline's own description" (SnapHas $snap 'Restore code and conversation to an earlier point')
    Invoke-UiaClick -Element (SnapOne $snap 'RewindRow')
    Start-Sleep -Milliseconds 900
}

$snap = Snapshot
Check 'the picker opens' ($null -ne (SnapOne $snap 'RewindPanelTitle'))
Check 'titled as baseline titles it' (SnapHas $snap 'Rewind to')
Check "the empty state is baseline's sentence" (SnapHas $snap 'No messages to rewind to yet.')
Check 'CONTROL - the action rows are absent while there is nothing to act on' `
    ($null -eq (SnapOne $snap 'RewindForkRow'))
$close = SnapOne $snap 'CloseRewindButton'
if ($close) { Invoke-UiaClick -Element $close }
Check 'and it closes' (Ensure-Closed 'RewindPanelTitle')

# ════════════════════════════════════════════════════════════════════════════════════════════════
"" ; "=== two real turns, so there is something to rewind (this is the part that costs) ==="
# ════════════════════════════════════════════════════════════════════════════════════════════════
$transcriptDir = Get-TranscriptDir
# Session IDS, not file names. A first version kept "<id>.jsonl" here and compared it against bare
# ids later, so the "is this session new?" filter never excluded anything and the fork's id check
# passed against a transcript left behind by an earlier run - a false positive that looked exactly
# like a pass.
function SessionIds {
    return @(Get-ChildItem $transcriptDir -Filter '*.jsonl' -ErrorAction SilentlyContinue |
             ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.Name) })
}
$before = SessionIds

Check 'the model chip accepts Haiku' (Pick-ChipOption 'ModelButton' 'Haiku')
Check 'the permission chip accepts Accept Edits' (Pick-ChipOption 'PermissionButton' 'Accept Edits')

Check 'turn 1 ran and finished' (Send-Turn "Create a file named $ScratchName in the current directory whose entire contents are the single word ALPHA. Do nothing else.")
$deadline = (Get-Date).AddSeconds(25)
while (-not (Test-Path $scratch) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 700 }
Check 'turn 1 really wrote the scratch file' (Test-Path $scratch) `
    $(if (Test-Path $scratch) { "'" + (Get-Content $scratch -Raw).Trim() + "'" } else { '' })

Check 'turn 2 ran and finished' (Send-Turn "Now change $ScratchName so its entire contents are the single word BETA. Do nothing else.")
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Content $scratch -Raw -ErrorAction SilentlyContinue).Trim() -ne 'BETA' -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
}
$afterTwoTurns = (Get-Content $scratch -Raw).Trim()
Check 'turn 2 really changed it' ($afterTwoTurns -ceq 'BETA') "'$afterTwoTurns'"

$sessionBefore = Get-SessionProcess
$idBefore = $null
if ($sessionBefore -and $sessionBefore.CommandLine -match '--resume ([0-9a-f-]{36})') { $idBefore = $Matches[1] }
if (-not $idBefore) {
    $fresh = @(SessionIds | Where-Object { $before -notcontains $_ })
    if ($fresh.Count -gt 0) { $idBefore = $fresh[0] }
}
Check 'the live session has an id to fork from' ($null -ne $idBefore) "$idBefore"

# ════════════════════════════════════════════════════════════════════════════════════════════════
"" ; "=== the picker, now that the session has real content ==="
# ════════════════════════════════════════════════════════════════════════════════════════════════
$snap = Ensure-Open 'PaletteButton' 'RewindRow'
Check 'the palette reopens' ($null -ne $snap)
if ($snap) { Invoke-UiaClick -Element (SnapOne $snap 'RewindRow'); Start-Sleep -Milliseconds 1200 }

$snap = Snapshot
$list = SnapOne $snap 'RewindList'
Check 'the picker lists the session transcript' ($null -ne $list)
$rows = @()
if ($list) {
    $rows = @($list.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition))
}
Check 'one row per prompt actually typed, and no tool-result relays' ($rows.Count -eq 2) "got $($rows.Count)"
# The row's accessible name is RewindPoint.ToString(), which is the prompt. That override exists
# because this very check found the rows announcing themselves as the type name.
Check 'newest first - the BETA prompt is at the top' `
    (($rows.Count -ge 1) -and ($rows[0].Current.Name -like '*BETA*')) `
    $(if ($rows.Count -ge 1) { "'" + $rows[0].Current.Name + "'" } else { '' })
Check 'CONTROL - and the older prompt is the one below it' `
    (($rows.Count -ge 2) -and ($rows[1].Current.Name -like '*ALPHA*'))
Check 'rows carry a relative age' (SnapHas $snap 'just now')
Check "and the picker's hint is baseline's" (SnapHas $snap 'Select a message to restore code and fork the conversation from that point.')

$forkRow = SnapOne $snap 'RewindForkRow'
Check 'the three actions are offered here, not only on the message menu' `
    (($null -ne $forkRow) -and ($null -ne (SnapOne $snap 'RewindCodeRow')) -and
     ($null -ne (SnapOne $snap 'RewindForkAndCodeRow')))
Check 'and they are disabled until a row is selected' (($null -ne $forkRow) -and ($forkRow.Current.IsEnabled -eq $false))

if ($rows.Count -ge 1) {
    $si = $null
    if ($rows[0].TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$si)) { $si.Select() }
    Start-Sleep -Milliseconds 700
}
$snap = Snapshot
$forkRow = SnapOne $snap 'RewindForkRow'
Check 'selecting a row enables the actions' (($null -ne $forkRow) -and ($forkRow.Current.IsEnabled -eq $true))

# ════════════════════════════════════════════════════════════════════════════════════════════════
"" ; "=== the confirmation shows a real dry run, and cancelling really cancels ==="
# ════════════════════════════════════════════════════════════════════════════════════════════════
$codeRow = SnapOne $snap 'RewindCodeRow'
if ($codeRow) { Invoke-UiaClick -Element $codeRow }
Start-Sleep -Seconds 5

$snap = Snapshot
Check 'the confirmation opens' ($null -ne (SnapOne $snap 'RewindConfirmTitle'))
Check 'titled "Rewind code" for the code-only action' (SnapHas $snap 'Rewind code')
Check 'CONTROL - the fork note is withheld when no fork is going to happen' (-not (SnapHas $snap 'A new forked conversation'))
Check 'the dry run names the file that would be restored' (SnapHas $snap $ScratchName)
Check 'and reports the change counts' (SnapHas $snap 'will be restored')
Check "the CLI's own caveat about manual edits is shown" (SnapHas $snap 'Rewinding does not affect files edited manually or via bash.')
$confirm = SnapOne $snap 'ConfirmRewindButton'
Check 'the confirm button is enabled once there is something to restore' `
    (($null -ne $confirm) -and ($confirm.Current.IsEnabled -eq $true))

$cancel = SnapOne $snap 'CancelRewindButton'
if ($cancel) { Invoke-UiaClick -Element $cancel }
Check 'Never mind closes it' (Ensure-Closed 'RewindConfirmTitle')
$stillBeta = (Get-Content $scratch -Raw).Trim()
Check 'and changes nothing on disk' ($stillBeta -ceq 'BETA') "'$stillBeta'"

# ════════════════════════════════════════════════════════════════════════════════════════════════
"" ; "=== the per-message menu, and a rewind that really restores the file ==="
# ════════════════════════════════════════════════════════════════════════════════════════════════
$snap = Snapshot
$actionButtons = SnapById $snap 'MessageActionsButton'
Check 'every user message carries a rewind affordance' ($actionButtons.Count -eq 2) "got $($actionButtons.Count)"
if ($actionButtons.Count -ge 1) {
    Invoke-UiaClick -Element $actionButtons[$actionButtons.Count - 1]
    Start-Sleep -Milliseconds 1000
}
$snap = Snapshot
Check 'it opens the message menu' ($null -ne (SnapOne $snap 'MessageForkRow'))
Check "with baseline's three options, verbatim" `
    ((SnapHas $snap 'Fork conversation from here') -and (SnapHas $snap 'Rewind code to here') -and
     (SnapHas $snap 'Fork conversation and rewind code'))

$row = SnapOne $snap 'MessageRewindCodeRow'
if ($row) { Invoke-UiaClick -Element $row }
Start-Sleep -Seconds 5
$snap = Snapshot
Check 'the same confirmation appears from this surface too' ($null -ne (SnapOne $snap 'RewindConfirmTitle'))
$confirm = SnapOne $snap 'ConfirmRewindButton'
if ($confirm) { Invoke-UiaClick -Element $confirm }
Start-Sleep -Seconds 6

$restored = (Get-Content $scratch -Raw -ErrorAction SilentlyContinue)
$restored = if ($restored) { $restored.Trim() } else { '' }
Check 'THE MUST-PASS CHECK - the file on disk is back to its pre-BETA contents' ($restored -ceq 'ALPHA') "'$restored'"
Check "and the transcript says so in the CLI's own words" (SnapHas (Snapshot) 'Code rewind successful')

# ════════════════════════════════════════════════════════════════════════════════════════════════
"" ; "=== forking from a message: a different session, a trimmed view, a prefilled composer ==="
# ════════════════════════════════════════════════════════════════════════════════════════════════
$snap = Snapshot
$actionButtons = SnapById $snap 'MessageActionsButton'
if ($actionButtons.Count -ge 1) {
    Invoke-UiaClick -Element $actionButtons[$actionButtons.Count - 1]
    Start-Sleep -Milliseconds 1000
    $forkRow = SnapOne (Snapshot) 'MessageForkRow'
    if ($forkRow) { Invoke-UiaClick -Element $forkRow }
}
Start-Sleep -Seconds 12

$sessionAfter = Get-SessionProcess
Check 'the fork restarted the CLI' ($null -ne $sessionAfter)
if ($sessionAfter) {
    Check 'it really is a new process' (($null -eq $sessionBefore) -or ($sessionAfter.ProcessId -ne $sessionBefore.ProcessId))
    Check '--fork-session is on its command line' ($sessionAfter.CommandLine -like '*--fork-session*')
    Check '--resume-session-at names an anchor' ($sessionAfter.CommandLine -match '--resume-session-at [0-9a-f-]{36}')
    Check 'it resumes the session it was forked from' `
        (($null -eq $idBefore) -or ($sessionAfter.CommandLine -like "*--resume $idBefore*"))
}

$prefill = Get-Input
Check 'the composer is prefilled with the message it was forked from' `
    (($null -ne $prefill) -and ($prefill -like '*BETA*')) "'$prefill'"

# Read through TextPattern, not through Name. A user message is rendered by the markdown viewer
# into a FlowDocument, which UIA exposes as a Document with an EMPTY Name - so a Name sweep is
# structurally blind to it, and "the turn is gone" passes whether it is there or not. uia-lib has
# carried that warning since Phase D; the first run of this script asserted it the wrong way
# anyway, and got a pass out of a check that could not fail.
$docs = @(Get-DocumentTexts -ProcessId $ProcessId)
Check 'the forked-from turn is gone from the visible transcript' `
    (@($docs | Where-Object { $_ -like '*Now change*' }).Count -eq 0)
Check 'and the turn before it is still there' `
    (@($docs | Where-Object { $_ -like '*Create a file named*' }).Count -gt 0)
Check 'CONTROL - the same read sees text that is still on screen' `
    (@($docs | Where-Object { $_ -like '*ALPHA*' }).Count -gt 0)
$snap = Snapshot
$remaining = SnapById $snap 'MessageActionsButton'
Check 'exactly one user message is left, down from two' ($remaining.Count -eq 1) "got $($remaining.Count)"
Check 'the fork is announced rather than happening silently' (SnapHas $snap 'Forked the conversation from here')

# A forked session does not write a transcript until it persists something, so its id is not on
# disk yet - measured, not assumed: right after a fork the project folder still held only the
# sessions from before it. One trivial turn is what makes the new session real, and it doubles as
# proof that the fork is a working session and not just a new command line.
Check 'the forked session accepts a turn' (Send-Turn 'Reply with exactly: OK')
$deadline = (Get-Date).AddSeconds(60)
$idAfter = $null
while ((Get-Date) -lt $deadline -and -not $idAfter) {
    $fresh = @(SessionIds | Where-Object { $_ -ne $idBefore -and $before -notcontains $_ })
    if ($fresh.Count -gt 0) { $idAfter = $fresh[0] } else { Start-Sleep -Seconds 3 }
}
Check 'THE MUST-PASS CHECK - the fork has a genuinely different session id' `
    (($null -ne $idAfter) -and ($idAfter -ne $idBefore)) "before=$idBefore after=$idAfter"

# Stronger than the id alone: the forked transcript must hold the kept turn and not the dropped one.
if ($idAfter) {
    $forkText = Get-Content (Join-Path $transcriptDir "$idAfter.jsonl") -Raw
    Check 'the fork kept the turn before the fork point' ($forkText -like '*single word ALPHA*')
    Check 'and dropped the turn it was forked from' (-not ($forkText -like '*single word BETA*'))
    $origText = Get-Content (Join-Path $transcriptDir "$idBefore.jsonl") -Raw
    Check 'CONTROL - the session it was forked from still has that turn, untouched' `
        ($origText -like '*single word BETA*')
}

# ════════════════════════════════════════════════════════════════════════════════════════════════
"" ; "=== put the environment back ==="
# ════════════════════════════════════════════════════════════════════════════════════════════════
Set-Input ''
$null = Pick-ChipOption 'PermissionButton' 'CLI Default'
$null = Pick-ChipOption 'ModelButton' 'Default'
if (Test-Path $scratch) { Remove-Item $scratch -Force }
Check 'the scratch file is gone' (-not (Test-Path $scratch))

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
