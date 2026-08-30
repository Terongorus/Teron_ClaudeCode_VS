# Phase H (FEAT-6, FEAT-7) against the real built assembly. No IDE, no model call.
#
# The FEAT-7 fixtures are not invented. The four `system` subtypes, their field names, their
# trigger vocabulary and the exact sentences they carry were read out of the shipped CLI binary
# (v2.1.251) on 2026-08-30 - schemas plus the message builders themselves:
#
#   model_fallback         trigger in {model_not_found, permission_denied, overloaded,
#                          server_error, last_resort, model_blocked}, content built by
#                            overloaded|server_error -> "Switched to {f} due to high demand for {o}"
#                            not_found|denied|blocked -> "Switched to {f} because {o} is not available"
#                            last_resort              -> "Switched to {f} because {o} returned an
#                                                         error that could not be retried"
#   model_refusal_fallback trigger "refusal", scope in {session, local}
#   model_consent_fallback content "Switched to {f} for this session · {o} requires usage credits
#                                   · /model to change"
#   model_refusal_no_fallback  no fallback_model at all
#
# The rule from feedback-live-verification-rigor #6 applies throughout: every check that asserts
# a null/empty result is paired with one that runs the same code and must NOT be null/empty.
param(
    [string]$BinDir = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\bin\Debug\net481',
    [string]$Root   = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS'
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
# ClaudeCodeOptionsPage is a DialogPage, so reaching its defaults means loading VS SDK assemblies
# that the VSIX itself never copies to bin. They ship with the VSSDK build tools; newest wins.
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

$asm = [System.Reflection.Assembly]::LoadFrom((Join-Path $BinDir 'TeronClaudeCodeVS.dll'))
$msgType     = $asm.GetType('TeronClaudeCodeVS.Protocol.ClaudeMessage', $true)
$fbType      = $asm.GetType('TeronClaudeCodeVS.Protocol.ModelFallbackEvent', $true)
$webType     = $asm.GetType('TeronClaudeCodeVS.ViewModels.WebContextComposer', $true)
$optsType    = $asm.GetType('TeronClaudeCodeVS.Core.ClaudeSessionStartOptions', $true)
$pageType    = $asm.GetType('TeronClaudeCodeVS.Core.ClaudeCodeOptionsPage', $true)
$sessionVm   = $asm.GetType('TeronClaudeCodeVS.ViewModels.ChatSessionViewModel', $true)
"loaded: $($asm.GetName().Name) $($asm.GetName().Version)"

$NS = [System.Reflection.BindingFlags]'NonPublic,Public,Static'
$parse    = $msgType.GetMethod('Parse', $NS)
$compose  = $webType.GetMethod('Compose', $NS)
$normUrl  = $webType.GetMethod('TryNormalizeUrl', $NS)

function Parse1([string]$line) { $parse.Invoke($null, @([string]$line)) }
function Compose1([string]$text) { $compose.Invoke($null, @([string]$text)) }
function Url1([string]$text) { $normUrl.Invoke($null, @([string]$text)) }

""
"=== FEAT-6: a URL is recognised and fetched ==="
Check 'an https URL is passed through as typed' `
    ((Compose1 'https://docs.claude.com/en/docs/mcp') -ceq 'Read https://docs.claude.com/en/docs/mcp and use it as context for this conversation.')
Check 'an http URL is accepted too' `
    ((Compose1 'http://localhost:3000/health') -ceq 'Read http://localhost:3000/health and use it as context for this conversation.')
Check 'a bare host gains an https scheme' `
    ((Compose1 'docs.claude.com/en/docs/mcp') -ceq 'Read https://docs.claude.com/en/docs/mcp and use it as context for this conversation.')
Check 'surrounding whitespace is trimmed before anything else' `
    ((Compose1 "   https://example.com/a   ") -ceq 'Read https://example.com/a and use it as context for this conversation.')
Check 'a query string survives intact' `
    ((Compose1 'https://example.com/s?q=a&b=c#frag') -ceq 'Read https://example.com/s?q=a&b=c#frag and use it as context for this conversation.')

""
"=== FEAT-6: anything else is searched for ==="
Check 'a multi-word phrase becomes a search' `
    ((Compose1 'claude code pricing 2026') -ceq 'Search the web for "claude code pricing 2026" and use the results as context for this conversation.')
Check 'a single word with no dot is a search, not a host' `
    ((Compose1 'kubernetes') -ceq 'Search the web for "kubernetes" and use the results as context for this conversation.')
Check 'a phrase containing a dotted word is still a search (it has spaces)' `
    ((Compose1 'what is example.com used for') -ceq 'Search the web for "what is example.com used for" and use the results as context for this conversation.')
Check 'double quotes in the terms are re-quoted so the span cannot close early' `
    ((Compose1 'the "big" one') -ceq 'Search the web for "the ''big'' one" and use the results as context for this conversation.')

""
"=== FEAT-6: nothing typed produces nothing ==="
Check 'an empty box composes null' ($null -eq (Compose1 ''))
Check 'a whitespace-only box composes null' ($null -eq (Compose1 "  `t  "))
Check 'CONTROL - one character is enough to compose something' ($null -ne (Compose1 'x'))

""
"=== FEAT-6: the URL test is narrow on purpose ==="
Check 'a file:// URI is not treated as web content' ($null -eq (Url1 'file:///c:/temp/x.txt'))
Check 'a mailto: URI is not treated as web content' ($null -eq (Url1 'mailto:someone@example.com'))
Check 'a bare word with no dot is not a host' ($null -eq (Url1 'kubernetes'))
Check 'a trailing dot is not a host' ($null -eq (Url1 'example.'))
Check 'a leading dot is not a host' ($null -eq (Url1 '.com'))
Check 'a bare path is not a host' ($null -eq (Url1 '/usr/local/bin'))
Check 'a Windows path is not a host' ($null -eq (Url1 'C:\temp\notes.md'))
Check 'CONTROL - the same routine does accept a real host' ((Url1 'example.com') -ceq 'https://example.com')
Check 'CONTROL - and a real absolute URL' ((Url1 'https://example.com') -ceq 'https://example.com')
# A file path that happens to contain a dot is the case most likely to be mis-read as a host.
Check 'a relative file path with an extension is not turned into a URL' `
    ((Compose1 'src/Program.cs') -like 'Search the web for*')

""
"=== FEAT-7: the four subtypes the CLI actually emits ==="
$overloaded = '{"type":"system","subtype":"model_fallback","trigger":"overloaded","original_model":"claude-opus-4-5-20251101","fallback_model":"claude-haiku-4-5-20251001","content":"Switched to claude-haiku-4-5-20251001 due to high demand for claude-opus-4-5-20251101","uuid":"6d0f2f6e-1f7b-4a2e-9c6a-9d3f1e2b7c40","session_id":"s1"}'
$m = Parse1 $overloaded
Check 'model_fallback parses to a ModelFallbackEvent' ($null -ne $m -and $m.GetType().FullName -eq $fbType.FullName)
Check 'its subtype is carried' ($m.Subtype -ceq 'model_fallback')
Check 'its trigger is carried' ($m.Trigger -ceq 'overloaded')
Check 'the original model is carried' ($m.OriginalModel -ceq 'claude-opus-4-5-20251101')
Check 'the fallback model is carried' ($m.FallbackModel -ceq 'claude-haiku-4-5-20251001')
Check 'the notice is the CLI''s own sentence, not a rebuilt one' `
    ($m.NoticeText -ceq 'Switched to claude-haiku-4-5-20251001 due to high demand for claude-opus-4-5-20251101')
Check 'a successful switch is not an error' ($m.IsFailure -eq $false)

$notFound = '{"type":"system","subtype":"model_fallback","trigger":"model_not_found","original_model":"claude-opus-3","fallback_model":"sonnet","content":"Switched to sonnet because claude-opus-3 is not available","uuid":"u","session_id":"s"}'
$m = Parse1 $notFound
Check 'the model_not_found trigger parses' ($m.Trigger -ceq 'model_not_found')
Check 'and carries its own wording' ($m.NoticeText -ceq 'Switched to sonnet because claude-opus-3 is not available')

$lastResort = '{"type":"system","subtype":"model_fallback","trigger":"last_resort","original_model":"opus","fallback_model":"haiku","content":"Switched to haiku because opus returned an error that could not be retried (503 upstream)","uuid":"u","session_id":"s"}'
$m = Parse1 $lastResort
Check 'the last_resort trigger parses with its parenthesised detail intact' `
    ($m.NoticeText -ceq 'Switched to haiku because opus returned an error that could not be retried (503 upstream)')

$consent = '{"type":"system","subtype":"model_consent_fallback","choice":"consent","original_model":"claude-opus-4-5-20251101","fallback_model":"claude-haiku-4-5-20251001","persisted_as_default":false,"content":"Switched to claude-haiku-4-5-20251001 for this session · claude-opus-4-5-20251101 requires usage credits · /model to change","uuid":"u","session_id":"s"}'
$m = Parse1 $consent
Check 'model_consent_fallback parses' ($m.Subtype -ceq 'model_consent_fallback')
Check 'this is the subtype behind the line the audit saw baseline print' ($m.NoticeText -like 'Switched to claude-haiku-4-5-20251001*')
Check 'the middot-separated sentence survives verbatim' ($m.NoticeText -clike '*· claude-opus-4-5-20251101 requires usage credits · /model to change')
Check 'a consented switch is not an error either' ($m.IsFailure -eq $false)

$refusal = '{"type":"system","subtype":"model_refusal_fallback","trigger":"refusal","direction":"retry","scope":"session","original_model":"opus","fallback_model":"sonnet","request_id":"req_1","api_refusal_category":"cyber","content":"Switched to sonnet. This response was generated by sonnet instead.","uuid":"u","session_id":"s"}'
$m = Parse1 $refusal
Check 'model_refusal_fallback parses' ($m.Subtype -ceq 'model_refusal_fallback')
Check 'its scope is carried' ($m.Scope -ceq 'session')
Check 'its trigger is the literal "refusal"' ($m.Trigger -ceq 'refusal')
Check 'a refusal that was rescued is not an error' ($m.IsFailure -eq $false)

$refusalLocal = '{"type":"system","subtype":"model_refusal_fallback","trigger":"refusal","direction":"retry","scope":"local","original_model":"opus","fallback_model":"sonnet","content":"Switched to sonnet for this response.","uuid":"u","session_id":"s"}'
Check 'the local scope is carried too' ((Parse1 $refusalLocal).Scope -ceq 'local')

$noFallback = '{"type":"system","subtype":"model_refusal_no_fallback","original_model":"opus","request_id":null,"api_refusal_category":"cyber","content":"opus declined this request and no fallback model is configured.","uuid":"u","session_id":"s"}'
$m = Parse1 $noFallback
Check 'model_refusal_no_fallback parses' ($m.Subtype -ceq 'model_refusal_no_fallback')
Check 'it has no fallback model, by definition' ([string]::IsNullOrEmpty($m.FallbackModel))
Check 'and it is the one subtype flagged as a failure' ($m.IsFailure -eq $true)
Check 'CONTROL - the other three are not' `
    (((Parse1 $overloaded).IsFailure -eq $false) -and ((Parse1 $consent).IsFailure -eq $false) -and ((Parse1 $refusal).IsFailure -eq $false))

""
"=== FEAT-7: older CLIs, and lines that say nothing ==="
$noContent = '{"type":"system","subtype":"model_fallback","trigger":"overloaded","original_model":"opus","fallback_model":"haiku","uuid":"u","session_id":"s"}'
$m = Parse1 $noContent
Check 'a subtype with no content still parses' ($null -ne $m)
Check 'and the notice is rebuilt from the two models' ($m.NoticeText -ceq 'Switched to haiku from opus')
$noModels = '{"type":"system","subtype":"model_refusal_no_fallback","uuid":"u","session_id":"s"}'
Check 'a line with neither a sentence nor a model is dropped rather than shown blank' ($null -eq (Parse1 $noModels))
$onlyOriginal = '{"type":"system","subtype":"model_refusal_no_fallback","original_model":"opus","uuid":"u","session_id":"s"}'
$m = Parse1 $onlyOriginal
Check 'CONTROL - one model is enough to keep the line' ($null -ne $m)
Check 'and it still says something a reader can act on' ($m.NoticeText -ceq 'opus refused this turn and no fallback model is configured')

""
"=== FEAT-7: the parser is not over-eager ==="
Check 'an unrelated system subtype is still ignored' ($null -eq (Parse1 '{"type":"system","subtype":"permission_denied","tool":"Edit"}'))
Check 'a subtype that merely contains the word is ignored' ($null -eq (Parse1 '{"type":"system","subtype":"compact_no_model_fallback_env","content":"x"}'))
$init = Parse1 '{"type":"system","subtype":"init","session_id":"s","model":"sonnet","permissionMode":"acceptEdits","cwd":"c:\\x","slash_commands":["help"]}'
Check 'CONTROL - init still parses as before' ($init.GetType().Name -eq 'InitMessage')
$compact = Parse1 '{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"manual","pre_tokens":10,"post_tokens":2,"cumulative_dropped_tokens":8}}'
Check 'CONTROL - compact_boundary still parses as before' ($compact.GetType().Name -eq 'CompactBoundaryEvent')

""
"=== FEAT-7: the flag is only emitted when it means something ==="
Check 'ClaudeSessionStartOptions carries a FallbackModel' ($null -ne $optsType.GetProperty('FallbackModel'))
# ClaudeCodeOptionsPage is a DialogPage: its constructor needs a live VS service provider, so it
# cannot be instantiated here, and its DEFAULTS are checked in the Exp instance instead (see
# phase-h-live.ps1). What is checkable headlessly is that the two properties exist, are the right
# type, and are surfaced on the page rather than hidden.
$toggle = $pageType.GetProperty('SwitchModelsAutomatically')
$target = $pageType.GetProperty('FallbackModel')
Check 'the page declares a toggle, as a bool' (($null -ne $toggle) -and ($toggle.PropertyType -eq [bool]))
Check 'the page declares a fallback target, as a string' (($null -ne $target) -and ($target.PropertyType -eq [string]))
foreach ($prop in @($toggle, $target)) {
    $cat = $prop.GetCustomAttributes([System.ComponentModel.CategoryAttribute], $false)
    $disp = $prop.GetCustomAttributes([System.ComponentModel.DisplayNameAttribute], $false)
    $desc = $prop.GetCustomAttributes([System.ComponentModel.DescriptionAttribute], $false)
    Check "$($prop.Name) is filed under the Defaults category" (($cat.Count -eq 1) -and ($cat[0].Category -ceq 'Defaults'))
    Check "$($prop.Name) has a display name and a real description" (($disp.Count -eq 1) -and ($desc.Count -eq 1) -and ($desc[0].Description.Length -gt 40))
}
# CONTROL: the same attribute reader must see the internal throttle field as NOT browsable, so a
# missing attribute would show up as a difference rather than as an empty read everywhere.
$internal = $pageType.GetProperty('LastUpdateCheckUtc')
$browsable = $internal.GetCustomAttributes([System.ComponentModel.BrowsableAttribute], $false)
Check 'CONTROL - the attribute reader tells a hidden property from a shown one' `
    (($browsable.Count -eq 1) -and ($browsable[0].Browsable -eq $false))

# The gating expression itself, exercised on the real view model rather than re-implemented here.
$vm = [System.Activator]::CreateInstance($sessionVm)
$setOpts = $sessionVm.GetMethod('SetAdvancedOptions')
$optsField = $sessionVm.GetField('_advancedOptions', [System.Reflection.BindingFlags]'NonPublic,Instance')
function Fallback([bool]$on, [string]$model) {
    $null = $setOpts.Invoke($vm, @([string]'', [string]'', [string]'', [string]'', [string]'', [string]'', [bool]$false, [bool]$on, [string]$model))
    return $optsField.GetValue($vm).FallbackModel
}
Check 'toggle off, model named  -> no flag' ($null -eq (Fallback $false 'haiku'))
Check 'toggle on, model blank   -> no flag' ($null -eq (Fallback $true ''))
Check 'toggle on, model spaces  -> no flag' ($null -eq (Fallback $true '   '))
Check 'toggle on, model named   -> the flag' ((Fallback $true 'haiku') -ceq 'haiku')
Check 'a padded value is trimmed before it reaches the command line' ((Fallback $true '  sonnet  ') -ceq 'sonnet')
Check 'a comma-separated chain is passed through as one argument, as the CLI documents' `
    ((Fallback $true 'sonnet,haiku') -ceq 'sonnet,haiku')

""
"=== the new markup's element references resolve ==="
# Click/KeyDown handler names are a compile error when missing, so the build already covers those.
# ElementName is not: it resolves at runtime and fails silently, exactly like a binding path.
$xaml = Get-Content (Join-Path $Root 'Core\ClaudeCodeChatControl.xaml') -Raw
$names = [regex]::Matches($xaml, 'x:Name="([A-Za-z0-9_]+)"') | ForEach-Object { $_.Groups[1].Value }
$refs  = [regex]::Matches($xaml, 'ElementName=([A-Za-z0-9_]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
Check 'the markup declares ElementName references to resolve' ($refs.Count -ge 3) "$($refs.Count) distinct"
foreach ($r in $refs) { Check "ElementName=$r is a declared x:Name" ($names -contains $r) }
Check 'CONTROL - the same test rejects a name that was never declared' (-not ($names -contains 'AddMenuButtonn'))

foreach ($n in @('AddMenuButton','AddMenuPopup','WebQueryPanel','WebQueryBox')) {
    Check "the add menu declares $n" ($names -contains $n)
}
foreach ($label in @('Upload from computer','Add context','Browse the web')) {
    Check "baseline's label '$label' appears verbatim" ($xaml -clike "*Text=`"$label`"*")
}

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
