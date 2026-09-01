# Phase J (FEAT-8 voice, FEAT-9 sessions) against the real built assembly. No IDE.
#
# The two things worth saying about what this covers:
#
#  * DICTATION IS ACTUALLY EXERCISED, not stubbed. A sentence is synthesised to a .wav and fed
#    through VoiceInput's real pipeline - the same SpeechRecognitionEngine, the same
#    DictationGrammar, the same event plumbing the microphone path uses, with one line different
#    (SetInputToWaveFile instead of SetInputToDefaultAudioDevice). A test that mocked the engine
#    would proved only that the mock works, and nobody can speak into a headless run.
#  * THE SESSION PARSER IS FED REAL CLI OUTPUT, in two captures rather than one, because the field
#    set changes with the session's state: a background agent has `pid`/`status` while alive and
#    neither once stopped. See ../fixtures/README.md.
#
# Rigor rule #6 throughout: every assertion that something is absent, rejected or empty is paired
# with one proving the same read finds it when it IS there.
param(
    [string]$BinDir   = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\bin\Debug\net481',
    [string]$Root     = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS',
    [string]$Fixtures = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\comparison-audit\fixtures'
)
$ErrorActionPreference = 'Stop'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$script:pass = 0
$script:fail = 0
function Check([string]$label, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; "  PASS  $label $detail" }
    else { $script:fail++; "  FAIL  $label $detail" }
}

# ─── Load the real assembly ─────────────────────────────────────────────────────────────────────
$script:probeDirs = @(
    Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.vssdk.buildtools" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName 'tools\vssdk\bin\lib' } |
        Where-Object { Test-Path $_ }
)
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
        foreach ($dir in @($BinDir) + $script:probeDirs) {
            $candidate = Join-Path $dir "$simple.dll"
            if (Test-Path $candidate) { return [System.Reflection.Assembly]::LoadFrom($candidate) }
        }
        return $null
    }
    finally { [void]$script:resolving.Remove($simple) }
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($onResolve)
$null = [System.Reflection.Assembly]::LoadFrom((Join-Path $BinDir 'Newtonsoft.Json.dll'))
Add-Type -AssemblyName System.Speech

$asm        = [System.Reflection.Assembly]::LoadFrom((Join-Path $BinDir 'TeronClaudeCodeVS.dll'))
$agentsType = $asm.GetType('TeronClaudeCodeVS.ViewModels.AgentSessionsViewModel', $true)
$entryType  = $asm.GetType('TeronClaudeCodeVS.ViewModels.AgentSessionEntry', $true)
$voiceType  = $asm.GetType('TeronClaudeCodeVS.Core.VoiceInput', $true)
$availType  = $asm.GetType('TeronClaudeCodeVS.Core.VoiceAvailability', $true)
$vmType     = $asm.GetType('TeronClaudeCodeVS.ViewModels.ChatSessionViewModel', $true)
"loaded: $($asm.GetName().Name) $($asm.GetName().Version)"

$NS       = [System.Reflection.BindingFlags]'NonPublic,Public,Static'
$parse    = $agentsType.GetMethod('Parse', $NS)
$normId   = $agentsType.GetMethod('NormalizeCloudId', $NS)
$sameDir  = $agentsType.GetMethod('IsSameFolder', $NS)
$describe = $agentsType.GetMethod('DescribeCloudInput', $NS)
$probe    = $voiceType.GetMethod('Probe', $NS)

$THIS_FOLDER = 'd:\Projects\Visual Studio Projects\Teron_Extensions'
$BG_FOLDER   = 'C:\Users\kkole\AppData\Local\Temp\claude\d--Projects-Visual-Studio-Projects-Teron-Extensions\a0084635-226d-4e83-a751-65bdbaa155fd\scratchpad\bgtest'

$liveJson    = Join-Path $Fixtures 'agents-live-background.json'
$stoppedJson = Join-Path $Fixtures 'agents-all.json'
Check 'the live-background capture is present' (Test-Path $liveJson)
Check 'the post-stop capture is present' (Test-Path $stoppedJson)

# ════════════════════════════════════════════════════════════════════════════════════════════════
""
"=== FEAT-9: the session list, parsed from real CLI output ==="

$now  = [datetime]::UtcNow
$live = @($parse.Invoke($null, @([string](Get-Content $liveJson -Raw), [string]$THIS_FOLDER, [datetime]$now)))
Check 'three sessions parsed from the live capture' ($live.Count -eq 3) "got $($live.Count)"

$bgLive = $live | Where-Object { $_.Kind -eq 'background' }
Check 'exactly one of them is a background agent' (@($bgLive).Count -eq 1)
Check 'the background agent carries the short id the CLI commands take' ($bgLive.ShortId -eq 'e6e765fd') "got '$($bgLive.ShortId)'"
Check 'its generated name is the CLI''s, not a truncated prompt' ($bgLive.Name -eq 'reply to pong') "got '$($bgLive.Name)'"
Check 'while alive it reports a pid, so IsRunning is true' ($bgLive.IsRunning -and $bgLive.Pid -eq 25328) "pid=$($bgLive.Pid)"
Check 'and a status, which only a live background agent has' ($bgLive.Status -eq 'idle') "got '$($bgLive.Status)'"
Check 'its state is carried through verbatim' ($bgLive.State -eq 'done') "got '$($bgLive.State)'"

$interactive = @($live | Where-Object { $_.Kind -eq 'interactive' })
Check 'the two interactive sessions have no short id' `
    (($interactive.Count -eq 2) -and ($null -eq $interactive[0].ShortId) -and ($null -eq $interactive[1].ShortId))
# CONTROL for the line above: the same read DOES find a short id when the JSON has one.
Check 'CONTROL - and the background one does, so "no short id" is a real distinction' ($null -ne $bgLive.ShortId)
Check 'nor a status or state' (($null -eq $interactive[0].Status) -and ($null -eq $interactive[0].State))

# ─── the same agent, after `claude stop` ────────────────────────────────────────────────────────
""
"=== FEAT-9: the same agent after it was stopped ==="

$stopped   = @($parse.Invoke($null, @([string](Get-Content $stoppedJson -Raw), [string]$THIS_FOLDER, [datetime]$now)))
$bgStopped = $stopped | Where-Object { $_.Kind -eq 'background' }
Check 'it is still listed - that is what --all is for' ($null -ne $bgStopped)
Check 'it is the same session' ($bgStopped.SessionId -eq $bgLive.SessionId)
Check 'but it no longer reports a pid, so IsRunning is false' ((-not $bgStopped.IsRunning) -and ($null -eq $bgStopped.Pid))
Check 'and its status is gone, while state survives' (($null -eq $bgStopped.Status) -and ($bgStopped.State -eq 'done'))
# This pair is the whole reason there are two fixtures: pid and status are optional in fact, not in
# theory, and a parser that required either would pass the live capture and fail this one.
Check 'CONTROL - the live capture of the SAME agent did report both' `
    (($null -ne $bgLive.Pid) -and ($null -ne $bgLive.Status))

""
"=== FEAT-9: what each row is allowed to do ==="

Check 'a live interactive session cannot be opened here - something is running it' (-not $interactive[0].CanOpenHere)
Check 'and it says so, naming the pid' `
    ($interactive[0].OpenHereBlockedReason -like "*running right now (pid $($interactive[0].Pid))*") `
    "'$($interactive[0].OpenHereBlockedReason)'"
Check 'and it offers no terminal command either - nothing joins a live interactive session' `
    (($null -eq $interactive[0].TerminalArgs) -and (-not $interactive[0].CanOpenInTerminal))
Check 'CONTROL - a LIVE BACKGROUND agent does offer one, via the CLI''s own attach' `
    (($bgLive.CanOpenInTerminal) -and ($bgLive.TerminalArgs[0] -eq 'attach') -and ($bgLive.TerminalArgs[1] -eq 'e6e765fd'))
Check 'a stopped agent is resumed the ordinary way instead' `
    (($bgStopped.TerminalArgs[0] -eq '--resume') -and ($bgStopped.TerminalArgs[1] -eq $bgStopped.SessionId))
Check 'the terminal tooltip names the command and the directory' `
    (($bgStopped.TerminalCommandText -like '*claude --resume*') -and ($bgStopped.TerminalCommandText -like "*$BG_FOLDER*")) `
    "'$($bgStopped.TerminalCommandText)'"

Check 'a stopped agent from ANOTHER folder still cannot be opened here' (-not $bgStopped.CanOpenHere)
Check 'and the reason given is the folder, not the pid' `
    (($bgStopped.OpenHereBlockedReason -like '*was started in*') -and ($bgStopped.OpenHereBlockedReason -notlike '*pid*')) `
    "'$($bgStopped.OpenHereBlockedReason)'"

# CONTROL for both of the above: re-parse the SAME capture as though the IDE were open on the
# agent's own folder. Nothing about the row changes except the one fact being tested.
$rehomed   = @($parse.Invoke($null, @([string](Get-Content $stoppedJson -Raw), [string]$BG_FOLDER, [datetime]$now)))
$bgRehomed = $rehomed | Where-Object { $_.Kind -eq 'background' }
Check 'CONTROL - open the IDE on ITS folder and the same stopped agent CAN be opened here' `
    ($bgRehomed.CanOpenHere -and ($null -eq $bgRehomed.OpenHereBlockedReason))
Check 'CONTROL - and it is the same row, not a different one' ($bgRehomed.SessionId -eq $bgStopped.SessionId)

""
"=== FEAT-9: ordering and folder matching ==="

Check 'sessions in the open folder sort ahead of the rest' `
    ($stopped[0].IsCurrentFolder -and $stopped[1].IsCurrentFolder -and (-not $stopped[2].IsCurrentFolder))
Check 'and within a folder the newest is first' ($stopped[0].StartedUtc -ge $stopped[1].StartedUtc)
# CONTROL: the newest row overall is the background one, and it is NOT first - so the folder rule
# is genuinely outranking the time rule rather than the two agreeing by luck.
Check 'CONTROL - the newest session overall is last, because it is in another folder' `
    (($stopped[2].StartedUtc -gt $stopped[0].StartedUtc) -and ($stopped[2].Name -eq 'reply to pong'))

Check 'folder matching ignores case' ([bool]$sameDir.Invoke($null, @([string]'D:\Projects\X', [string]'d:\projects\x')))
Check 'folder matching ignores a trailing separator' ([bool]$sameDir.Invoke($null, @([string]'d:\a\b\', [string]'d:\a\b')))
Check 'folder matching ignores separator style' ([bool]$sameDir.Invoke($null, @([string]'d:/a/b', [string]'d:\a\b')))
Check 'CONTROL - genuinely different folders do not match' (-not [bool]$sameDir.Invoke($null, @([string]'d:\a\b', [string]'d:\a\c')))
Check 'an empty path never matches' (-not [bool]$sameDir.Invoke($null, @([string]'', [string]'d:\a')))

Check 'a startedAt is read as epoch milliseconds, not seconds' `
    ($bgLive.StartedUtc.Year -eq ([datetimeoffset]::FromUnixTimeMilliseconds(1788205201603)).UtcDateTime.Year)
Check 'the relative age uses the same wording as the rewind picker' `
    ($bgLive.RelativeAge -match '^(just now|\d+[mhd] ago)$') "'$($bgLive.RelativeAge)'"

Check 'a row announces itself by name, not by type - see RewindPoint.ToString' `
    ($bgLive.ToString() -eq 'reply to pong') "'$($bgLive.ToString())'"
Check 'the detail line reads as one sentence fragment' `
    ($bgLive.DetailLine -like 'background*done*ago') "'$($bgLive.DetailLine)'"

Check 'empty output parses to an empty list rather than throwing' `
    (@($parse.Invoke($null, @([string]'[]', [string]$THIS_FOLDER, [datetime]$now))).Count -eq 0)

""
"=== FEAT-9: the cloud id rule, transcribed from the CLI's own validator ==="

# The CLI accepted all four of these forms when they were passed to --cloud: it got past its own
# "is not a cloud session ID or URL" check on every one, and failed later for a different reason.
Check 'a session_ id is accepted' ($normId.Invoke($null, @([string]'session_abc123')) -eq 'session_abc123')
Check 'a cse_ id is accepted' ($normId.Invoke($null, @([string]'cse_abc123')) -eq 'cse_abc123')
Check 'a claude.ai/code link is reduced to its id' `
    ($normId.Invoke($null, @([string]'https://claude.ai/code/session_abc123')) -eq 'session_abc123')
Check 'a cse_ link too' ($normId.Invoke($null, @([string]'https://claude.ai/code/cse_abc123')) -eq 'cse_abc123')
Check 'surrounding whitespace is tolerated, because pasting adds it' `
    ($normId.Invoke($null, @([string]"  session_abc123`n")) -eq 'session_abc123')

# The rejections. The first is the one that matters: a plain uuid LOOKS like a session id and is
# not one - the CLI said so in as many words when it was handed one.
Check 'a bare uuid is rejected - the CLI refuses those' `
    ($null -eq $normId.Invoke($null, @([string]'00000000-0000-0000-0000-000000000000')))
Check 'an untagged word is rejected' ($null -eq $normId.Invoke($null, @([string]'abc123')))
Check 'a prefix with nothing after it is rejected' ($null -eq $normId.Invoke($null, @([string]'session_')))
Check 'characters outside [A-Za-z0-9_-] are rejected' ($null -eq $normId.Invoke($null, @([string]'session_abc!123')))
Check 'empty input is rejected' ($null -eq $normId.Invoke($null, @([string]'')))
Check 'a link with no path is rejected' ($null -eq $normId.Invoke($null, @([string]'https://claude.ai/')))

Check 'the empty box explains what to paste' `
    ($describe.Invoke($null, @([string]'')) -like '*session_*cse_*claude.ai/code*') "'$($describe.Invoke($null, @([string]'')))'"
Check 'a valid id says where it will open, and why not here' `
    ($describe.Invoke($null, @([string]'session_abc123')) -like '*terminal*cannot stream into this panel*')
Check 'an invalid one borrows the CLI''s own sentence' `
    ($describe.Invoke($null, @([string]'nope')) -eq 'That is not a cloud session ID or URL.')

# ════════════════════════════════════════════════════════════════════════════════════════════════
""
"=== FEAT-8: is dictation possible on this machine at all ==="

$availability = $probe.Invoke($null, @())
Check 'the probe answers without throwing' ($null -ne $availability)
"  recognizer: $($availability.RecognizerName)"
Check 'this machine has a recognizer, so the rest of this section is meaningful' `
    ($availability.IsAvailable) "reason='$($availability.Reason)'"
Check 'an available probe carries no reason' ($null -eq $availability.Reason)

# CONTROL: the unavailable shape is not hypothetical - construct one and prove it reads differently.
$unavailable = $availType.GetMethod('Unavailable', $NS).Invoke($null, @([string]'no recognizer here'))
Check 'CONTROL - an unavailable probe is not available and does carry a reason' `
    ((-not $unavailable.IsAvailable) -and ($unavailable.Reason -eq 'no recognizer here'))

""
"=== FEAT-8: recognition, through the real pipeline ==="

$wav = Join-Path $env:TEMP 'phase-j-dictation.wav'
$syn = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $syn.SetOutputToWaveFile($wav)
    $syn.Rate = -1
    $syn.Speak('please add a unit test for the login page')
} finally { $syn.Dispose() }
Check 'a spoken sentence was synthesised to a wave file' ((Test-Path $wav) -and ((Get-Item $wav).Length -gt 1000))

# Drive VoiceInput exactly as the mic path does, differing only in where the audio comes from.
# Two techniques were tried here before this one, and both failed in ways worth recording because
# neither looked like a failure:
#   * Register-ObjectEvent with an -Action block. The block runs in its own scope, so `$script:`
#     inside it is not this script's `$script:` - the harness looked like it was collecting results
#     and collected nothing, reporting a working feature as broken.
#   * A scriptblock cast to [EventHandler[string]]. VoiceInput raises its events on a recognition
#     worker thread, and invoking a PowerShell scriptblock from a thread with no runspace took the
#     whole process down with a StackOverflowException.
# Subscribing with NO -Action queues each event for the caller to drain, which is the one form that
# is safe across threads and keeps the reading on this thread.
$voice = [System.Activator]::CreateInstance($voiceType)
Register-ObjectEvent -InputObject $voice -EventName TextRecognized -SourceIdentifier VoiceText | Out-Null
Register-ObjectEvent -InputObject $voice -EventName ListeningChanged -SourceIdentifier VoiceListen | Out-Null

$startError = $voice.StartFromWaveFile($wav)
Check 'VoiceInput started on the wave file' ($null -eq $startError) "$startError"

$deadline = (Get-Date).AddSeconds(30)
while (((Get-Date) -lt $deadline) -and (-not (Get-Event -SourceIdentifier VoiceText -ErrorAction SilentlyContinue))) {
    Start-Sleep -Milliseconds 200
}
$voice.Stop()
Start-Sleep -Milliseconds 500

# SourceArgs is [sender, args]; for EventHandler<string> the second element is the recognised text.
$heard = @(Get-Event -SourceIdentifier VoiceText -ErrorAction SilentlyContinue | ForEach-Object { $_.SourceArgs[1] })
$listened = @(Get-Event -SourceIdentifier VoiceListen -ErrorAction SilentlyContinue | Where-Object { $_.SourceArgs[1] -eq $true })
$recognized = ($heard -join ' ')
"  recognised: '$recognized'"
Check 'it raised ListeningChanged when it started' ($listened.Count -gt 0)
Check 'it recognised something' ($heard.Count -gt 0)
Check 'and what it recognised is the sentence that was spoken' `
    ($recognized -match '(?i)add a unit test for the login page') "'$recognized'"
$voice.Dispose()
Unregister-Event -SourceIdentifier VoiceText -ErrorAction SilentlyContinue
Unregister-Event -SourceIdentifier VoiceListen -ErrorAction SilentlyContinue

# CONTROL: the same pipeline on silence recognises nothing. Without this, "it recognised the
# sentence" could be a harness that reports success for any audio at all.
$silentWav = Join-Path $env:TEMP 'phase-j-silence.wav'
$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, 'Sixteen', 'Mono')
$syn2 = New-Object System.Speech.Synthesis.SpeechSynthesizer
try {
    $syn2.SetOutputToWaveFile($silentWav, $fmt)
    $syn2.Speak((New-Object System.Speech.Synthesis.PromptBuilder))
} finally { $syn2.Dispose() }

$voice2 = [System.Activator]::CreateInstance($voiceType)
Register-ObjectEvent -InputObject $voice2 -EventName TextRecognized -SourceIdentifier VoiceSilence | Out-Null
$null = $voice2.StartFromWaveFile($silentWav)
Start-Sleep -Seconds 3
$voice2.Stop(); $voice2.Dispose()
$heard2 = @(Get-Event -SourceIdentifier VoiceSilence -ErrorAction SilentlyContinue | ForEach-Object { $_.SourceArgs[1] })
Unregister-Event -SourceIdentifier VoiceSilence -ErrorAction SilentlyContinue
Check 'CONTROL - the same pipeline hears nothing in silence' ($heard2.Count -eq 0) "got '$($heard2 -join ' ')'"

Remove-Item $wav, $silentWav -ErrorAction SilentlyContinue

""
"=== FEAT-8 / FEAT-9: what the view model exposes ==="

$vm = New-Object $vmType
Check 'the mic is disabled until it has been probed' (-not $vm.IsVoiceAvailable)
Check 'and says so rather than staying silent' ($vm.VoiceTooltipText -like '*has not been checked*') "'$($vm.VoiceTooltipText)'"
$vm.ProbeVoiceAvailability()
Check 'after probing, the mic is available on this machine' ($vm.IsVoiceAvailable)
Check 'and the tooltip is baseline''s, verbatim' `
    ($vm.VoiceTooltipText -eq "Tap or hold to record · Ctrl+D") "'$($vm.VoiceTooltipText)'"

Check 'dictation starts off' (-not $vm.IsDictating)
Check 'and shows no status line while it is off' ((-not $vm.HasVoiceStatus) -and ($vm.VoiceStatusText -eq ''))
$vm.IsDictating = $true
Check 'listening with nothing heard yet says so' ($vm.VoiceStatusText -eq '🎤 Listening…') "'$($vm.VoiceStatusText)'"
$vm.VoiceHypothesis = 'add a unit'
Check 'and the running guess replaces it once there is one' ($vm.VoiceStatusText -eq '🎤 add a unit') "'$($vm.VoiceStatusText)'"
$vm.IsDictating = $false
Check 'CONTROL - and the status line disappears again when it stops' (-not $vm.HasVoiceStatus)

Check 'history opens on the Local tab' ($vm.IsLocalTab -and (-not $vm.IsRunningTab) -and (-not $vm.IsCloudTab))
$vm.SelectedHistoryTab = [System.Enum]::Parse($vmType.GetNestedType('HistoryTab'), 'Running')
Check 'selecting Running deselects the others' ($vm.IsRunningTab -and (-not $vm.IsLocalTab) -and (-not $vm.IsCloudTab))
$vm.SelectedHistoryTab = [System.Enum]::Parse($vmType.GetNestedType('HistoryTab'), 'Cloud')
Check 'and so does selecting Cloud' ($vm.IsCloudTab -and (-not $vm.IsRunningTab))

Check 'the cloud button is disabled until something valid is pasted' (-not $vm.CanOpenCloudSession)
$vm.CloudSessionInput = 'not-an-id'
Check 'and stays disabled for something invalid' (-not $vm.CanOpenCloudSession)
$vm.CloudSessionInput = 'https://claude.ai/code/session_abc123'
Check 'CONTROL - and enables for a real link, so the gate is doing work' ($vm.CanOpenCloudSession)
Check 'the hint tracks what was typed' ($vm.CloudHintText -like '*terminal*') "'$($vm.CloudHintText)'"

Check 'the running-session list starts empty and unloaded' `
    (($vm.AgentSessions.Sessions.Count -eq 0) -and (-not $vm.AgentSessions.HasLoaded))
Check 'an unloaded list is not "empty" - those are different states' (-not $vm.AgentSessions.IsEmpty)
$vm.Dispose()

""
"=== the XAML says what the code says ==="

$xaml = Get-Content (Join-Path $Root 'Core\ClaudeCodeChatControl.xaml') -Raw
$code = Get-Content (Join-Path $Root 'Core\ClaudeCodeChatControl.xaml.cs') -Raw

Check 'the mic button exists and is reachable by automation' ($xaml -match 'AutomationId="MicButton"')
Check 'its enabled state is bound, not hard-coded' ($xaml -match 'IsEnabled="\{Binding IsVoiceAvailable\}"')
Check 'its tooltip comes from the view model, so it can be the reason when disabled' `
    ($xaml -match 'ToolTip="\{Binding VoiceTooltipText\}"')
Check 'Ctrl+D is handled in the composer''s key handler' ($code -match 'e\.Key == Key\.D && Keyboard\.Modifiers == ModifierKeys\.Control')
Check 'tap and hold are told apart by a real threshold' ($code -match 'MicTapThreshold')
# A Button whose only handlers are mouse handlers cannot be pressed by a keyboard, a screen reader
# or UI Automation. Both paths have to exist, and the flag is what stops them cancelling each other.
Check 'the mic also has a Click handler, so it works without a mouse' ($xaml -match 'Click="OnMicClicked"')
Check 'and the two paths are kept from fighting' ($code -match '_micGestureHandled')
Check 'all three history tabs are present' `
    (($xaml -match 'AutomationId="HistoryLocalTabButton"') -and
     ($xaml -match 'AutomationId="HistoryRunningTabButton"') -and
     ($xaml -match 'AutomationId="HistoryCloudTabButton"'))
Check 'the running pane has a list, an empty state and an error slot' `
    (($xaml -match 'AutomationId="AgentSessionsList"') -and
     ($xaml -match 'AutomationId="AgentSessionsEmptyState"') -and
     ($xaml -match 'AutomationId="AgentSessionsError"'))
Check 'the empty state in the XAML is the one the view model declares' `
    ($xaml -match [regex]::Escape($agentsType::EmptyStateText))
Check 'the cloud pane states the gap rather than hiding it' ($xaml -match 'AutomationId="CloudGapNote"')
Check 'and the gap note names both real limits' `
    (($xaml -match 'no command that lists your cloud sessions') -and ($xaml -match 'refuses the streaming output format'))

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
