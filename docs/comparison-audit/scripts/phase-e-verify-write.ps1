# Phase E, part 2: prove the "before" side of an APPLIED edit really comes from the CLI's own
# checkpoint store and not from reconstruction.
#
# The Edit path is ambiguous evidence. An Edit call carries both old_string and new_string, so a
# correct "before" could have been produced either by reading Claude Code's backup or by undoing
# the replacement against the working copy - the assertion passes either way and proves neither.
#
# A Write call is not ambiguous. It carries only the new contents; nothing in the call says what it
# overwrote, and ReverseApply returns null for it by construction. So if the tab still shows the
# previous contents on the left after a Write has been applied, SessionCheckpointStore is the only
# thing that could have supplied them.
#
# Run against the same instance immediately after phase-e-verify.ps1.
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$ScratchFile = 'D:\Projects\Visual Studio Projects\Test_Project_Claude\phase-e-scratch.txt'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'uia-lib.ps1')

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
# Retrying variant lives in uia-lib.ps1; see the note there on why waiting for text is not the
# same question as finding the control that text belongs to.
function Find-RowButton([string]$label) {
    return Find-InvokableByName -ProcessId $ProcessId -Label $label -TimeoutMs 8000
}

$scratchName = Split-Path -Leaf $ScratchFile
$priorContents = Get-Content $ScratchFile -Raw
"file before the Write: $($priorContents.Length) bytes, contains BRAVO = $($priorContents -like '*BRAVO*')"

$root = Get-MainWindowByPid -ProcessId $ProcessId
$box = Find-ByAutomationId -Root $root -AutomationId 'InputBox' -TimeoutMs 5000
Set-UiaValue -Element $box -Value "Use the Write tool to overwrite $scratchName so its entire contents are exactly the single line: CHARLIE. Do not read it first and do not use Edit."
Start-Sleep -Milliseconds 700
Invoke-UiaClick -Element (Find-ByAutomationId -Root $root -AutomationId 'SendButton' -TimeoutMs 5000)

if (Wait-For '1  Allow' 180) { Check 'approval card raised for the Write' $true }
else { Check 'approval card raised for the Write' $false }

$allow = Find-RowButton '1  Allow'
if ($allow) { Invoke-UiaClick -Element $allow } else { Check 'Allow invokable' $false }
Start-Sleep -Seconds 6

$now = Get-Content $ScratchFile -Raw
Check 'the Write landed on disk' ($now -like '*CHARLIE*' -and $now -notlike '*BRAVO*')
if (Wait-For 'Done' 180) { "  turn finished" } else { "  WARN  no result footer seen" }

# Collapse the Edit card from part 1 first. Every tool card carries its own control with the same
# AutomationId, so with both expanded a lookup by id would return the earlier card's button - which
# is exactly what happened on the first attempt at this script, producing a real diff of the wrong
# call and a failure that looked like a product bug.
$null = Expand-UiaByLabel -ProcessId $ProcessId -Label 'Edit file' -Collapse
Start-Sleep -Milliseconds 800

$expanded = Expand-UiaByLabel -ProcessId $ProcessId -Label 'Write file'
Check 'Write tool card expands' $expanded
Start-Sleep -Milliseconds 1500

$root = Get-MainWindowByPid -ProcessId $ProcessId
$btn = Find-ByAutomationId -Root $root -AutomationId 'ToolCallOpenDiffTab' -TimeoutMs 6000
Check '"Open diff tab" offered for a Write call' ($null -ne $btn)

if ($btn) {
    Invoke-UiaClick -Element $btn
    Start-Sleep -Seconds 3
    Check 'tab opened for the applied Write' (Has "[Claude Code] $scratchName")

    $tempRoot = Join-Path $env:TEMP 'TeronClaudeCodeVS-difftab'
    $b = @(Get-ChildItem $tempRoot -Recurse -Filter '*.before.*' | Sort-Object LastWriteTime -Descending)[0]
    $a = @(Get-ChildItem $tempRoot -Recurse -Filter '*.after.*' | Sort-Object LastWriteTime -Descending)[0]
    $lt = Get-Content $b.FullName -Raw
    $rt = Get-Content $a.FullName -Raw
    "  left  : $($lt -replace '\r?\n', ' / ')"
    "  right : $($rt -replace '\r?\n', ' / ')"

    # THE point of this script. Nothing in the Write call knows the old text.
    Check 'previous contents recovered for a Write (only the CLI backup can supply this)' `
        ($lt.Trim() -ceq $priorContents.Trim())
    Check 'after side is the newly written file' ($rt -like '*CHARLIE*')
}

& (Join-Path $here 'screenshot-composite.ps1') -ProcessId $ProcessId `
    -OutFile (Join-Path $OutDir '44-PhaseE-write-diff-tab.png') | Out-Null
"  screenshot: 44-PhaseE-write-diff-tab.png"

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
