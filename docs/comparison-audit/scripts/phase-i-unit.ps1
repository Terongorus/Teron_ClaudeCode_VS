# Phase I (FEAT-1) against the real built assembly. No IDE.
#
# What this covers, and why it is worth covering headlessly:
#
#  * The transcript reader. Its fixture is a REAL two-turn session captured from the real CLI
#    (see ../fixtures/README.md), chosen because it contains the two things that make a naive
#    reader wrong: tool-result relays that are also `user` records, and a second edit to a file the
#    CLI was already tracking. Everything asserted here was measured off that session before any of
#    FEAT-1 was written.
#  * The fork's command line. `--fork-session` and `--resume-session-at` are assembled inside
#    ClaudeCodeSession.Start, which spawns the process in the same breath - so the only way to read
#    them is to let it spawn a real claude.exe and ask Windows what it was started with. That is
#    what this does, in a scratch directory, sending nothing.
#  * Copy that came from baseline rather than from me, asserted verbatim.
#
# Rigor rule #6 throughout: an assertion that something is absent is paired with one that the same
# read finds it when it IS there.
param(
    [string]$BinDir   = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\bin\Debug\net481',
    [string]$Root     = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS',
    [string]$Fixtures = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\docs\comparison-audit\fixtures'
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

$asm         = [System.Reflection.Assembly]::LoadFrom((Join-Path $BinDir 'TeronClaudeCodeVS.dll'))
$storeType   = $asm.GetType('TeronClaudeCodeVS.ViewModels.SessionCheckpointStore', $true)
$pointType   = $asm.GetType('TeronClaudeCodeVS.ViewModels.RewindPoint', $true)
$sessionType = $asm.GetType('TeronClaudeCodeVS.Core.ClaudeCodeSession', $true)
$vmType      = $asm.GetType('TeronClaudeCodeVS.ViewModels.ChatSessionViewModel', $true)
"loaded: $($asm.GetName().Name) $($asm.GetName().Version)"

$NS = [System.Reflection.BindingFlags]'NonPublic,Public,Static'
$readPoints  = $storeType.GetMethod('ReadRewindPoints', $NS)
$describeAge = $pointType.GetMethod('DescribeAge', $NS)
$describeOut = $vmType.GetMethod('DescribeRewindOutcome', $NS)

# The fixture's own ids, read off the captured session at capture time.
$UUID_ALPHA  = 'e24a5a14-28be-4a63-afd2-80cd84635bd0'   # turn 1's prompt
$UUID_BETA   = 'b199b493-da1a-4c1e-8fff-912295764b54'   # turn 2's prompt
$UUID_ANCHOR = 'e6c53864-566c-43f2-b090-538ab1e4b9a6'   # turn 1's last assistant entry

$original = Join-Path $Fixtures 'rewind-session-original.jsonl'
$forked   = Join-Path $Fixtures 'rewind-session-forked.jsonl'
Check 'the captured original transcript is present' (Test-Path $original)
Check 'the captured forked transcript is present' (Test-Path $forked)

""
"=== the rewind points read out of a real transcript ==="
$now = [datetime]::UtcNow
$points = @($readPoints.Invoke($null, @([string]$original, [datetime]$now)))
Check 'two rewind points, one per real prompt' ($points.Count -eq 2) "got $($points.Count)"

# CONTROL: the file really does hold four `user` records, so "two" is a filter doing work and not
# an artefact of a short fixture.
$userRecords = @(Get-Content $original | Where-Object { $_ -like '*"type":"user"*' })
Check 'CONTROL - the transcript holds more user records than prompts' ($userRecords.Count -gt $points.Count) `
    "$($userRecords.Count) user records vs $($points.Count) prompts"

if ($points.Count -eq 2) {
    # Newest first, which is the order the picker shows.
    Check 'newest first' ($points[0].MessageUuid -ceq $UUID_BETA)
    Check 'oldest last'  ($points[1].MessageUuid -ceq $UUID_ALPHA)

    Check "the later prompt's fork anchor is the assistant entry before it" `
        ($points[0].ResumeAtUuid -ceq $UUID_ANCHOR)
    Check 'the first prompt has no anchor, so forking there means a new session' `
        ($null -eq $points[1].ResumeAtUuid)
    Check 'and it says so' ($points[1].IsFirstMessage -eq $true)
    Check 'CONTROL - the later prompt does not claim to be the first' ($points[0].IsFirstMessage -eq $false)

    Check 'ordinals count real prompts only, in conversation order' `
        (($points[1].UserOrdinal -eq 0) -and ($points[0].UserOrdinal -eq 1))

    Check 'the prompt text is the text that was typed' `
        ($points[0].PromptText -clike 'Now change note.txt*') "'$($points[0].PromptText)'"
    Check 'no tool-result relay leaked in as a prompt' `
        (-not ($points | Where-Object { $_.PromptText -clike '*tool_result*' }))
    Check 'every point carries a uuid to rewind to' `
        (-not ($points | Where-Object { [string]::IsNullOrEmpty($_.MessageUuid) }))
    Check 'every point carries a timestamp read from the record' `
        (-not ($points | Where-Object { $_.TimestampUtc -eq [datetime]::MinValue }))

    # A ListBoxItem with no AutomationProperties.Name falls back to ToString(). A live run found
    # the picker's rows announcing themselves as "TeronClaudeCodeVS.ViewModels.RewindPoint" to
    # anything reading the accessibility tree, which is what a screen reader would have read out.
    Check 'a rewind point announces itself as the prompt it stands for' `
        ($points[0].ToString() -ceq $points[0].PromptText)
    Check 'CONTROL - and not as its type name' ($points[0].ToString() -notlike '*RewindPoint*')
}

""
"=== the fork the CLI actually produced, checked against the original ==="
# Not a claim about the flag - a measurement of what came back when it was used. Both files are
# real CLI output; this asserts the relationship between them that FEAT-1 depends on.
function ChainUuids([string]$path) {
    Get-Content $path | ForEach-Object {
        if ($_ -match '"type":"(user|assistant)"' -and $_ -match '"uuid":"([0-9a-f-]{36})"') { $Matches[1] }
    }
}
$origChain = @(ChainUuids $original)
$forkChain = @(ChainUuids $forked)
$anchorAt  = $origChain.IndexOf($UUID_ANCHOR)
Check 'the anchor is in the original chain' ($anchorAt -ge 0) "index $anchorAt"
Check 'the fork keeps everything up to and including the anchor' `
    (($forkChain.Count -ge $anchorAt + 1) -and
     (-not (Compare-Object $origChain[0..$anchorAt] $forkChain[0..$anchorAt] -SyncWindow 0)))
Check 'the fork drops the turn that followed it' (-not ($forkChain -contains $UUID_BETA))
Check 'CONTROL - the original still has that turn, so nothing was rewritten in place' `
    ($origChain -contains $UUID_BETA)
Check 'the fork continues past the anchor with its own turn' ($forkChain.Count -gt $anchorAt + 1)

""
"=== relative ages, in baseline's own wording ==="
function Age([double]$seconds) {
    $t = [datetime]::UtcNow
    $describeAge.Invoke($null, @([datetime]$t.AddSeconds(-$seconds), [datetime]$t))
}
Check '5 seconds  -> just now' ((Age 5) -ceq 'just now')
Check '59 seconds -> just now' ((Age 59) -ceq 'just now')
Check '90 seconds -> 1m ago'   ((Age 90) -ceq '1m ago')
Check '2 hours    -> 2h ago'   ((Age 7200) -ceq '2h ago')
Check '3 days     -> 3d ago'   ((Age 259200) -ceq '3d ago')
Check 'the hour boundary is 60 minutes, not 59' ((Age 3540) -ceq '59m ago')

""
"=== the outcome wording, including the part that stops a count reading as data loss ==="
Check 'a clean rewind says so' ($describeOut.Invoke($null, @([int]0)) -ceq 'Code rewind successful')
$one = $describeOut.Invoke($null, @([int]1))
$two = $describeOut.Invoke($null, @([int]2))
Check 'one skipped file is singular' ($one -clike '*1 file was skipped*')
Check 'two skipped files are plural'  ($two -clike '*2 files were skipped*')
Check 'and the reason is spelled out rather than left as a number' `
    ($one -clike '*a link or other non-regular file*')

""
"=== the fork flags on a REAL command line ==="
# ClaudeCodeSession.Start spawns the process itself, so the args are read back off the spawned
# process rather than from any seam in our own code. Filtered by PARENT pid - the audit's standing
# rule, earned when a claude.exe matched by name turned out to be the operator's own VS Code.
$claude = "$env:USERPROFILE\.vscode\extensions\anthropic.claude-code-2.1.251-win32-x64\resources\native-binary\claude.exe"
$scratch = Join-Path $env:TEMP 'claude-phase-i-args'
if (-not (Test-Path $scratch)) { New-Item -ItemType Directory -Path $scratch | Out-Null }
$startMethod = $sessionType.GetMethod('Start')

function CommandLineFor([string]$resume, [bool]$fork, [string]$at) {
    $s = [System.Activator]::CreateInstance($sessionType)
    $startArgs = @(
        [string]$claude, [string]$scratch, [string]'haiku', $null,
        $(if ($resume) { [string]$resume } else { $null }), $null, $null, $null,
        [bool]$fork, $(if ($at) { [string]$at } else { $null })
    )
    $null = $startMethod.Invoke($s, $startArgs)
    $line = $null
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and -not $line) {
        $p = Get-CimInstance Win32_Process -Filter "ParentProcessId=$PID AND Name='claude.exe'" -ErrorAction SilentlyContinue
        if ($p) { $line = @($p)[0].CommandLine }
        else { Start-Sleep -Milliseconds 400 }
    }
    $s.Dispose()
    Start-Sleep -Milliseconds 800
    return $line
}

$plain = CommandLineFor '' $false ''
Check 'a plain start spawns a real CLI process' ($null -ne $plain)
if ($plain) {
    Check 'no --fork-session on it'      (-not ($plain -like '*--fork-session*'))
    Check 'no --resume-session-at on it' (-not ($plain -like '*--resume-session-at*'))
    # CONTROL: the same read finds a flag that IS there, so "absent" is a result and not a failure
    # to read the command line at all.
    Check 'CONTROL - the same read finds --permission-prompt-tool' ($plain -like '*--permission-prompt-tool*')
}

$forked1 = CommandLineFor $UUID_ALPHA $true $UUID_ANCHOR
Check 'a forking start spawns a real CLI process' ($null -ne $forked1)
if ($forked1) {
    Check '--resume names the session being forked' ($forked1 -like "*--resume $UUID_ALPHA*")
    Check '--fork-session is passed'                ($forked1 -like '*--fork-session*')
    Check '--resume-session-at names the anchor'    ($forked1 -like "*--resume-session-at $UUID_ANCHOR*")
}

# Neither flag means anything without --resume, and the CLI ignores them there; not emitting them
# keeps the command line honest about what the session actually is.
$noResume = CommandLineFor '' $true $UUID_ANCHOR
if ($noResume) {
    Check 'fork flags are withheld when there is no session to resume' `
        (-not (($noResume -like '*--fork-session*') -or ($noResume -like '*--resume-session-at*')))
}

""
"=== the markup ==="
$xaml = Get-Content (Join-Path $Root 'Core\ClaudeCodeChatControl.xaml') -Raw
$names = [regex]::Matches($xaml, 'x:Name="([A-Za-z0-9_]+)"') | ForEach-Object { $_.Groups[1].Value }
$refs  = [regex]::Matches($xaml, 'ElementName=([A-Za-z0-9_]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
foreach ($r in $refs) { Check "ElementName=$r is a declared x:Name" ($names -contains $r) }
Check 'CONTROL - the same test rejects a name that was never declared' (-not ($names -contains 'RewindPopupp'))

foreach ($n in @('RewindPopup', 'RewindConfirmPopup', 'MessageActionsPopup', 'RewindList')) {
    Check "the markup declares $n" ($names -contains $n)
}

# Baseline's copy, verbatim. Paraphrasing any of these would be a silent divergence from the thing
# the audit measured.
$verbatim = @(
    'Rewind to…',
    'Select a message to restore code and fork the conversation from that point.',
    'Fork conversation from here',
    'Rewind code to here',
    'Fork conversation and rewind code',
    'A new forked conversation will be created after rewinding.',
    'The code has not changed, so no code will be restored.',
    'Restore code and conversation to an earlier point'
)
foreach ($t in $verbatim) {
    Check "baseline's copy appears verbatim: '$t'" ($xaml -clike "*$t*")
}
Check "the CLI's own warning about manual edits is carried" `
    ($xaml -clike '*Rewinding does not affect files edited manually or via bash.*')
Check 'the confirmation is the one popup that does not close on an outside click' `
    ($xaml -match 'x:Name="RewindConfirmPopup"[^>]*(?s).{0,200}StaysOpen="True"')

# The empty state is the view model's, not the markup's - the markup binds it. Asserted where it
# actually lives, which is also the only place a typo in it could hide.
$vmInstance = [System.Activator]::CreateInstance($vmType)
Check "baseline's empty state is the string the panel binds" `
    ($vmInstance.RewindEmptyStateText -ceq 'No messages to rewind to yet.') `
    "'$($vmInstance.RewindEmptyStateText)'"
Check 'the picker starts closed and with nothing selected' `
    ((-not $vmInstance.IsRewindPickerVisible) -and ($null -eq $vmInstance.SelectedRewindPoint) -and
     (-not $vmInstance.HasSelectedRewindPoint))
Check 'the confirmation starts closed, and its button starts disabled' `
    ((-not $vmInstance.IsRewindConfirmVisible) -and (-not $vmInstance.CanConfirmRewind))
$vmInstance.Dispose()

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
