# Phase H (FEAT-6, FEAT-7) and the Phase G panels (FEAT-4, FEAT-5), driven against a REAL running
# Visual Studio experimental instance.
#
# Why this exists: Phases G and H are mostly XAML - popups, templates, bindings, Click handlers -
# and every one of those fails at RUNTIME, not at compile time. The headless harnesses
# (phase-g-unit / phase-g-vm / phase-h-unit) prove the parsers and the view models; only this
# proves the panels open, render, and do what their buttons say. Phase G shipped without this pass,
# so its two panels are re-checked here alongside Phase H's own work rather than left on trust.
#
# Background-safe: UIA InvokePattern and ValuePattern only. No SetForegroundWindow, no physical
# mouse or keyboard. It keeps working while the operator is in another application and never steals
# their focus.
#
# WHAT THIS SCRIPT DELIBERATELY DOES NOT DO: it never sends a message to the model, so it costs no
# quota; and it opens the "Upload from computer" file dialog only to read it back and cancel it,
# never to attach anything.
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [string]$OutDir = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\docs\comparison-audit\screenshots\our-extension'
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

# Set-UiaValue in uia-lib refuses an empty string (its parameter is a mandatory [string]), and
# clearing the input box between checks is exactly what this needs, so go to the pattern directly.
Add-Type -ErrorAction SilentlyContinue @"
using System;
using System.Runtime.InteropServices;
public class DlgCloser {
  [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@

function Clear-Input($element) {
    $vp = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $vp.SetValue('') }
}

$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
function Elems { $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond) }
function Texts { Elems | ForEach-Object { $_.Current.Name } | Where-Object { $_ } }
function Has([string]$needle) { return @(Texts | Where-Object { $_ -like "*$needle*" }).Count -gt 0 }
function ById([string]$id, [int]$ms = 3000) {
    Find-ByAutomationId -Root (Get-MainWindowByPid -ProcessId $ProcessId) -AutomationId $id -TimeoutMs $ms
}
# Popup content lives in its own PopupRoot HWND, outside the main window's subtree (Phase C lesson),
# so anything inside an open popup has to be found from the desktop root by process id.
function ByIdAnywhere([string]$id, [int]$ms = 4000) {
    Find-ByAutomationId -Root $desktop -AutomationId $id -TimeoutMs $ms
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
Check 'the chat control loads at all (InputBox present)' $true

# ════════════════════════════════════════════════════════════════════════════════════════════════
# Phase G debt: the two panels that shipped without ever being rendered.
# ════════════════════════════════════════════════════════════════════════════════════════════════

""
"=== FEAT-4: the MCP servers panel opens and renders ==="
$paletteBtn = ById 'PaletteButton' 4000
Invoke-UiaClick -Element $paletteBtn
Start-Sleep -Milliseconds 1200
$mcpRow = Find-InvokableByName -ProcessId $ProcessId -Label 'MCP servers' -TimeoutMs 5000
Check 'the CUSTOMIZE section offers an "MCP servers" row' ($null -ne $mcpRow)
if ($mcpRow) {
    Invoke-UiaClick -Element $mcpRow
    # The panel runs `claude mcp list`, which health-checks every configured server; give it room.
    Start-Sleep -Seconds 6
    Check 'the MCP panel renders its title' (Has 'MCP servers')

    $err   = ByIdAnywhere 'McpErrorText' 1500
    $empty = ByIdAnywhere 'McpEmptyStateText' 1500
    $list  = ByIdAnywhere 'McpServerList' 3000
    Check 'the panel resolved to a real state, not a blank card' `
        (($null -ne $list) -or ($null -ne $empty) -or ($null -ne $err))
    if ($err) { "  panel reported an error: $($err.Current.Name)" }
    if ($empty) { "  empty state shown: $($empty.Current.Name)" }
    Check 'the panel names the directory it queried' (Has 'Teron_ClaudeCode_VS')
    Check 'the footer offers the docs link' (Has 'Learn more about MCP')
    & (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
        -OutFile (Join-Path $OutDir '40-PhaseG-mcp-panel.png') | Out-Null
    "  screenshot: 40-PhaseG-mcp-panel.png"
    $close = ByIdAnywhere 'CloseMcpButton' 2000
    if ($close) { Invoke-UiaClick -Element $close; Start-Sleep -Milliseconds 600 }
    Check 'the panel has a close button' ($null -ne $close)
}

""
"=== FEAT-5: the plugins panel opens, renders, and its tab strip works ==="
Invoke-UiaClick -Element (ById 'PaletteButton' 4000)
Start-Sleep -Milliseconds 1200
$plugRow = Find-InvokableByName -ProcessId $ProcessId -Label 'Manage plugins' -TimeoutMs 5000
Check 'the CUSTOMIZE section offers a "Manage plugins" row' ($null -ne $plugRow)
if ($plugRow) {
    Invoke-UiaClick -Element $plugRow
    Start-Sleep -Seconds 6
    Check 'the plugins panel renders its title' (Has 'Manage plugins')
    Check 'the Plugins tab is present' (Has 'Plugins')
    Check 'the Marketplaces tab is present' (Has 'Marketplaces')

    # The panel remembers which tab was last open, so a run that switched tabs would leave the
    # next run starting somewhere else. Select Plugins explicitly rather than assume it.
    $plugTab = ByIdAnywhere 'PluginsTabButton' 2000
    Check 'the Plugins tab is selectable' ($null -ne $plugTab)
    if ($plugTab) { Invoke-UiaClick -Element $plugTab; Start-Sleep -Milliseconds 900 }
    $pluginsEmpty = ByIdAnywhere 'PluginsEmptyStateText' 2000
    $installed    = ByIdAnywhere 'InstalledPluginList' 2000
    $plugErr      = ByIdAnywhere 'PluginsErrorText' 1500
    Check 'the Plugins tab resolved to a real state' `
        (($null -ne $pluginsEmpty) -or ($null -ne $installed) -or ($null -ne $plugErr))
    if ($pluginsEmpty) { "  plugins empty state: $($pluginsEmpty.Current.Name)" }
    if ($plugErr) { "  plugins error: $($plugErr.Current.Name)" }

    $mktTab = ByIdAnywhere 'MarketplacesTabButton' 2000
    if (-not $mktTab) { $mktTab = Find-InvokableByName -ProcessId $ProcessId -Label 'Marketplaces' -TimeoutMs 3000 }
    if ($mktTab) {
        Invoke-UiaClick -Element $mktTab
        Start-Sleep -Milliseconds 1200
        $mktEmpty = ByIdAnywhere 'MarketplacesEmptyStateText' 2000
        $mktList  = ByIdAnywhere 'MarketplaceList' 2000
        Check 'switching to Marketplaces shows that tab''s own content' `
            (($null -ne $mktEmpty) -or ($null -ne $mktList))
        # CONTROL: the Plugins tab's content must be GONE, or "the tab switched" proves nothing.
        $stillInstalled = ByIdAnywhere 'InstalledPluginList' 800
        $stillEmpty     = ByIdAnywhere 'PluginsEmptyStateText' 800
        Check 'CONTROL - and hides the Plugins tab''s content' `
            (($null -eq $stillInstalled) -and ($null -eq $stillEmpty))
    }
    else { Check 'the Marketplaces tab is invokable' $false }
    Check 'the footer offers the docs link' (Has 'Learn more about plugins')
    & (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
        -OutFile (Join-Path $OutDir '41-PhaseG-plugins-panel.png') | Out-Null
    "  screenshot: 41-PhaseG-plugins-panel.png"
    $close = ByIdAnywhere 'ClosePluginsButton' 2000
    if ($close) { Invoke-UiaClick -Element $close; Start-Sleep -Milliseconds 600 }
    Check 'the panel has a close button' ($null -ne $close)
}

# ════════════════════════════════════════════════════════════════════════════════════════════════
# Phase H, FEAT-6.
# ════════════════════════════════════════════════════════════════════════════════════════════════

""
"=== FEAT-6: the + menu opens with baseline's three entries ==="
Clear-Input (ById 'InputBox' 3000)
Start-Sleep -Milliseconds 300
$addBtn = ById 'AddMenuButton' 4000
Check 'the input area carries a + button' ($null -ne $addBtn)
if (-not $addBtn) { throw 'FAIL: no AddMenuButton - the rest of FEAT-6 cannot be checked.' }
Invoke-UiaClick -Element $addBtn
Start-Sleep -Milliseconds 1200
foreach ($row in @('Upload from computer', 'Add context', 'Browse the web')) {
    Check "the menu offers '$row'" (Has $row)
}
Check 'the web box is hidden until asked for' ($null -eq (ByIdAnywhere 'WebQueryBox' 800))
& (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
    -OutFile (Join-Path $OutDir '42-PhaseH-add-menu.png') | Out-Null
"  screenshot: 42-PhaseH-add-menu.png"

""
"=== FEAT-6: 'Browse the web' reveals its box and composes a fetch line ==="
$webRow = Find-InvokableByName -ProcessId $ProcessId -Label 'Browse the web' -TimeoutMs 4000
Invoke-UiaClick -Element $webRow
Start-Sleep -Milliseconds 900
$webBox = ByIdAnywhere 'WebQueryBox' 4000
Check 'CONTROL - and the box appears once it is' ($null -ne $webBox)
Check 'the box explains what it will do' (Has 'A URL is fetched; anything else is searched for.')
if ($webBox) {
    Set-UiaValue -Element $webBox -Value 'docs.claude.com/en/docs/mcp'
    Start-Sleep -Milliseconds 400
    $addWeb = ByIdAnywhere 'AddWebContextButton' 3000
    Invoke-UiaClick -Element $addWeb
    Start-Sleep -Milliseconds 1200

    $input = ById 'InputBox' 3000
    $val = $null
    $vp = $null
    if ($input.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
        $val = $vp.Current.Value
    }
    "  input box now reads: `"$val`""
    Check 'a bare host became a fetch instruction in the input box' `
        ($val -ceq 'Read https://docs.claude.com/en/docs/mcp and use it as context for this conversation. ')
    Check 'and the menu closed itself afterwards' ($null -eq (ByIdAnywhere 'WebQueryBox' 800))

    # Clear the box again so the next check starts from a known state.
    Clear-Input $input
    Start-Sleep -Milliseconds 300
}

""
"=== FEAT-6: 'Add context' hands over to the @ mention picker ==="
Invoke-UiaClick -Element (ById 'AddMenuButton' 4000)
Start-Sleep -Milliseconds 1000
$ctxRow = Find-InvokableByName -ProcessId $ProcessId -Label 'Add context' -TimeoutMs 4000
Check 'the row is invokable' ($null -ne $ctxRow)
if ($ctxRow) {
    Invoke-UiaClick -Element $ctxRow
    Start-Sleep -Milliseconds 1500
    $input = ById 'InputBox' 3000
    $vp = $null; $val = ''
    if ($input.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $val = $vp.Current.Value }
    Check 'it inserts the @ that baseline inserts' ($val -ceq '@')
    # This is the check that matters: a programmatic insert sets Text and CaretIndex separately, so
    # the picker only opens if UpdateInputPickers is re-run after both. If that call were dropped,
    # everything above would still pass and the feature would still be broken.
    $picker = ByIdAnywhere 'FilePickerList' 4000
    Check 'and the mention picker actually opens on it' ($null -ne $picker)
    if ($picker) {
        $n = $picker.FindAll([System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition).Count
        Check 'the picker is populated with solution files' ($n -gt 0) "$n row(s)"
    }
    & (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
        -OutFile (Join-Path $OutDir '43-PhaseH-add-context-picker.png') | Out-Null
    "  screenshot: 43-PhaseH-add-context-picker.png"
    Clear-Input $input
    Start-Sleep -Milliseconds 400
}

""
"=== FEAT-6: 'Upload from computer' opens a real file dialog (then cancels it) ==="
Invoke-UiaClick -Element (ById 'AddMenuButton' 4000)
Start-Sleep -Milliseconds 1000
$upRow = Find-InvokableByName -ProcessId $ProcessId -Label 'Upload from computer' -TimeoutMs 4000
Check 'the row is invokable' ($null -ne $upRow)
if ($upRow) {
    Invoke-UiaClick -Element $upRow

    # GOTCHA, learned the hard way on 2026-08-30: a Win32 common dialog raised by a VS extension is
    # an OWNED window, so in the UIA tree it hangs off the VS main window - it is a Descendant of
    # the desktop, never a Child of it. The first version of this check enumerated desktop Children
    # by process id (the idiom that works for tool windows) and concluded no dialog had opened,
    # while two real "Attach files" dialogs were in fact sitting on screen. Find it by name.
    $dlgCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Attach files')
    $dlg = $null
    for ($i = 0; $i -lt 15 -and $null -eq $dlg; $i++) {
        Start-Sleep -Milliseconds 700
        foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $dlgCond)) {
            if ($w.Current.ProcessId -eq $ProcessId) { $dlg = $w; break }
        }
    }
    Check 'a file dialog opened, titled as the code asked for' ($null -ne $dlg) `
        $(if ($dlg) { "class=$($dlg.Current.ClassName)" } else { '' })
    if ($dlg) {
        $kids = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $names = $kids | ForEach-Object { $_.Current.Name }
        Check 'it offers the attachment filter the staging path actually accepts' `
            (@($names | Where-Object { $_ -like '*All supported files*' }).Count -gt 0)
        Check 'CONTROL - and it is not offering some other dialog''s contents' `
            (@($names | Where-Object { $_ -like '*File name*' }).Count -gt 0)

        # Cancel it. The dialog's Cancel is a real Button; the elements merely NAMED "Cancel"
        # elsewhere in its tree support no invokable pattern, so filter by control type first.
        $cancel = $null
        foreach ($e in $kids) {
            if ($e.Current.Name -eq 'Cancel' -and
                $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) { $cancel = $e; break }
        }
        if ($cancel) { Invoke-UiaClick -Element $cancel }
        Start-Sleep -Milliseconds 1200

        # Belt and braces: a modal dialog left open would wedge the IDE for the operator, so never
        # leave this to the click alone.
        $still = @($desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $dlgCond) |
                   Where-Object { $_.Current.ProcessId -eq $ProcessId })
        if ($still.Count -gt 0) {
            foreach ($w in $still) {
                [void][DlgCloser]::PostMessage([IntPtr]$w.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
            }
            Start-Sleep -Seconds 2
            $still = @($desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $dlgCond) |
                       Where-Object { $_.Current.ProcessId -eq $ProcessId })
        }
        Check 'and it was closed again without attaching anything' ($still.Count -eq 0)
    }
}

""
"=== nothing was staged and nothing was sent ==="

$input = ById 'InputBox' 3000
$vp = $null; $val = ''
if ($input.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { $val = $vp.Current.Value }
Check 'the input box was left empty' ([string]::IsNullOrEmpty($val)) "value=`"$val`""

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
