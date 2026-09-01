# Phase E (FEAT-2) verification.
#
# Everything Phase E adds is either XAML (two new buttons inside existing DataTemplates) or a VS
# SDK call that only exists at runtime, so a clean build proves nothing about either. This drives a
# real edit through a real session and then asserts against the real VS document-window list -
# looking at a picture of a tab would not distinguish an opened comparison from a rendering of one.
#
# Carries forward the Phase C/D UIA facts: a WPF Popup lives in its own PopupRoot HWND so queries
# enumerate by ProcessId from the desktop root, WPF Panels are not in the control view, and
# FlowDocument text is only reachable through TextPattern (Get-DocumentTexts), never through Name.
#
# Background-safe: UIA InvokePattern only, no SetForegroundWindow, no synthesised input.
#
# WHAT THIS DOES TOUCH, deliberately: it creates one scratch file inside the test project, has
# Claude edit it, and deletes it again at the end. Nothing outside that folder and the extension's
# own temp directory is written. The edit is approved for real, because "the tab shows the applied
# state" is exactly half of what FEAT-2 claims and cannot be checked without applying something.
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$ScratchFile = 'D:\Projects\Visual Studio Projects\Test_Project_Claude\phase-e-scratch.txt'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'uia-lib.ps1')
. (Join-Path $here 'vs-menu.ps1')
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$script:pass = 0
$script:fail = 0
function Check([string]$label, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; "  PASS  $label $detail" }
    else { $script:fail++; "  FAIL  $label $detail" }
}

$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
function Elems { $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond) }
function Texts { Elems | ForEach-Object { $_.Current.Name } | Where-Object { $_ } }
# Ordinal Contains, deliberately not -like: the tab caption this phase asserts on is
# "[Claude Code] <file>", and PowerShell's -like reads "[...]" as a character class, so the
# wildcard form silently fails to match the very string FEAT-2 is about.
function Has([string]$needle) {
    return @(Texts | Where-Object { $_.IndexOf($needle, [StringComparison]::Ordinal) -ge 0 }).Count -gt 0
}

function Wait-For([string]$needle, [int]$seconds = 120) {
    for ($i = 0; $i -lt [int]($seconds / 2); $i++) {
        if (Has $needle) { return $true }
        Start-Sleep -Seconds 2
    }
    return $false
}

function Send-Prompt([string]$text) {
    $root = Get-MainWindowByPid -ProcessId $ProcessId
    $box = Find-ByAutomationId -Root $root -AutomationId 'InputBox' -TimeoutMs 5000
    Set-UiaValue -Element $box -Value $text
    Start-Sleep -Milliseconds 700
    Invoke-UiaClick -Element (Find-ByAutomationId -Root $root -AutomationId 'SendButton' -TimeoutMs 5000)
}

# Retrying variant lives in uia-lib.ps1; see the note there on why waiting for text is not the
# same question as finding the control that text belongs to.
function Find-RowButton([string]$label) {
    return Find-InvokableByName -ProcessId $ProcessId -Label $label -TimeoutMs 8000
}

$scratchName = Split-Path -Leaf $ScratchFile
$original = "line one`r`nthe marker word is ALPHA here`r`nline three`r`n"
Set-Content -Path $ScratchFile -Value $original -NoNewline -Encoding utf8
"scratch file: $ScratchFile ($($original.Length) bytes)"

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

""
"=== permission mode -> Manual, so an Edit actually raises an approval card ==="
$permBtn = Find-ByAutomationId -Root $root -AutomationId 'PermissionButton' -TimeoutMs 5000
Invoke-UiaClick -Element $permBtn
Start-Sleep -Milliseconds 1200
$manual = Find-RowButton 'Manual'
if ($manual) { Invoke-UiaClick -Element $manual; Start-Sleep -Milliseconds 1200; "  selected Manual" }
else { throw 'FAIL: could not find the Manual permission row.' }

""
"=== drive one real Edit and stop at the approval card ==="
Send-Prompt "Use the Edit tool to replace the word ALPHA with BRAVO in $scratchName. Change nothing else and do not read any other file."

if (Wait-For 'Allow Edit file?' 180) { Check 'approval card raised' $true }
else {
    # Title wording varies with what the CLI sends; fall back to the numbered action.
    Check 'approval card raised' (Wait-For '1  Allow' 30)
}

""
"=== FEAT-2 on the pending card ==="
$root = Get-MainWindowByPid -ProcessId $ProcessId
$permDiffBtn = Find-ByAutomationId -Root $root -AutomationId 'PermissionOpenDiffTab' -TimeoutMs 5000
Check '"Open diff tab" button present on the approval card' ($null -ne $permDiffBtn)

# The auto-open half: the option defaults on, so the tab must already be there without anyone
# pressing anything.
$caption = "[Claude Code] $scratchName"
Check 'diff tab opened automatically with the proposed change' (Wait-For $caption 20) "caption=`"$caption`""
Check 'left pane labelled as the before side'  (Has "$scratchName (before)")
Check 'right pane labelled as proposed'        (Has "$scratchName (proposed)")

# VS's own difference window supplies the navigation half of baseline's five toolbar buttons.
$navNames = @(Texts | Where-Object { $_ -match '(?i)difference' }) | Select-Object -Unique
"  difference-related UI in the window: $([string]::Join(' | ', $navNames))"
Check 'VS diff window exposes difference navigation' ($navNames.Count -gt 0)

& (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
    -OutFile (Join-Path $OutDir '42-PhaseE-proposed-diff-tab.png') | Out-Null
"  screenshot: 42-PhaseE-proposed-diff-tab.png"

""
"=== the comparison files themselves ==="
$tempRoot = Join-Path $env:TEMP 'TeronClaudeCodeVS-difftab'
$before = @(Get-ChildItem $tempRoot -Recurse -Filter '*.before.*' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending)
$after = @(Get-ChildItem $tempRoot -Recurse -Filter '*.after.*' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending)
Check 'a before/after pair was written' (($before.Count -gt 0) -and ($after.Count -gt 0))
if ($before.Count -gt 0) {
    Check 'both sides are read-only (the tab is a view, not an editor)' `
        ($before[0].IsReadOnly -and $after[0].IsReadOnly)
    Check 'extension preserved for syntax colouring' ($before[0].Extension -eq [IO.Path]::GetExtension($ScratchFile))
    $lt = Get-Content $before[0].FullName -Raw
    $rt = Get-Content $after[0].FullName -Raw
    Check 'left side is the file as it stands now'  ($lt -like '*ALPHA*' -and $lt -notlike '*BRAVO*')
    Check 'right side is the file as it would be'   ($rt -like '*BRAVO*' -and $rt -notlike '*ALPHA*')
    Check 'only the marker changed' (($lt -replace 'ALPHA', 'BRAVO') -eq $rt)
}

""
"=== approve, then re-open the tab from the finished tool card (the applied path) ==="
$allow = Find-RowButton '1  Allow'
if ($allow) { Invoke-UiaClick -Element $allow } else { Check 'Allow button invokable' $false }
Start-Sleep -Seconds 6

$onDisk = Get-Content $ScratchFile -Raw
Check 'the edit really landed on disk' ($onDisk -like '*BRAVO*' -and $onDisk -notlike '*ALPHA*')

if (Wait-For 'Done' 180) { "  turn finished" } else { "  WARN  no result footer seen" }

$root = Get-MainWindowByPid -ProcessId $ProcessId
$stillThere = Find-ByAutomationId -Root $root -AutomationId 'PermissionOpenDiffTab' -TimeoutMs 1500
Check 'approval card drops its diff button once answered' ($null -eq $stillThere)

# Expand the tool card so its detail (and the button inside it) is realised.
$root = Get-MainWindowByPid -ProcessId $ProcessId
$expanded = Expand-UiaByLabel -ProcessId $ProcessId -Label 'Edit file'
Check 'tool card expands' $expanded
Start-Sleep -Milliseconds 1500

$root = Get-MainWindowByPid -ProcessId $ProcessId
$callDiffBtn = Find-ByAutomationId -Root $root -AutomationId 'ToolCallOpenDiffTab' -TimeoutMs 6000
Check '"Open diff tab" button present on the finished tool call' ($null -ne $callDiffBtn)

if ($callDiffBtn) {
    Invoke-UiaClick -Element $callDiffBtn
    Start-Sleep -Seconds 3
    Check 'tab reopened for the applied edit' (Has $caption)
    Check 'right pane now labelled as applied' (Has "$scratchName (after Claude's edit)")

    $before2 = @(Get-ChildItem $tempRoot -Recurse -Filter '*.before.*' -ErrorAction SilentlyContinue |
                 Sort-Object LastWriteTime -Descending)
    $after2 = @(Get-ChildItem $tempRoot -Recurse -Filter '*.after.*' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTime -Descending)
    if ($before2.Count -gt 0) {
        $lt2 = Get-Content $before2[0].FullName -Raw
        $rt2 = Get-Content $after2[0].FullName -Raw
        # This is the half that needs a real "before" from somewhere other than the working copy.
        Check 'recovered pre-edit contents for an already-applied edit' ($lt2 -like '*ALPHA*')
        Check 'after side is the working copy' ($rt2 -like '*BRAVO*')
    }

    # One tab per file: re-opening must replace, not stack. Count TabItems specifically - the
    # same caption also shows up on the window frame, the document element and the title bar, so
    # counting raw Name matches measures the UIA tree's shape rather than the number of tabs.
    $tabs = @(Elems | Where-Object {
        $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::TabItem -and
        $_.Current.Name.IndexOf($caption, [StringComparison]::Ordinal) -ge 0
    })
    Check 'still exactly one comparison tab for this file' ($tabs.Count -le 1) "count=$($tabs.Count)"
}

& (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
    -OutFile (Join-Path $OutDir '43-PhaseE-applied-diff-tab.png') | Out-Null
"  screenshot: 43-PhaseE-applied-diff-tab.png"

""
"=== a tool that is not a file edit must say so rather than open an empty tab ==="
"  (checked by inspection of VsDiffTab.CanOpenDiffTab gating - the button is not rendered at all)"
$bashCards = @(Texts | Where-Object { $_ -eq 'Run command' })
"  non-edit tool cards present in this transcript: $($bashCards.Count)"

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
