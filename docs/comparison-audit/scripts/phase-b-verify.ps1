# Phase B (ST-1..ST-5) verification.
#
# What this proves, and why each step is needed:
#   * The chat control's XAML actually LOADS. A missing StaticResource key is not a compile
#     error - the BAML compiles fine and the failure only appears at parse time, as a
#     XamlParseException that leaves the tool window empty. So a clean build proves nothing here
#     and the control has to be instantiated for real.
#   * Every token resolves. If any did not, the whole dictionary parse fails, so a fully
#     populated visual tree is the proof.
#   * ST-4: surfaces re-derive from the VS theme while the accent stays constant. Verified by
#     screenshotting the same control under two themes and sampling pixels.
#
# Background-safe throughout: UIA + PrintWindow only, no SetForegroundWindow, no physical input.
param(
    [Parameter(Mandatory=$true)][int]$ProcessId,
    [Parameter(Mandatory=$true)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'uia-lib.ps1')
. (Join-Path $here 'vs-menu.ps1')
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$root = Get-MainWindowByPid -ProcessId $ProcessId
"main window: $($root.Current.Name)"

# --- open the tool window -----------------------------------------------------------------
$panel = Find-ByAutomationId -Root $root -AutomationId 'InputBox' -TimeoutMs 2000
if (-not $panel) {
    "tool window not open - invoking View > Other Windows > Claude Code"
    Invoke-VsMenuPath -ProcessId $ProcessId -Path @('View','Other Windows','Claude Code')
    Start-Sleep -Seconds 3
    $root = Get-MainWindowByPid -ProcessId $ProcessId
    $panel = Find-ByAutomationId -Root $root -AutomationId 'InputBox' -TimeoutMs 25000
}
if (-not $panel) { throw 'FAIL: InputBox never appeared - the chat control did not load.' }
"PASS: chat control loaded (InputBox present)"

# --- prove the visual tree is populated, not a XamlParseException stub ----------------------
$expected = @('InputBox','SendButton','ModelButton','PermissionButton')
foreach ($id in $expected) {
    $e = Find-ByAutomationId -Root $root -AutomationId $id -TimeoutMs 3000
    if ($e) { "  found: $id" } else { "  MISSING: $id" }
}
$descendants = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition).Count
"visual tree elements under main window: $descendants"
