# Phase E (FEAT-2) branch coverage, without Visual Studio.
#
# phase-e-verify.ps1 drives the two paths a user actually takes and proves they work end to end.
# It does NOT reach several branches, because reaching them needs inputs a live session will not
# produce on demand: the CLI writes a backup for every edit, so the reverse-reconstruction fallback
# never runs; the model does not emit `replace_all` on request; and no temp directory is ever a day
# old during a test. Those branches shipped unexecuted, which is the same as untested.
#
# This closes them against the REAL built assembly - bin\Debug\net481\TeronClaudeCodeVS.dll,
# reached by reflection - rather than against a copy of the logic pasted into a test project. A
# copy would prove the algorithm and say nothing about the code that ships.
#
# Needs no VS instance, opens no window, and takes no focus.
param(
    [string]$BinDir = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\bin\Debug\net481'
)
$ErrorActionPreference = 'Stop'

$script:pass = 0
$script:fail = 0
function Check([string]$label, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; "  PASS  $label $detail" }
    else { $script:fail++; "  FAIL  $label $detail" }
}

# The VSIX's own dependencies sit beside it; the SDK reference assemblies it was compiled against
# are resolved out of the same folder. Without this, loading VsDiffTab fails the moment its static
# Dictionary<string, IVsWindowFrame> field has to be constructed.
#
# Three guards, each for a failure this handler actually hit. Already-loaded assemblies are
# returned rather than loaded a second time, because a duplicate identity makes later type checks
# fail in ways that look like logic bugs. Resource lookups are declined outright - the runtime asks
# for a satellite assembly per culture and none exist here. And a re-entrancy set is essential:
# LoadFrom raises AssemblyResolve for the dependencies of what it is loading, so an assembly that
# cannot be found asks for itself forever and takes the process down with a StackOverflow, which is
# exactly what the first run of this script did.
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

# Loaded up front rather than left to the handler above: PowerShell resolves a type name like
# [Newtonsoft.Json.Linq.JObject] against assemblies already in the AppDomain and never raises
# AssemblyResolve for it, so the handler alone is not enough to construct the tool inputs below.
$null = [System.Reflection.Assembly]::LoadFrom((Join-Path $BinDir 'Newtonsoft.Json.dll'))

$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $BinDir 'TeronClaudeCodeVS.dll'))
$diff = $asm.GetType('TeronClaudeCodeVS.Core.VsDiffTab', $true)
$store = $asm.GetType('TeronClaudeCodeVS.ViewModels.SessionCheckpointStore', $true)
"loaded: $($asm.GetName().Name) $($asm.GetName().Version)"

$FLAGS = [System.Reflection.BindingFlags]'NonPublic,Public,Static'
# Not named $args: that is an automatic variable in PowerShell, and a parameter of that name is
# silently overwritten by the function's own argument array, which surfaces as a parameter-count
# mismatch from Invoke rather than as anything resembling the actual mistake.
function Call($type, [string]$name, [object[]]$callArgs) {
    $m = $type.GetMethod($name, $FLAGS)
    if ($null -eq $m) { throw "no such method: $($type.Name).$name" }

    # Unwrap PSObject before reflection sees it: Invoke binds by exact type and would otherwise
    # reject a JObject that is in fact the right type, merely wrapped.
    #
    # Built by index into a fixed-size array rather than with +=, and that is not style. A JObject
    # is enumerable, so `$bound += $jobject` appends its PROPERTIES as separate elements - three
    # arguments silently become four, and reflection reports a parameter-count mismatch that looks
    # like the method signature is wrong rather than the marshalling.
    $bound = New-Object 'object[]' $callArgs.Length
    for ($i = 0; $i -lt $callArgs.Length; $i++) {
        $a = $callArgs[$i]
        if ($a -is [System.Management.Automation.PSObject]) { $bound[$i] = $a.BaseObject }
        else { $bound[$i] = $a }
    }
    if ($bound.Length -ne $m.GetParameters().Length) {
        throw "$name expects $($m.GetParameters().Length) arguments, got $($bound.Length)"
    }
    return $m.Invoke($null, $bound)
}

# Builds one tool-call input, by round-tripping through JSON rather than by poking at a JObject
# from PowerShell. Two attempts at the direct route failed in ways worth remembering: the indexer
# assignment silently does nothing through PowerShell's object adapter, so the tests ran against an
# empty input and several of them "passed" while exercising nothing; and .Count on the wrapped
# object reads as empty, so the guard meant to catch that could not see it either. Parsing a JSON
# string is the one construction path with no adapter in the middle.
#
# The leading comma is load-bearing: a JObject is IEnumerable and PowerShell unrolls enumerables on
# the way out of a function, handing the caller an array of properties instead of the object.
function J([hashtable]$fields) {
    $json = if ($fields.Count -eq 0) { '{}' } else { ConvertTo-Json -InputObject $fields -Compress -Depth 5 }
    if ($fields.Count -gt 0 -and $json -eq '{}') { throw 'J produced an empty object' }
    return ,[Newtonsoft.Json.Linq.JObject]::Parse($json)
}

""
"=== ApplyForward: what the file becomes ==="
$r = Call $diff 'ApplyForward' @('Edit', (J @{ old_string = 'ALPHA'; new_string = 'BRAVO' }), "x ALPHA y ALPHA z")
Check 'Edit replaces only the first occurrence by default' ($r -ceq 'x BRAVO y ALPHA z') "got '$r'"

$r = Call $diff 'ApplyForward' @('Edit', (J @{ old_string = 'ALPHA'; new_string = 'BRAVO'; replace_all = $true }), "x ALPHA y ALPHA z")
Check 'replace_all rewrites every occurrence' ($r -ceq 'x BRAVO y BRAVO z') "got '$r'"

$r = Call $diff 'ApplyForward' @('Edit', (J @{ old_string = ''; new_string = "brand new`nfile" }), '')
Check 'empty old_string is the CLI convention for creating a file' ($r -ceq "brand new`nfile") "got '$r'"

$r = Call $diff 'ApplyForward' @('Edit', (J @{ old_string = 'NOT PRESENT'; new_string = 'x' }), 'unrelated contents')
Check 'text that is not in the file yields no comparison' ($null -eq $r)

$r = Call $diff 'ApplyForward' @('Write', (J @{ content = "whole new body" }), 'anything at all')
Check 'Write replaces the entire file' ($r -ceq 'whole new body') "got '$r'"

$r = Call $diff 'ApplyForward' @('Write', (J @{}), 'anything at all')
Check 'Write with no content is an empty file, not a failure' ($r -ceq '')

""
"=== ReverseApply: what the file WAS - the fallback the live run never reached ==="
$r = Call $diff 'ReverseApply' @('Edit', (J @{ old_string = 'ALPHA'; new_string = 'BRAVO' }), "x BRAVO y ALPHA z")
Check 'undoes a single replacement' ($r -ceq 'x ALPHA y ALPHA z') "got '$r'"

$r = Call $diff 'ReverseApply' @('Edit', (J @{ old_string = 'ALPHA'; new_string = 'BRAVO'; replace_all = $true }), "x BRAVO y BRAVO z")
Check 'undoes a replace_all' ($r -ceq 'x ALPHA y ALPHA z') "got '$r'"

$r = Call $diff 'ReverseApply' @('Write', (J @{ content = 'new body' }), 'new body')
Check 'a Write cannot be undone from the call alone' ($null -eq $r)

$r = Call $diff 'ReverseApply' @('Edit', (J @{ old_string = 'ALPHA'; new_string = 'BRAVO' }), "the file has since changed")
Check 'refuses to guess when the file has moved on' ($null -eq $r)

$r = Call $diff 'ReverseApply' @('Edit', (J @{ old_string = 'ALPHA'; new_string = '' }), "anything")
Check 'a pure deletion cannot be located and is refused' ($null -eq $r)

""
"=== which tools are offered a tab at all ==="
# OpenCore returns its refusal before touching any VS service, so this is reachable with no IDE.
$r = Call $diff 'OpenCore' @('NotebookEdit', (J @{ notebook_path = 'x.ipynb' }), $false, '', $null, $null)
Check 'NotebookEdit is refused with a reason, not silently ignored' ($r -like '*Edit and Write*') "said: $r"

$r = Call $diff 'OpenCore' @('Bash', (J @{ command = 'ls' }), $false, '', $null, $null)
Check 'a non-file tool is refused' ($r -like '*Edit and Write*')

$r = Call $diff 'OpenCore' @('Edit', (J @{ old_string = 'a'; new_string = 'b' }), $false, '', $null, $null)
Check 'an Edit with no file_path is refused with its own reason' ($r -like "*doesn't name a file*") "said: $r"

$missing = Join-Path $env:TEMP ('phase-e-absent-' + [Guid]::NewGuid().ToString('N') + '.txt')
$r = Call $diff 'OpenCore' @('Edit', (J @{ file_path = $missing; old_string = 'a'; new_string = 'b' }), $true, '', $null, $null)
Check 'an applied edit to a file that is gone says so' ($r -like '*no longer on disk*') "said: $r"

""
"=== SessionCheckpointStore against the real transcripts the live runs left behind ==="
# Ground truth, read straight out of ~/.claude: the Edit was the first change to this file, so its
# own delta holds the original; the Write came later, when the file was already tracked and the CLI
# wrote no delta at all - its "before" exists only in the turn snapshot. That second case is the
# one that a delta-only reading got wrong, so it is the case worth pinning down.
$wd = 'D:\Projects\Visual Studio Projects\Test_Project_Claude'
$scratch = Join-Path $wd 'phase-e-scratch.txt'
$projectDir = Join-Path $env:USERPROFILE '.claude\projects\D--Projects-Visual-Studio-Projects-Test-Project-Claude'

$sessions = @(Get-ChildItem $projectDir -Filter '*.jsonl' -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending)
$tested = $false
foreach ($s in $sessions) {
    $sid = $s.BaseName
    $editId = $null; $writeId = $null
    foreach ($line in [IO.File]::ReadLines($s.FullName)) {
        if (-not $line.Trim()) { continue }
        try { $o = ConvertFrom-Json $line } catch { continue }
        if ($o.type -ne 'assistant') { continue }
        foreach ($b in $o.message.content) {
            if ($b.type -ne 'tool_use') { continue }
            # Cast: values off ConvertFrom-Json arrive wrapped, and reflection binds by exact
            # type. An unwrapped-looking wrapper reaches the method as something that is not a
            # string, and the scan simply finds nothing - a silent null rather than a type error.
            if ($b.name -eq 'Edit' -and -not $editId) { $editId = [string]$b.id }
            if ($b.name -eq 'Write' -and -not $writeId) { $writeId = [string]$b.id }
        }
    }
    if (-not ($editId -and $writeId)) { continue }

    "  session $sid"
    "    Edit  $editId"
    "    Write $writeId"

    $beforeEdit = Call $store 'TryReadContentBeforeEdit' @([string]$wd, [string]$sid, [string]$editId, [string]$scratch)
    $beforeWrite = Call $store 'TryReadContentBeforeEdit' @([string]$wd, [string]$sid, [string]$writeId, [string]$scratch)

    "    pre-Edit  -> $(if ($null -eq $beforeEdit) { '<null>' } else { ($beforeEdit -replace '?
', ' / ') })"
    "    pre-Write -> $(if ($null -eq $beforeWrite) { '<null>' } else { ($beforeWrite -replace '?
', ' / ') })"
    Check 'pre-Edit contents come back (from the delta)' ($beforeEdit -like '*ALPHA*' -and $beforeEdit -notlike '*BRAVO*')
    Check 'pre-Write contents come back (only the turn snapshot has these)' ($beforeWrite -like '*BRAVO*' -and $beforeWrite -notlike '*CHARLIE*')
    Check 'the two are genuinely different points in the file history' ($beforeEdit -cne $beforeWrite)

    $unknown = Call $store 'TryReadContentBeforeEdit' @([string]$wd, [string]$sid, 'toolu_does_not_exist', [string]$scratch)
    Check 'an unknown tool-use id answers nothing rather than guessing' ($null -eq $unknown)

    $otherFile = Call $store 'TryReadContentBeforeEdit' @([string]$wd, [string]$sid, [string]$editId, [string](Join-Path $wd 'Class1.cs'))
    Check 'a file this call never touched answers nothing' ($null -eq $otherFile)

    $tested = $true
    break
}
if (-not $tested) { Check 'a transcript with both an Edit and a Write was available' $false }

$r = Call $store 'TryReadContentBeforeEdit' @([string]$wd, 'not-a-real-session-id', 'toolu_x', [string]$scratch)
Check 'a missing transcript answers nothing' ($null -eq $r)

""
"=== SweepStaleTempDirs: the cleanup nothing has ever been old enough to trigger ==="
$root = Join-Path $env:TEMP 'TeronClaudeCodeVS-difftab'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$stale = Join-Path $root 'unit-stale'
$fresh = Join-Path $root 'unit-fresh'
New-Item -ItemType Directory -Force -Path $stale, $fresh | Out-Null
Set-Content -Path (Join-Path $stale 'a.before.txt') -Value 'old'
Set-Content -Path (Join-Path $fresh 'a.before.txt') -Value 'new'
# Read-only is the whole reason cleanup is ours rather than VS's, so the stale file must be one.
(Get-Item (Join-Path $stale 'a.before.txt')).IsReadOnly = $true
[IO.Directory]::SetLastWriteTimeUtc($stale, (Get-Date).ToUniversalTime().AddDays(-3))

Call $diff 'SweepStaleTempDirs' @() | Out-Null

Check 'a stale comparison directory is removed' (-not (Test-Path $stale))
Check 'read-only files do not block that removal' (-not (Test-Path (Join-Path $stale 'a.before.txt')))
Check 'a current comparison directory is left alone' (Test-Path $fresh)

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
