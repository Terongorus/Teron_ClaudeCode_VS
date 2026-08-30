# FEAT-7 end to end against a REAL running Visual Studio experimental instance, with no model call.
#
# The claim being tested is not "the string appears in a list" - it is that turning the setting on
# in the real Tools > Options UI puts --fallback-model on the real command line of the real
# claude.exe the extension spawns, and that the CLI accepts it. Nothing short of a live instance
# can show that: the flag is assembled inside ClaudeCodeSession.Start, which spawns the process in
# the same breath, so there is no seam a headless harness can read it from.
#
# How it avoids costing anything: the CLI validates its flags at parse time and exits 1 on an
# unknown option (confirmed directly - `--fallback-model-typo` gives "error: unknown option"), and
# it emits its `init` event, which turns the chat status to "Ready", before any user message. So a
# session that reaches "Ready" has already proven the flag was accepted. No prompt is ever sent.
#
# SIDE EFFECT, and it is restored: this flips "Switch Models Automatically" on and back off again
# in the experimental hive, and closes and reopens the Claude Code tool window (the options are
# read once, in the control's Loaded handler, so nothing short of a reload picks up a change).
#
# Two UIA facts this depends on, both learned on 2026-08-30:
#   * VS 18's Tools > Options is a docked tool-window TAB, not a modal dialog. Waiting for a
#     top-level window named "Options" waits forever.
#   * Each setting row is a TreeItem carrying ValuePattern directly - the value is on the row
#     itself, not on a child editor control that only exists once the row is being edited.
param(
    [Parameter(Mandatory = $true)][int]$ProcessId
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
function Elems { $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond) }
function Named([string]$name) { Elems | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1 }

function Get-SettingValue([string]$name) {
    $item = Named $name
    if ($null -eq $item) { return $null }
    $vp = $null
    if ($item.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) { return $vp.Current.Value }
    return $null
}
function Set-SettingValue([string]$name, [string]$value) {
    $item = Named $name
    $vp = $null
    if (-not $item.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
        throw "setting '$name' carries no ValuePattern"
    }
    $vp.SetValue($value)
    Start-Sleep -Milliseconds 1500
}

# Reads the command line of the claude.exe the IDE itself spawned. Filtered by PARENT pid, never by
# name: the audit's standing rule, earned when a claude.exe matched by name turned out to belong to
# the operator's real VS Code rather than to anything under test.
function Get-SessionCommandLine([int]$timeoutSec = 25) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $p = Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId AND Name='claude.exe'"
        if ($p) { return @($p)[0] }
        Start-Sleep -Milliseconds 1000
    }
    return $null
}
function Restart-ChatToolWindow {
    $bar = Elems | Where-Object { $_.Current.Name -eq 'Claude Code' -and $_.Current.ClassName -eq 'ToolWindowTitleBar' } |
        Select-Object -First 1
    if ($bar) {
        $close = $bar.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.Name -like 'Close*' } | Select-Object -First 1
        if ($close) { Invoke-UiaClick -Element $close; Start-Sleep -Seconds 3 }
    }
    Invoke-VsMenuPath -ProcessId $ProcessId -Path @('View', 'Other Windows', 'Claude Code')
    Start-Sleep -Seconds 8
}

""
"=== the two settings are really on the page, with the defaults the code declares ==="
Invoke-VsMenuPath -ProcessId $ProcessId -Path @('Tools', 'Options...')
Start-Sleep -Seconds 4
$node = Elems | Where-Object { $_.Current.Name -eq 'Claude Code' -and $_.Current.ClassName -eq 'TreeViewItem' } |
    Select-Object -First 1
Check 'Tools > Options lists a "Claude Code" page' ($null -ne $node)
$si = $null
if ($node -and $node.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$si)) { $si.Select() }
Start-Sleep -Seconds 2
$general = Elems | Where-Object {
    $_.Current.Name -eq 'General' -and $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Hyperlink
} | Select-Object -First 1
if ($general) { Invoke-UiaClick -Element $general; Start-Sleep -Seconds 3 }

$originalToggle = Get-SettingValue 'Switch Models Automatically'
$originalTarget = Get-SettingValue 'Fallback Model'
Check 'the toggle is on the page' ($null -ne $originalToggle) "value='$originalToggle'"
Check 'the target is on the page' ($null -ne $originalTarget) "value='$originalTarget'"
Check 'it ships off by default, so no existing session changes behaviour' ($originalToggle -ceq 'False')
Check 'and it names a model, so turning it on is enough on its own' ($originalTarget -ceq 'haiku')
$defaultsGroup = Elems | Where-Object { $_.Current.Name -eq 'Defaults' } | Select-Object -First 1
Check 'both sit under the Defaults group with the other session defaults' ($null -ne $defaultsGroup)

""
"=== with the toggle off, the flag is absent from the real command line ==="
# Reload first. Whatever session happens to be running was started under whatever the setting was
# at the time, which is not necessarily what it is now - a previous run of this script, or a hand
# check, leaves a process behind that would be read as evidence about the current setting and is
# nothing of the kind. Only a session started after this point says anything.
Restart-ChatToolWindow
$before = Get-SessionCommandLine
Check 'the extension spawned a CLI process of its own' ($null -ne $before) `
    $(if ($before) { "pid=$($before.ProcessId)" } else { '' })
if (-not $before) { throw 'FAIL: no claude.exe under this IDE - nothing to compare.' }
Check 'no --fallback-model on it' (-not ($before.CommandLine -like '*--fallback-model*'))
# CONTROL: the same read must FIND a flag that is genuinely there, or "absent" proves nothing.
Check 'CONTROL - the same read finds a flag that is present' `
    ($before.CommandLine -like '*--permission-prompt-tool*')

""
"=== turn it on in the real Options UI, reload, and read the command line again ==="
Set-SettingValue 'Switch Models Automatically' 'True'
Check 'the UI took the change' ((Get-SettingValue 'Switch Models Automatically') -ceq 'True')
Restart-ChatToolWindow
$after = Get-SessionCommandLine
Check 'the reloaded control spawned a new CLI process' ($null -ne $after) `
    $(if ($after) { "pid=$($after.ProcessId)" } else { '' })
if ($after) {
    Check 'it is a different process, so the reload really happened' ($after.ProcessId -ne $before.ProcessId)
    Check '--fallback-model is now on the command line' ($after.CommandLine -like '*--fallback-model*')
    Check 'with the configured model as its argument' ($after.CommandLine -like "*--fallback-model $originalTarget*")
    # The CLI exits 1 on an unknown option before reading any input, and only prints its init event
    # - which is what turns the status to "Ready" - once it has parsed everything. So a live process
    # showing Ready is the CLI's own confirmation that it accepted the flag.
    Start-Sleep -Seconds 5
    $alive = $null -ne (Get-Process -Id $after.ProcessId -ErrorAction SilentlyContinue)
    Check 'the CLI accepted it rather than exiting on a parse error' $alive
    $texts = Elems | ForEach-Object { $_.Current.Name }
    Check 'and the session reached Ready, so its init event arrived' `
        (@($texts | Where-Object { $_ -eq 'Ready' }).Count -gt 0)
    Check 'no CLI error surfaced in the transcript' `
        (@($texts | Where-Object { $_ -like '*unknown option*' -or $_ -like '*error:*' }).Count -eq 0)
}

""
"=== put the setting back the way it was found ==="
$restored = $false
for ($i = 0; $i -lt 3 -and -not $restored; $i++) {
    if ($null -eq (Named 'Switch Models Automatically')) {
        Invoke-VsMenuPath -ProcessId $ProcessId -Path @('Tools', 'Options...')
        Start-Sleep -Seconds 4
    }
    try { Set-SettingValue 'Switch Models Automatically' $originalToggle } catch { }
    $restored = (Get-SettingValue 'Switch Models Automatically') -ceq $originalToggle
}
Check 'the toggle was restored to how this run found it' $restored "back to '$originalToggle'"

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
