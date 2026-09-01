# Phase J (FEAT-8 voice, FEAT-9 sessions) end to end against a REAL Visual Studio experimental
# instance.
#
# WHAT THIS COSTS: one short Haiku turn, spent OUTSIDE the IDE - a background agent is started in
# the solution directory so that the Running tab has a row that is genuinely in the current folder
# and genuinely finished. Without it the tab would only ever show sessions from elsewhere, and
# "Open here" would never be seen enabled. Nothing is sent through the chat panel itself.
#
# SIDE EFFECTS, all cleaned up before it returns:
#   * a background agent is created and stopped (it stays in `claude agents --json --all` history,
#     which is the feature working rather than a leak);
#   * the chat panel is left on a resumed session, and a new session is started at the end;
#   * the microphone is opened for about a second while the dictation toggle is exercised.
#
# BACKGROUND-SAFE, like every live script here: UI Automation patterns only - Invoke, Value,
# SelectionItem. No SetForegroundWindow, no SendInput, no physical mouse movement. The IDE can sit
# behind whatever the user is actually doing.
#
# The three UIA facts this is built on are documented at the top of phase-i-live.ps1 and all three
# still apply: popup content is found from the desktop root by process id; a Popup has no automation
# peer, so openness is asked of an element INSIDE it; and a toggle re-clicked "to try harder" just
# closes what it opened, so every surface is opened by looking first.
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [string]$SolutionDir = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS',
    [string]$ClaudePath  = "$env:USERPROFILE\.local\bin\claude.exe"
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

function Snapshot { return @($desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond)) }
function SnapById($snap, [string]$id) { return @($snap | Where-Object { $_.Current.AutomationId -eq $id }) }
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
function Ensure-Open([string]$toggleId, [string]$innerId, [int]$attempts = 5) {
    for ($i = 0; $i -lt $attempts; $i++) {
        $snap = Snapshot
        if (SnapOne $snap $innerId) { return $snap }
        $toggle = SnapOne $snap $toggleId
        if ($toggle) { Invoke-UiaClick -Element $toggle }
        Start-Sleep -Milliseconds 700
    }
    return $null
}
function Set-Value($element, [string]$text) {
    $vp = $null
    if ($element -and $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$vp)) {
        $vp.SetValue($text); return $true
    }
    return $false
}

# ════════════════════════════════════════════════════════════════════════════════════════════════
""
"=== preflight: a background agent in the solution folder, so the Running tab has a real row ==="

# Native stderr is NOT an error here. The CLI prints "Starting background service…" to stderr as
# ordinary progress, and in PowerShell 5.1 each such line becomes an ErrorRecord whenever the
# stream is merged - which the *caller* may be doing, not just this script. With
# $ErrorActionPreference = 'Stop' that kills the run before a single check has executed. So every
# native call below is made with the preference relaxed, and it is put back afterwards.
$strict = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
# IN THE SOLUTION DIRECTORY, explicitly. A background agent inherits the shell's current directory,
# and the first run of this script created one in the scripts folder instead - which the panel then
# correctly refused to open, because it is not the folder the IDE has open. The check that followed
# fell through to whatever other row happened to be enabled and read an unrelated transcript.
Push-Location $SolutionDir
try {
    $bgOutput = (& $ClaudePath --bg --model haiku 'Reply with the single word PONG and nothing else.') | Out-String
} finally { Pop-Location }
$shortId = $null
if ($bgOutput -match 'backgrounded\s+.\s*([0-9a-f]{8})') { $shortId = $Matches[1] }
elseif ($bgOutput -match '([0-9a-f]{8})') { $shortId = $Matches[1] }
Check 'a background agent was started' ($null -ne $shortId) "id=$shortId"

# Let it finish its one turn, then stop it - a STOPPED agent in THIS folder is the row that makes
# "Open here" reachable, and a running one would (correctly) refuse.
Start-Sleep -Seconds 20
& $ClaudePath stop $shortId | Out-Null
Start-Sleep -Seconds 2

$agentsJson = (& $ClaudePath agents --json --all) | Out-String
$ErrorActionPreference = $strict
# NOT `@($json | ConvertFrom-Json)`. Piping a string in emits ONE object that is itself the
# array, so `$_` in a later Where-Object is the whole array; `$_.id -eq $shortId` then evaluates
# as an array filter, comes back non-empty, and every row "matches". The first run of this script
# reported a $mine holding all seven sessions with their names concatenated.
$agents = ConvertFrom-Json -InputObject $agentsJson
$matched = @($agents | Where-Object { $_.id -eq $shortId })
Check 'the CLI still lists it after stopping, with --all' ($matched.Count -eq 1) "matched=$($matched.Count)"
$mine = $matched[0]
Check 'and it no longer has a pid, so it counts as finished' ($null -eq $mine.pid) "pid=$($mine.pid)"
$bgName = $mine.name
$bgSessionId = $mine.sessionId
"  agent: '$bgName' ($shortId) session=$bgSessionId"

# ════════════════════════════════════════════════════════════════════════════════════════════════
""
"=== FEAT-8: the mic ==="

$snap = Snapshot
$mic = SnapOne $snap 'MicButton'
Check 'the mic button is in the composer' ($null -ne $mic)
Check 'it is enabled, because this machine has a recognizer' ($mic.Current.IsEnabled)
Check 'it announces itself as something other than its type' ($mic.Current.Name -eq 'Dictate') "'$($mic.Current.Name)'"

$helpText = $mic.Current.HelpText
Check 'its tooltip is baseline''s, verbatim, with the keybinding' `
    ($helpText -eq "Tap or hold to record · Ctrl+D") "'$helpText'"

Check 'nothing is being dictated yet' ($null -eq (SnapOne $snap 'VoiceStatusText'))

# Invoke rather than a mouse gesture: this is the keyboard/automation path, and a button that only
# answers to a mouse is one a screen reader cannot press. It is also the only press available to a
# background-safe script.
Invoke-UiaClick -Element $mic
Start-Sleep -Milliseconds 1200
$listening = Snapshot
$status = SnapOne $listening 'VoiceStatusText'
Check 'invoking it starts dictation, and the status line appears' ($null -ne $status)
if ($status) { "  status: '$($status.Current.Name)'" }

Invoke-UiaClick -Element (SnapOne $listening 'MicButton')
Start-Sleep -Milliseconds 1200
# CONTROL for the check above: the status line is not simply always present.
Check 'CONTROL - invoking it again stops dictation, and the line goes away' `
    ($null -eq (SnapOne (Snapshot) 'VoiceStatusText'))

# ════════════════════════════════════════════════════════════════════════════════════════════════
""
"=== FEAT-9: the history overlay's three tabs ==="

$snap = Ensure-Open 'HistoryButton' 'HistoryLocalTabButton'
Check 'the history overlay opens' ($null -ne $snap)

Check 'it has a Local tab' ($null -ne (SnapOne $snap 'HistoryLocalTabButton'))
Check 'a Running tab' ($null -ne (SnapOne $snap 'HistoryRunningTabButton'))
Check 'and a Cloud tab' ($null -ne (SnapOne $snap 'HistoryCloudTabButton'))
Check 'it opens on Local, so the search box is showing' ($null -ne (SnapOne $snap 'SessionSearchBox'))
Check 'and the Running pane is not' ($null -eq (SnapOne $snap 'AgentSessionsList'))

""
"=== FEAT-9: the Running tab, listing real sessions ==="

Invoke-UiaClick -Element (SnapOne $snap 'HistoryRunningTabButton')
Start-Sleep -Seconds 4
$snap = Snapshot

Check 'switching to Running shows the session list' ($null -ne (SnapOne $snap 'AgentSessionsList'))
# CONTROL: the Local pane really did go away, so the tabs are switching rather than stacking.
Check 'CONTROL - and hides the Local pane''s search box' ($null -eq (SnapOne $snap 'SessionSearchBox'))
Check 'it did not fail to read the CLI' ($null -eq (SnapOne $snap 'AgentSessionsError')) `
    "$(if (SnapOne $snap 'AgentSessionsError') { (SnapOne $snap 'AgentSessionsError').Current.Name })"
Check 'and it is not empty - the agent started above is a real running-session row' `
    ($null -eq (SnapOne $snap 'AgentSessionsEmptyState'))

$list = SnapOne $snap 'AgentSessionsList'
$rows = @()
if ($list) {
    $rows = @($list.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition))
}
Check 'the list has rows' ($rows.Count -gt 0) "count=$($rows.Count)"
Check 'the background agent started above is one of them' (SnapHas $snap $bgName) "looking for '$bgName'"

# ── the row's own actions ───────────────────────────────────────────────────────────────────────
$openHere = @(SnapById $snap 'OpenAgentSessionHereButton')
$terminal = @(SnapById $snap 'OpenAgentSessionTerminalButton')
Check 'every row offers Open here' ($openHere.Count -eq $rows.Count) "$($openHere.Count) vs $($rows.Count) rows"
Check 'and a terminal hand-off' ($terminal.Count -eq $rows.Count)

$enabledOpen = @($openHere | Where-Object { $_.Current.IsEnabled })
Check 'at least one row can be opened here - the finished agent in this folder' ($enabledOpen.Count -ge 1) `
    "enabled=$($enabledOpen.Count) of $($openHere.Count)"
# CONTROL: the button is not simply always enabled. The other rows are live sessions from another
# folder, and each one must say why it refuses rather than just being grey.
$disabledOpen = @($openHere | Where-Object { -not $_.Current.IsEnabled })
Check 'CONTROL - and at least one cannot, so the gate is doing work' ($disabledOpen.Count -ge 1) `
    "disabled=$($disabledOpen.Count)"
if ($disabledOpen.Count -ge 1) {
    $why = $disabledOpen[0].Current.HelpText
    Check 'a refused row explains itself rather than going quiet' `
        (($why -like '*running right now*') -or ($why -like '*was started in*')) "'$why'"
}

# ════════════════════════════════════════════════════════════════════════════════════════════════
""
"=== FEAT-9: the Cloud tab ==="

Invoke-UiaClick -Element (SnapOne $snap 'HistoryCloudTabButton')
Start-Sleep -Milliseconds 900
$snap = Snapshot

Check 'the Cloud tab shows its paste box' ($null -ne (SnapOne $snap 'CloudSessionBox'))
Check 'CONTROL - and the Running list is gone' ($null -eq (SnapOne $snap 'AgentSessionsList'))
Check 'the gap is stated on the tab itself' ($null -ne (SnapOne $snap 'CloudGapNote'))
$gap = (SnapOne $snap 'CloudGapNote').Current.Name
Check 'and it names both real limits' `
    (($gap -like '*no command that lists*') -and ($gap -like '*refuses the streaming output format*')) "'$gap'"

$openCloud = SnapOne $snap 'OpenCloudSessionButton'
Check 'the open button starts disabled, with nothing pasted' ($openCloud -and (-not $openCloud.Current.IsEnabled))

$hint = SnapOne $snap 'CloudHintText'
Check 'the empty box says what to paste' ($hint.Current.Name -like '*session_*') "'$($hint.Current.Name)'"

$null = Set-Value (SnapOne $snap 'CloudSessionBox') '00000000-0000-0000-0000-000000000000'
Start-Sleep -Milliseconds 700
$snap = Snapshot
Check 'a bare uuid is refused - the CLI does not accept those' `
    (-not (SnapOne $snap 'OpenCloudSessionButton').Current.IsEnabled)
Check 'and the hint says so in the CLI''s words' `
    ((SnapOne $snap 'CloudHintText').Current.Name -eq 'That is not a cloud session ID or URL.') `
    "'$((SnapOne $snap 'CloudHintText').Current.Name)'"

$null = Set-Value (SnapOne $snap 'CloudSessionBox') 'https://claude.ai/code/session_abc123'
Start-Sleep -Milliseconds 700
$snap = Snapshot
Check 'CONTROL - a real link enables the button, so the gate is not stuck off' `
    ((SnapOne $snap 'OpenCloudSessionButton').Current.IsEnabled)
Check 'and the hint says where it will open' `
    ((SnapOne $snap 'CloudHintText').Current.Name -like '*terminal*') "'$((SnapOne $snap 'CloudHintText').Current.Name)'"

# The button is deliberately NOT pressed. Doing so opens a terminal window, which would take the
# foreground away from whatever the user is doing, and the command it builds is unit-tested and was
# measured directly against the CLI (which answered with a real server-side rejection).
$null = Set-Value (SnapOne $snap 'CloudSessionBox') ''

# ════════════════════════════════════════════════════════════════════════════════════════════════
""
"=== FEAT-9 must-pass: opening a background session in this panel ==="

Invoke-UiaClick -Element (SnapOne (Snapshot) 'HistoryRunningTabButton')
Start-Sleep -Seconds 4
$snap = Snapshot

# Re-read the rows. The list was torn down and rebuilt by the trip through the Cloud tab, so the
# elements captured earlier are stale handles onto items that no longer exist - and a search
# through them finds nothing while the list on screen is perfectly correct.
$list = SnapOne $snap 'AgentSessionsList'
$rows = @()
if ($list) {
    $rows = @($list.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition))
}
Check 'the rebuilt list still has rows' ($rows.Count -gt 0) "count=$($rows.Count)"

# Press the Open here belonging to THE ROW FOR OUR OWN AGENT, found by name - not merely "the
# enabled one". Seven sessions are listed and more than one could become openable; resuming an
# unrelated transcript and then asserting against it is how the first run of this check produced a
# failure that said nothing about the feature.
$target = $null
foreach ($row in $rows) {
    $named = @($row.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.Name -eq $bgName })
    if ($named.Count -eq 0) { continue }
    $btn = @($row.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) |
        Where-Object { $_.Current.AutomationId -eq 'OpenAgentSessionHereButton' })
    if ($btn.Count -gt 0) { $target = $btn[0]; break }
}
Check 'the row for our own agent was found by name' ($null -ne $target) "'$bgName'"
Check 'and its Open here is enabled - it finished, and it is in this folder' `
    ($target -and $target.Current.IsEnabled)

if ($target -and $target.Current.IsEnabled) {
    Invoke-UiaClick -Element $target
    Start-Sleep -Seconds 5

    Check 'the history overlay closed, so the panel is back' ($null -eq (SnapOne (Snapshot) 'AgentSessionsList'))

    # A chat message renders into a FlowDocument, which UIA exposes with an EMPTY Name - a Name
    # sweep is structurally blind to it. Phase I learned this the expensive way; read the text.
    # Polled rather than read once: hydration reads the transcript off disk after the session starts.
    $docs = @()
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        $docs = @(Get-DocumentTexts -ProcessId $ProcessId)
        if (($docs -join "`n") -match '(?i)reply with the single word PONG') { break }
        Start-Sleep -Seconds 3
    }
    $all = ($docs -join "`n")
    for ($i = 0; $i -lt $docs.Count; $i++) { "  doc[$i] ($($docs[$i].Length) chars): '$($docs[$i] -replace "`n", ' ')'" }

    # The prompt and the answer are asserted SEPARATELY, per document. The agent's reply is the
    # single word PONG, which also appears inside the prompt - so "the transcript contains PONG"
    # passes whether or not the reply was ever hydrated, and an earlier version of this check did
    # exactly that. Requiring the word in a document that is NOT the prompt is what makes it real.
    $promptDoc = @($docs | Where-Object { $_ -match '(?i)reply with the single word PONG' })
    $answerDoc = @($docs | Where-Object { ($_ -notmatch '(?i)reply with') -and ($_.Trim() -match '(?i)^PONG[.!]?$') })

    Check 'the background agent''s own prompt is now in the transcript' `
        ($promptDoc.Count -ge 1) "docs=$($docs.Count) chars=$($all.Length)"
    Check 'and its ANSWER came back as a message of its own, not the prompt echoing the word' `
        ($answerDoc.Count -ge 1) "docs=$($docs.Count)"
    # CONTROL: that read is not matching anything and everything.
    Check 'CONTROL - and text that was never in this session is not' `
        (-not ($all -match 'a phrase that was never sent to any session'))
}

# ════════════════════════════════════════════════════════════════════════════════════════════════
""
"=== cleanup ==="

$snap = Snapshot
if (SnapOne $snap 'AgentSessionsList') { Invoke-UiaClick -Element (SnapOne $snap 'CloseHistoryButton') }
$newSession = SnapOne (Snapshot) 'NewSessionButton'
if ($newSession) { Invoke-UiaClick -Element $newSession; Start-Sleep -Seconds 3 }
Check 'the panel is back on a fresh session' ($null -ne (ByIdAnywhere 'InputBox' 6000))

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
