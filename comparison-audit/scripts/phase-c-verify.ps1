# Phase C (UX-1..UX-12) verification.
#
# Why this exists rather than trusting the build: every change in Phase C is either XAML or a
# binding. A missing StaticResource key, a Style applied to the wrong TargetType, or a Binding to a
# property that does not exist are all RUNTIME failures - the first two throw XamlParseException at
# parse time and leave the tool window empty, the third fails silently and leaves a blank label. A
# clean compile distinguishes none of those from working code, so the control has to be
# instantiated for real and its rendered text read back.
#
# TWO UIA FACTS THIS SCRIPT IS BUILT AROUND, both learned by getting them wrong first:
#
#   1. A WPF Popup is NOT an element in the UIA tree, and neither is its own AutomationId. The
#      popup's content is hosted in a separate top-level PopupRoot HWND. Searching the main
#      window - or even the desktop - for "ModelPopup" therefore finds nothing even when the popup
#      is open and fully rendered. The reliable query is: enumerate every element belonging to this
#      process id from the desktop root, and look at the text.
#
#   2. WPF Panels (StackPanel, Grid) are not in the UIA *control* view, so an AutomationId placed
#      on one is invisible to FindAll. Ids must go on real controls or TextBlocks.
#
# Background-safe throughout: UIA InvokePattern + PrintWindow only. No SetForegroundWindow, no
# physical mouse or keyboard, so the run does not disturb whatever the user is doing.
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

# Every visible string anywhere in the process, popups included. See note 1 above.
function Get-ProcessText {
    $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ }
}
function Has([string[]]$texts, [string]$needle) {
    return @($texts | Where-Object { $_ -like "*$needle*" }).Count -gt 0
}

$root = Get-MainWindowByPid -ProcessId $ProcessId
"main window: $($root.Current.Name)"

# --- open the tool window ------------------------------------------------------------------
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
"=== resource dictionary + control load ==="
Check 'chat control instantiated (InputBox present)' $true
$descendants = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition).Count
"  visual tree elements under main window: $descendants"
"  (a XamlParseException from any unresolved token would leave this near zero)"

""
"=== UX-11 empty state ==="
$title = Find-ByAutomationId -Root $root -AutomationId 'EmptyStateTitle' -TimeoutMs 3000
Check 'EmptyStateTitle rendered on a new session' ($null -ne $title)
$t = Get-ProcessText
Check 'empty state shows its one-line prompt' (Has $t 'Ask about this solution')
Check 'empty state advertises @ and /' (Has $t '@ to attach a file')

""
"=== UX-6 input placeholder + focus chord ==="
$ph = Find-ByAutomationId -Root $root -AutomationId 'InputPlaceholder' -TimeoutMs 3000
if ($ph) {
    $txt = $ph.Current.Name
    Check 'InputPlaceholder present' $true "text=`"$txt`""
    Check 'placeholder names a focus shortcut (UX-6 acceptance)' ($txt -match 'to focus')
}
else { Check 'InputPlaceholder present' $false }

""
"=== UX-1 model picker descriptions ==="
$modelBtn = Find-ByAutomationId -Root $root -AutomationId 'ModelButton' -TimeoutMs 3000
if ($modelBtn) {
    Invoke-UiaClick -Element $modelBtn
    Start-Sleep -Milliseconds 1200
    $t = Get-ProcessText
    Check 'MODELS header rendered' (Has $t 'MODELS')
    Check 'Opus row states the usage multiplier' (Has $t 'usage vs Sonnet')
    Check 'Fable row states the credit requirement' (Has $t 'Requires usage credits')
    Check 'Haiku row states its trade-off' (Has $t 'Fastest for quick answers')
    Check 'Sonnet row carries a description' (Has $t 'Efficient for routine tasks')
    & (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
        -OutFile (Join-Path $OutDir '31-PhaseC-model-descriptions.png') | Out-Null
    Invoke-UiaClick -Element $modelBtn
    Start-Sleep -Milliseconds 600
}
else { Check 'ModelButton found' $false }

""
"=== UX-2 permission picker descriptions + Shift+Tab hint ==="
$permBtn = Find-ByAutomationId -Root $root -AutomationId 'PermissionButton' -TimeoutMs 3000
if ($permBtn) {
    $modeBefore = $permBtn.Current.Name
    Invoke-UiaClick -Element $permBtn
    Start-Sleep -Milliseconds 1200
    $t = Get-ProcessText
    Check 'MODES header rendered' (Has $t 'MODES')
    Check 'Shift+Tab hint rendered' (Has $t 'tab to switch')
    Check 'Manual carries its description' (Has $t 'approval before making each edit')
    Check "Don't Ask carries its corrected description" (Has $t 'denies anything not already pre-approved')
    Check 'Bypass carries its description' (Has $t 'potentially dangerous commands')
    Check 'Auto carries its description' (Has $t 'pause for anything risky')
    & (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
        -OutFile (Join-Path $OutDir '32-PhaseC-permission-descriptions.png') | Out-Null
    Invoke-UiaClick -Element $permBtn
    Start-Sleep -Milliseconds 600
    "  (permission chip currently reads: '$modeBefore')"
}
else { Check 'PermissionButton found' $false }

""
"=== UX-4 palette filter + UX-10 version footer ==="
$paletteBtn = Find-ByAutomationId -Root $root -AutomationId 'PaletteButton' -TimeoutMs 3000
if ($paletteBtn) {
    Invoke-UiaClick -Element $paletteBtn
    Start-Sleep -Milliseconds 1200

    $filter = Find-ByAutomationId -Root $desktop -AutomationId 'PaletteFilterBox' -TimeoutMs 4000
    Check 'PaletteFilterBox present' ($null -ne $filter)
    $ver = Find-ByAutomationId -Root $desktop -AutomationId 'PaletteVersionText' -TimeoutMs 3000
    if ($ver) { Check 'PaletteVersionText present' $true "text=`"$($ver.Current.Name)`"" }
    else { Check 'PaletteVersionText present' $false }

    # UX-5: the command list must be alphabetical. Read the rendered rows and compare with a sort.
    $rows = @($desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond) |
            ForEach-Object { $_.Current.Name } |
            Where-Object { $_ -match '^/[a-z0-9:_-]+$' })
    if ($rows.Count -gt 2) {
        $sorted = @($rows | Sort-Object -Culture 'en-US')
        Check 'slash-command list is A-Z (UX-5)' (($rows -join ',') -eq ($sorted -join ',')) "($($rows.Count) rows)"
        "  first five: $((@($rows) | Select-Object -First 5) -join ' ')"
    }
    else { "  (no slash-command rows rendered - CLI session may not have initialised)" }

    # UX-4: typing must narrow the list.
    if ($filter -and $rows.Count -gt 2) {
        Set-UiaValue -Element $filter -Value 'co'
        Start-Sleep -Milliseconds 900
        $after = @($desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond) |
                ForEach-Object { $_.Current.Name } |
                Where-Object { $_ -match '^/[a-z0-9:_-]+$' })
        Check 'typing in the filter narrows the list' ($after.Count -lt $rows.Count) "($($rows.Count) -> $($after.Count) rows)"
        & (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
            -OutFile (Join-Path $OutDir '33-PhaseC-palette-filter.png') | Out-Null
        Set-UiaValue -Element $filter -Value ''
        Start-Sleep -Milliseconds 500
    }

    Invoke-UiaClick -Element $paletteBtn
    Start-Sleep -Milliseconds 600
}
else { Check 'PaletteButton found' $false }

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
