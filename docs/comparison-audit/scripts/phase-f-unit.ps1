# Phase F (FEAT-3) verification, without Visual Studio.
#
# FEAT-3 is a read of somebody else's file format, so the only verification worth anything runs
# against REAL transcripts - the ones on this machine, in the shapes that actually occur - and not
# against fixtures written to match my own reading of the format. The real assembly is reached by
# reflection out of bin\Debug\net481, so what is exercised is the code that ships.
#
# Every check states the value it consumed and could have failed on the opposite value; the
# precedence and revision cases are chosen specifically because a wrong-but-plausible rule
# (last record wins, first title wins) gives a DIFFERENT answer on them. What this harness cannot
# reach is listed at the bottom rather than left implied.
#
# Needs no VS instance, opens no window, and takes no focus.
param(
    [string]$BinDir = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\bin\Debug\net481',
    [string]$ProjectsRoot = (Join-Path $env:USERPROFILE '.claude\projects')
)
$ErrorActionPreference = 'Stop'

$script:pass = 0
$script:fail = 0
$script:skip = 0
function Check([string]$label, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; "  PASS  $label $detail" }
    else { $script:fail++; "  FAIL  $label $detail" }
}
# A fixture that has been deleted since must never look like a pass. These transcripts are real
# user data, not committed fixtures, so absence is possible and has to be reported as absence.
function Skip([string]$label, [string]$why) { $script:skip++; "  SKIP  $label - $why" }

# See phase-e-unit.ps1 for why this handler is shaped the way it is (re-entrancy guard, resource
# assemblies declined, already-loaded assemblies returned rather than loaded twice).
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
        $candidate = Join-Path $BinDir "$simple.dll"
        if (Test-Path $candidate) { return [System.Reflection.Assembly]::LoadFrom($candidate) }
        return $null
    }
    finally { [void]$script:resolving.Remove($simple) }
}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve($onResolve)
$null = [System.Reflection.Assembly]::LoadFrom((Join-Path $BinDir 'Newtonsoft.Json.dll'))

$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $BinDir 'TeronClaudeCodeVS.dll'))
$reader = $asm.GetType('TeronClaudeCodeVS.ViewModels.SessionTitleReader', $true)
$store = $asm.GetType('TeronClaudeCodeVS.ViewModels.SessionHistoryStore', $true)
$entryType = $asm.GetType('TeronClaudeCodeVS.ViewModels.SessionHistoryEntry', $true)
"loaded: $($asm.GetName().Name) $($asm.GetName().Version)  ($BinDir)"

$FLAGS = [System.Reflection.BindingFlags]'NonPublic,Public,Static'
function Call($type, [string]$name, [object[]]$callArgs) {
    $m = $type.GetMethod($name, $FLAGS)
    if ($null -eq $m) { throw "no such method: $($type.Name).$name" }
    $bound = New-Object 'object[]' $callArgs.Length
    for ($i = 0; $i -lt $callArgs.Length; $i++) {
        $a = $callArgs[$i]
        if ($a -is [System.Management.Automation.PSObject]) { $bound[$i] = $a.BaseObject } else { $bound[$i] = $a }
    }
    if ($bound.Length -ne $m.GetParameters().Length) {
        throw "$name expects $($m.GetParameters().Length) arguments, got $($bound.Length)"
    }
    return $m.Invoke($null, $bound)
}

# SessionTitleReader.Result and SessionHistoryStore.TitleUpdate: one is nested, the other internal,
# and PowerShell's object adapter does not expose members of a non-public type - a plain $x.Title
# comes back empty rather than raising, which is precisely the harness bug that reads as a product
# bug (see rule 7 in the live-verification rigor notes). Go through reflection instead.
function Prop($obj, [string]$name) {
    if ($null -eq $obj) { return $null }
    $o = if ($obj -is [System.Management.Automation.PSObject]) { $obj.BaseObject } else { $obj }
    $p = $o.GetType().GetProperty($name)
    if ($null -eq $p) { throw "no property $name on $($o.GetType().FullName)" }
    return $p.GetValue($o)
}
# Normalises whatever a reflected call hands back into a plain array. It has to cope with three
# shapes because PowerShell unrolls an enumerable on the way out of a function: a List<T> of two or
# more comes back as object[], a List<T> of one comes back as the bare element (which is not
# IEnumerable and used to blow up here), and an empty one comes back as $null. All three have to
# count correctly, or "no update" and "one update" become indistinguishable.
# Every return here carries the leading comma, because PowerShell unrolls an array on the way out
# of a function too - without it a one-element result arrives at the caller as the bare element and
# the very next Count check reads 1 as "a scalar", or 0 as "nothing happened".
function AsList($value) {
    if ($null -eq $value) { return ,@() }
    $v = if ($value -is [System.Management.Automation.PSObject]) { $value.BaseObject } else { $value }
    if ($v -isnot [System.Collections.IEnumerable] -or $v -is [string]) { return ,@($v) }
    $out = New-Object System.Collections.ArrayList
    foreach ($item in [System.Collections.IEnumerable]$v) { [void]$out.Add($item) }
    return ,$out.ToArray()
}

# ─── Ground truth, computed independently of the code under test ────────────────────────────────
# Deliberately NOT the same algorithm: a full forward scan, no tail window, no JObject, and it
# returns the last ai-title and the last custom-title SEPARATELY rather than applying a precedence
# rule. The precedence itself is then asserted in the checks, where a wrong rule shows up as a
# wrong answer instead of being baked into both sides.
function Truth([string]$path) {
    $ai = $null; $custom = $null; $aiFirst = $null; $aiCount = 0; $customCount = 0
    foreach ($line in [System.IO.File]::ReadLines($path)) {
        if ($line.Length -gt 2048 -or -not $line.StartsWith('{"type":"')) { continue }
        if ($line.StartsWith('{"type":"ai-title"')) {
            $o = ConvertFrom-Json $line
            if ($o.aiTitle) { if ($null -eq $aiFirst) { $aiFirst = [string]$o.aiTitle }; $ai = [string]$o.aiTitle; $aiCount++ }
        }
        elseif ($line.StartsWith('{"type":"custom-title"')) {
            $o = ConvertFrom-Json $line
            if ($o.customTitle) { $custom = [string]$o.customTitle; $customCount++ }
        }
    }
    return [pscustomobject]@{ Ai = $ai; AiFirst = $aiFirst; Custom = $custom; AiCount = $aiCount; CustomCount = $customCount }
}

function Fixture([string]$relative) {
    $p = Join-Path $ProjectsRoot $relative
    if (Test-Path $p) { return $p }
    return $null
}

# Real transcripts on this machine, each picked for the shape it exercises. Sizes are from
# 2026-08-29; they only grow.
$fxRevised  = Fixture 'd--Projects-Visual-Studio-Projects-Teron-Extensions\1bb4112b-f6a0-4156-8a3f-d540ac208f92.jsonl'   # 2.4 MB, ai revised, no custom
$fxCustom   = Fixture 'd--Projects-Visual-Studio-Projects-Teron-Applications\7fa8d213-48bc-4c86-9dd7-6d7132719c69.jsonl' # 1.6 MB, custom set, LATER ai
$fxSmallAi  = Fixture 'C--Program-Files-Microsoft-Visual-Studio-18-Community-Common7-IDE\67e8b7cd-9d8a-4856-a7ba-4d53002e296d.jsonl' # 27 KB, single ai
$fxNoTitle  = Fixture 'D--Projects-Visual-Studio-Projects-Test-Project-Claude\61df1c7e-5b1c-4dd1-8974-ef4303b3bef2.jsonl' # 20 KB, no title records
$fxHuge     = Fixture 'd--Projects-Visual-Studio-Projects-Teron-Extensions\19440230-dcab-4414-b21a-13d2ac1669e8.jsonl'    # 45 MB

""
"=== ReadFile: precedence, on transcripts where a wrong rule gives a different answer ==="

if (-not $fxCustom) { Skip 'custom-title beats a later ai-title' 'fixture transcript is gone' }
else {
    $t = Truth $fxCustom
    $r = Call $reader 'ReadFile' @([string]$fxCustom)
    "  fixture: $(Split-Path $fxCustom -Leaf)  ($([math]::Round((Get-Item $fxCustom).Length/1MB,1)) MB, $($t.AiCount) ai / $($t.CustomCount) custom records)"
    "  truth:   last custom = '$($t.Custom)'"
    "  truth:   last ai     = '$($t.Ai)'   <- this is the LAST title record in the file"
    "  reader:  '$(Prop $r 'Title')' (isCustom=$(Prop $r 'IsCustom'))"
    Check 'the two candidates genuinely differ, so this case discriminates' ($t.Custom -cne $t.Ai)
    Check 'custom-title wins even though an ai-title was written after it' ((Prop $r 'Title') -ceq $t.Custom)
    Check 'and it is reported as user-assigned' ((Prop $r 'IsCustom') -eq $true)
}

if (-not $fxRevised) { Skip 'the revised generated title wins' 'fixture transcript is gone' }
else {
    $t = Truth $fxRevised
    $r = Call $reader 'ReadFile' @([string]$fxRevised)
    "  fixture: $(Split-Path $fxRevised -Leaf)  ($($t.AiCount) ai / $($t.CustomCount) custom records)"
    "  truth:   first ai = '$($t.AiFirst)'"
    "  truth:   last  ai = '$($t.Ai)'"
    "  reader:  '$(Prop $r 'Title')' (isCustom=$(Prop $r 'IsCustom'))"
    Check 'first and last generated titles genuinely differ, so this case discriminates' ($t.AiFirst -cne $t.Ai)
    Check 'the CLI revised its title and the latest one is taken' ((Prop $r 'Title') -ceq $t.Ai)
    Check 'a generated title is not reported as user-assigned' ((Prop $r 'IsCustom') -eq $false)
}

""
"=== ReadFile: the small-file path (no tail truncation) ==="
if (-not $fxSmallAi) { Skip 'single ai-title in a small transcript' 'fixture transcript is gone' }
else {
    $t = Truth $fxSmallAi
    $r = Call $reader 'ReadFile' @([string]$fxSmallAi)
    "  fixture: $(Split-Path $fxSmallAi -Leaf)  ($((Get-Item $fxSmallAi).Length) bytes - smaller than the 1 MB window, so no seek happens)"
    "  truth:   '$($t.Ai)'   reader: '$(Prop $r 'Title')'"
    Check 'a whole-file read still finds the title' ((Prop $r 'Title') -ceq $t.Ai)
}

if (-not $fxNoTitle) { Skip 'transcript with no title records' 'fixture transcript is gone' }
else {
    $t = Truth $fxNoTitle
    $r = Call $reader 'ReadFile' @([string]$fxNoTitle)
    Check 'the fixture really has no title records, so null is the right answer' (($t.AiCount + $t.CustomCount) -eq 0) "counted $($t.AiCount)/$($t.CustomCount)"
    Check 'a transcript with no title yields null, not a guess' ($null -eq $r)
}

$r = Call $reader 'ReadFile' @([string](Join-Path $env:TEMP 'no-such-transcript-4a1f.jsonl'))
Check 'a missing file yields null rather than throwing' ($null -eq $r)

""
"=== ReadFile: the tail window, and the full-scan fallback behind it ==="
$scratch = Join-Path $env:TEMP 'teron-phase-f'
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

# A title at the very START of a file larger than the 1 MB window: the tail read cannot see it, so
# the only way this passes is if the fallback full scan runs. Filler lines are long enough to be
# rejected by the length gate, which is also what a real transcript's content lines look like.
$farAway = Join-Path $scratch 'title-before-the-window.jsonl'
$sw = New-Object System.IO.StreamWriter($farAway, $false, (New-Object System.Text.UTF8Encoding($false)))
$sw.WriteLine('{"type":"ai-title","aiTitle":"Buried far from the end","sessionId":"synthetic"}')
$filler = '{"type":"assistant","message":{"content":"' + ('x' * 4000) + '"}}'
for ($i = 0; $i -lt 600; $i++) { $sw.WriteLine($filler) }   # ~2.4 MB after the title
$sw.Dispose()
$len = (Get-Item $farAway).Length
$r = Call $reader 'ReadFile' @([string]$farAway)
Check 'the synthetic file is genuinely past the 1 MB window' ($len -gt 1MB) "$([math]::Round($len/1MB,1)) MB, title at byte 0"
Check 'a title behind the tail window is still found (fallback ran)' ((Prop $r 'Title') -ceq 'Buried far from the end') "got '$(Prop $r 'Title')'"

# The same file with the title moved into the window: same answer, different path through the code.
$nearEnd = Join-Path $scratch 'title-inside-the-window.jsonl'
$sw = New-Object System.IO.StreamWriter($nearEnd, $false, (New-Object System.Text.UTF8Encoding($false)))
for ($i = 0; $i -lt 600; $i++) { $sw.WriteLine($filler) }
$sw.WriteLine('{"type":"ai-title","aiTitle":"Inside the window","sessionId":"synthetic"}')
$sw.Dispose()
$r = Call $reader 'ReadFile' @([string]$nearEnd)
Check 'a title inside the tail window is found without a full scan' ((Prop $r 'Title') -ceq 'Inside the window') "got '$(Prop $r 'Title')'"

# Non-ASCII immediately before the end, so the 1 MB seek lands mid-character on a multi-byte one.
# The decode must not throw and the title after it must still parse.
$multibyte = Join-Path $scratch 'multibyte-boundary.jsonl'
$sw = New-Object System.IO.StreamWriter($multibyte, $false, (New-Object System.Text.UTF8Encoding($false)))
$wide = '{"type":"assistant","message":{"content":"' + ('é' * 2000) + '"}}'
for ($i = 0; $i -lt 600; $i++) { $sw.WriteLine($wide) }
$sw.WriteLine('{"type":"custom-title","customTitle":"Survived a split character","sessionId":"synthetic"}')
$sw.Dispose()
$r = Call $reader 'ReadFile' @([string]$multibyte)
Check 'a seek landing mid-character does not break the read' ((Prop $r 'Title') -ceq 'Survived a split character') "got '$(Prop $r 'Title')'"

""
"=== ReadFile: lines that look like titles but are not ==="
$decoys = Join-Path $scratch 'decoys.jsonl'
# Assistant text that quotes the record shape - the exact false positive a substring match makes.
# Built on its own line, and this is not style: inside an @(...) literal, `'a' + ('x' * 400), 'b'`
# binds the + across the whole comma list, so the array collapses to ONE concatenated string. The
# first version of this test wrote a single-line file, the reader correctly found no title in it,
# and two checks failed against a product that was behaving properly.
$decoyContent = '{"type":"assistant","message":{"content":"the CLI writes {\"type\":\"ai-title\",\"aiTitle\":\"WRONG ANSWER\"} per turn, ' + ('padding ' * 400) + '"}}'
$lines = @(
    $decoyContent
    '{"type":"ai-title","aiTitle":"   ","sessionId":"synthetic"}'          # whitespace-only
    '{"type":"ai-title","aiTitle":"Real title  ","sessionId":"synthetic"}' # trailing space, trimmed
    '{"type":"ai-title" BROKEN JSON'                                       # unparseable
    ''                                                                     # blank
    '{"type":"custom-title","customTitle":"","sessionId":"synthetic"}'     # empty custom title
)
if ($lines.Count -ne 6) { throw "decoy array collapsed to $($lines.Count) element(s)" }
[System.IO.File]::WriteAllLines($decoys, [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
$r = Call $reader 'ReadFile' @([string]$decoys)
"  reader: '$(Prop $r 'Title')' (isCustom=$(Prop $r 'IsCustom'))"
Check 'a long content line quoting the record shape is not mistaken for one' ((Prop $r 'Title') -cne 'WRONG ANSWER')
Check 'an empty custom-title does not win over a real generated one' ((Prop $r 'IsCustom') -eq $false)
Check 'blank and malformed lines are skipped, and the title is trimmed' ((Prop $r 'Title') -ceq 'Real title')

# No trailing newline on the last line - the shape a transcript has while it is being written.
$unterminated = Join-Path $scratch 'no-trailing-newline.jsonl'
[System.IO.File]::WriteAllText($unterminated, '{"type":"ai-title","aiTitle":"Last line unterminated","sessionId":"synthetic"}', (New-Object System.Text.UTF8Encoding($false)))
$r = Call $reader 'ReadFile' @([string]$unterminated)
Check 'a final line with no newline is still read' ((Prop $r 'Title') -ceq 'Last line unterminated') "got '$(Prop $r 'Title')'"

""
"=== ReadFile: cost on the largest transcript here ==="
if (-not $fxHuge) { Skip 'large-transcript cost' 'fixture transcript is gone' }
else {
    $mb = [math]::Round((Get-Item $fxHuge).Length / 1MB, 1)
    $t0 = [Diagnostics.Stopwatch]::StartNew(); $r = Call $reader 'ReadFile' @([string]$fxHuge); $t0.Stop()
    $t1 = [Diagnostics.Stopwatch]::StartNew(); $truth = Truth $fxHuge; $t1.Stop()
    "  $mb MB: reader $($t0.ElapsedMilliseconds) ms   vs. a full scan $($t1.ElapsedMilliseconds) ms"
    "  reader: '$(Prop $r 'Title')'   truth: custom='$($truth.Custom)' ai='$($truth.Ai)'"
    $expected = if ($truth.Custom) { $truth.Custom } else { $truth.Ai }
    Check 'the right title comes back from a 45 MB transcript' ((Prop $r 'Title') -ceq $expected)
    Check 'and it costs a fraction of a full scan (the tail window is doing its job)' ($t0.ElapsedMilliseconds -lt $t1.ElapsedMilliseconds)
}

""
"=== Read: the cwd-to-transcript mapping, not just a path ==="
if (-not $fxRevised) { Skip 'Read(workingDirectory, sessionId)' 'fixture transcript is gone' }
else {
    $sid = [IO.Path]::GetFileNameWithoutExtension($fxRevised)
    $r = Call $reader 'Read' @([string]'d:\Projects\Visual Studio Projects\Teron_Extensions', [string]$sid)
    Check 'a cwd plus a session id resolves to that transcript' ((Prop $r 'Title') -ceq (Truth $fxRevised).Ai) "got '$(Prop $r 'Title')'"
    $r = Call $reader 'Read' @([string]'d:\Projects\Visual Studio Projects\Teron_Extensions', [string]'00000000-0000-0000-0000-000000000000')
    Check 'an unknown session id yields null' ($null -eq $r)
    $r = Call $reader 'Read' @([string]'', [string]$sid)
    Check 'an empty working directory yields null' ($null -eq $r)
}

""
"=== ComputeTitleUpdates: what the history list actually consumes ==="
$listType = [System.Collections.Generic.List``1].MakeGenericType($entryType)
function NewEntry([string]$sessionId, [string]$cwd, [string]$title, [bool]$userTitle, [string]$stamp) {
    $e = [Activator]::CreateInstance($entryType)
    $entryType.GetProperty('SessionId').SetValue($e, $sessionId)
    $entryType.GetProperty('WorkingDirectory').SetValue($e, $cwd)
    $entryType.GetProperty('Title').SetValue($e, $title)
    $entryType.GetProperty('HasUserTitle').SetValue($e, $userTitle)
    $entryType.GetProperty('TitleStamp').SetValue($e, $stamp)
    return $e
}
function Updates([object[]]$entries) {
    $list = [Activator]::CreateInstance($listType)
    # [void] is load-bearing: MethodInfo.Invoke on a VOID method returns $null, and PowerShell
    # emits that $null into the enclosing function's output stream. Without it this function
    # returned one $null per entry ahead of the real result - and because $null.Count is 0 in
    # PowerShell, the "no update expected" checks then passed for entirely the wrong reason.
    foreach ($e in $entries) { [void]$listType.GetMethod('Add').Invoke($list, @($e)) }
    # The leading comma is load-bearing, same trap as phase-e-unit.ps1's J(): a List<T> is
    # enumerable, so @($list) hands reflection the list's ELEMENTS and the call fails claiming a
    # SessionHistoryEntry cannot be converted to IEnumerable<SessionHistoryEntry> - which reads as
    # a signature problem rather than as the marshalling mistake it is.
    $result = AsList (Call $store 'ComputeTitleUpdates' @(,$list))
    if ($result -isnot [object[]]) { throw "Updates produced $($result.GetType().FullName), not an array" }
    return ,$result   # keep the array intact through the return, so Count means what it says
}

if (-not $fxRevised) { Skip 'ComputeTitleUpdates' 'fixture transcript is gone' }
else {
    $cwd = 'd:\Projects\Visual Studio Projects\Teron_Extensions'
    $sid = [IO.Path]::GetFileNameWithoutExtension($fxRevised)
    $real = (Truth $fxRevised).Ai

    $stale = NewEntry $sid $cwd 'Read the meta-procedure file and tell me…' $false ''
    $u = Updates @($stale)
    $newTitle = if ($u.Count -eq 1) { Prop $u[0] 'Title' } else { '<no update>' }
    $stamp = if ($u.Count -eq 1) { Prop $u[0] 'Stamp' } else { '' }
    "  truncated first message -> '$newTitle'   stamp '$stamp'"
    Check 'a stale truncated title is replaced by the generated one' ($newTitle -ceq $real)
    Check 'and the read is stamped so the next refresh can skip the file' ($stamp -match '^\d+:\d+$')

    # Same entry, now carrying that stamp: the transcript has not changed, so nothing is re-read.
    $cached = NewEntry $sid $cwd 'anything' $false $stamp
    Check 'an unchanged transcript is not read again' ((Updates @($cached)).Count -eq 0)

    $bogusStamp = NewEntry $sid $cwd 'anything' $false '1:1'
    Check 'a stamp that no longer matches does cause a re-read' ((Updates @($bogusStamp)).Count -eq 1)

    $renamed = NewEntry $sid $cwd 'A name I typed myself' $true ''
    Check 'a row the user renamed here is left alone' ((Updates @($renamed)).Count -eq 0)

    $already = NewEntry $sid $cwd $real $false ''
    $u = Updates @($already)
    Check 'a row already showing the current title reports no title change' ($u.Count -eq 1 -and $null -eq (Prop $u[0] 'Title'))
    Check 'but is still stamped, so it is not re-read every time' ($u.Count -eq 1 -and (Prop $u[0] 'Stamp') -match '^\d+:\d+$')

    $missing = NewEntry '00000000-0000-0000-0000-000000000000' $cwd 'Untitled' $false ''
    Check 'a session with no transcript on disk produces no update' ((Updates @($missing)).Count -eq 0)

    $mixed = Updates @($renamed, $missing, $stale)
    Check 'a mixed batch returns exactly the one row that could change' ($mixed.Count -eq 1)
}

""
"=== Load: the sessions.json already on disk still deserializes ==="
# The two new fields are additive, but the file that exists now predates them, and a rename that
# silently reset every row to defaults would be a data-loss regression of the kind this project has
# shipped before. Read-only: the real file is deserialized, never written.
$sessionsPath = Join-Path $env:APPDATA 'TeronClaudeCodeVS\sessions.json'
if (-not (Test-Path $sessionsPath)) { Skip 'existing sessions.json' 'no history file on this machine yet' }
else {
    $json = [System.IO.File]::ReadAllText($sessionsPath)
    $rows = AsList ([Newtonsoft.Json.JsonConvert]::DeserializeObject($json, $listType))
    "  $($rows.Count) rows in $sessionsPath"
    Check 'the pre-Phase-F history file loads' ($rows.Count -gt 0)
    $bad = @($rows | Where-Object { [string]::IsNullOrEmpty($entryType.GetProperty('SessionId').GetValue($_)) })
    Check 'every row keeps its session id' ($bad.Count -eq 0) "$($bad.Count) row(s) lost it"
    $flagged = @($rows | Where-Object { $entryType.GetProperty('HasUserTitle').GetValue($_) })
    Check 'rows written before the flag existed default to not-user-renamed' ($flagged.Count -eq 0) "$($flagged.Count) row(s) came back flagged"
    $stamped = @($rows | Where-Object { -not [string]::IsNullOrEmpty($entryType.GetProperty('TitleStamp').GetValue($_)) })
    Check 'and to an empty stamp, so they get read once' ($stamped.Count -eq 0) "$($stamped.Count) row(s) came back stamped"

    # What FEAT-3 is worth on the real history, printed rather than asserted - it depends on which
    # sessions this machine happens to have.
    $would = 0
    foreach ($row in $rows) {
        $res = Call $reader 'Read' @([string]$entryType.GetProperty('WorkingDirectory').GetValue($row), [string]$entryType.GetProperty('SessionId').GetValue($row))
        if ($res -and (Prop $res 'Title') -cne $entryType.GetProperty('Title').GetValue($row)) { $would++ }
    }
    "  $would of $($rows.Count) real rows would get a better title on the next history open"
}

Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue

""
"=== not reached by this harness ==="
"  - ChatSessionViewModel.BeginRefreshSessionTitles / ApplySessionTitleUpdates, including the"
"    rename-during-a-refresh race: covered by phase-f-vm.ps1, which constructs the real view model"
"    on this thread and pumps the dispatcher itself. Run both."
"  - The overlay itself: that the ListBox row repaints when Title changes is a binding fact, and"
"    only a live instance shows it."
"  - IOException / UnauthorizedAccessException paths: no way to produce a locked or unreadable"
"    transcript here without leaving one behind."
""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail    skipped: $script:skip"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" }
elseif ($script:skip -gt 0) { "  RESULT: passed, but $script:skip fixture(s) were missing" }
else { "  RESULT: all checks passed" }
