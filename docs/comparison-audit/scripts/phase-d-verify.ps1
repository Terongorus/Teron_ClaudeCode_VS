# Phase D (GAP-1..GAP-3) verification.
#
# Same reason as phase-c-verify.ps1: everything added in Phase D is XAML, a DataTemplate, or a
# binding, and every one of those fails at RUNTIME rather than at compile time. Two new templates
# (ChoiceCardTemplate, SideQuestionTemplate) and a new palette section had to be instantiated for
# real to prove they resolve.
#
# Carries forward the two UIA facts from Phase C: a WPF Popup is not itself in the UIA tree (its
# content lives in a separate PopupRoot HWND, so queries must enumerate by ProcessId from the
# desktop root), and WPF Panels are not in the control view (AutomationIds must sit on real
# controls or TextBlocks).
#
# Background-safe: UIA InvokePattern only, no SetForegroundWindow, no physical input.
#
# WHAT THIS SCRIPT DELIBERATELY DOES NOT DO: it never presses "Continue in Terminal", never
# confirms /feedback, and never enables Remote Control. Those three are the outward-facing half of
# Phase D - launching a process, uploading a transcript to Anthropic, publishing the session to
# claude.ai/code - and a verification script must not perform them unattended. It asserts that the
# cards render with the right wording and the right numbered actions, and stops there. The
# terminal launch is exercised separately and deliberately by the operator; see the Phase D notes
# in implementation-backlog.md for what was and was not driven.
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$OutDir
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
function Has([string]$needle) { return @(Texts | Where-Object { $_ -like "*$needle*" }).Count -gt 0 }

# Menu rows are Buttons whose content is a two-line StackPanel, so the Button carries no Name.
# Find the TextBlock, then walk up to the nearest invokable ancestor. (Phase C lesson.)
function Find-RowButton([string]$label) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    foreach ($e in Elems) {
        if ($e.Current.Name -ne $label) { continue }
        $n = $e
        for ($i = 0; $i -lt 6 -and $null -ne $n; $i++) {
            try { $null = $n.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); return $n }
            catch { $n = $walker.GetParent($n) }
        }
    }
    return $null
}

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
"=== control loads with the Phase D templates in the dictionary ==="
Check 'chat control instantiated (InputBox present)' $true
$descendants = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition).Count
"  visual tree elements under main window: $descendants"

""
"=== GAP-1 / GAP-2: the CUSTOMIZE section in the palette ==="
$paletteBtn = Find-ByAutomationId -Root $root -AutomationId 'PaletteButton' -TimeoutMs 4000
Invoke-UiaClick -Element $paletteBtn
Start-Sleep -Milliseconds 1500

Check 'CUSTOMIZE header rendered' (Has 'CUSTOMIZE')
foreach ($row in @('Memory', 'Agents', 'Hooks', 'Output styles', 'Permissions')) {
    Check "hand-off row '$row' present" (Has $row)
}
foreach ($desc in @("Manage Claude's memory", 'Configure custom agents', 'Set up event hooks',
                    'Change response formatting style', 'Manage permission settings')) {
    Check "baseline menu description present: '$desc'" (Has $desc)
}
$openRow = Find-ByAutomationId -Root $desktop -AutomationId 'OpenInTerminalRow' -TimeoutMs 3000
Check 'GAP-2 "Open Claude in Terminal" row present' ($null -ne $openRow)

""
"=== GAP-3: the three injected commands appear in the palette list ==="
$raw = @(Texts | Where-Object { $_ -match '^/[a-z0-9:_.-]+$' })
$rows = @()
foreach ($r in $raw) { if ($rows.Count -eq 0 -or $rows[-1] -ne $r) { $rows += $r } }
"  slash-command rows rendered: $($rows.Count)"
if ($rows.Count -lt 5) {
    "  (no live session yet - the palette only lists commands after the CLI's init event)"
}
else {
    foreach ($c in @('/btw', '/feedback', '/remote-control')) {
        Check "injected command $c listed" ($rows -contains $c)
    }
    # UX-5 must still hold with the injected commands merged in.
    $sorted = [System.Collections.Generic.List[string]]::new()
    foreach ($r in $rows) { [void]$sorted.Add($r) }
    $sorted.Sort([System.StringComparer]::OrdinalIgnoreCase)
    Check 'merged list is still A-Z (UX-5 not regressed)' (($rows -join ',') -eq (($sorted.ToArray()) -join ','))
}

& (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
    -OutFile (Join-Path $OutDir '37-PhaseD-customize-section.png') | Out-Null
"  screenshot: 37-PhaseD-customize-section.png"

""
"=== GAP-1: pick 'Memory' and assert the hand-off card, WITHOUT launching anything ==="
$memory = Find-RowButton 'Memory'
if (-not $memory) { Check 'Memory row invokable' $false }
else {
    Invoke-UiaClick -Element $memory
    Start-Sleep -Milliseconds 1800
    $root = Get-MainWindowByPid -ProcessId $ProcessId

    $title = Find-ByAutomationId -Root $root -AutomationId 'ChoiceCardTitle' -TimeoutMs 4000
    if ($title) { Check 'ChoiceCard rendered' $true "title=`"$($title.Current.Name)`"" }
    else { Check 'ChoiceCard rendered' $false }

    Check 'card title is baseline verbatim' (Has 'Continue in Terminal to edit memory?')
    Check 'card body is baseline verbatim' (Has 'Once configured, memories will be picked up by Claude Code here in your IDE.')
    Check 'card shows the command it would run' (Has 'claude /memory')
    Check 'primary action is numbered' (Has '1  Continue in Terminal')
    Check 'secondary action is numbered' (Has '2  Never mind')
    Check 'key hint rendered' (Has '1 / 2 to choose')

    & (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
        -OutFile (Join-Path $OutDir '38-PhaseD-handoff-card.png') | Out-Null
    "  screenshot: 38-PhaseD-handoff-card.png"

    # Decline it. This proves the second action resolves the card, and leaves nothing running.
    $never = Find-ByAutomationId -Root $root -AutomationId 'ChoiceCardSecondary' -TimeoutMs 3000
    if ($never) {
        Invoke-UiaClick -Element $never
        Start-Sleep -Milliseconds 1200
        Check 'declining resolves the card' (Has 'Never mind.')
    }
    else { Check 'secondary button invokable' $false }
}

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
