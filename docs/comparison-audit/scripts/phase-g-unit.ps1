# Phase G (FEAT-4, FEAT-5) at the parser level, against the real built assembly. No IDE.
#
# Every fixture in this file is either:
#   (a) real output captured from the shipped CLI on 2026-08-30 - `claude mcp list`,
#       `claude plugin list --json --available`, `claude plugin marketplace list --json`, run
#       against a throwaway marketplace in TEMP and a throwaway CLAUDE_CONFIG_DIR so the user's own
#       configuration was never read or written; or
#   (b) a line assembled from the CLI binary's OWN renderer and status vocabulary, which were read
#       out of the shipped executable rather than guessed:
#         renderer: `${name}: ${url} (HTTP) - ${o}` etc., o = issue ? `${status} — ${issue}` : status
#         statuses: ✓ Connected · ! Connected · tools fetch failed · ! Needs authentication ·
#                   - Not configured · ✗ Failed to connect · ✗ Connection error ·
#                   ⏸ Pending approval (run `claude` to approve) · ✗ Rejected (…) · ⊘ Disabled (…)
#       Category (b) exists because a machine with two dozen healthy MCP servers is not available
#       to capture from, and "we never tested the connected case" is not an acceptable gap.
#
# The rule from feedback-live-verification-rigor #6 applies throughout: every check that asserts
# something is EMPTY is paired with one that runs the same code and must NOT be empty.
param(
    [string]$BinDir = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\bin\Debug\net481'
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
$mcpType = $asm.GetType('TeronClaudeCodeVS.ViewModels.McpServersViewModel', $true)
$plugType = $asm.GetType('TeronClaudeCodeVS.ViewModels.PluginsViewModel', $true)
$queryType = $asm.GetType('TeronClaudeCodeVS.Core.ClaudeCliQuery', $true)
"loaded: $($asm.GetName().Name) $($asm.GetName().Version)"

$NS = [System.Reflection.BindingFlags]'NonPublic,Public,Static'
$mcpParse = $mcpType.GetMethod('Parse', $NS)
$mcpClassify = $mcpType.GetMethod('Classify', $NS)
$mcpEmpty = $mcpType.GetMethod('ExtractEmptyState', $NS)
$parseInstalled = $plugType.GetMethod('ParseInstalled', $NS)
$parseAvailable = $plugType.GetMethod('ParseAvailable', $NS)
$parseMarkets = $plugType.GetMethod('ParseMarketplaces', $NS)
$stripAnsi = $queryType.GetMethod('StripAnsi', $NS)

# Reflection hands back the List<T> itself here (assignment, not pipeline), but assert that rather
# than assume it - a silently unrolled single-element list is the Phase F trap all over again.
function Rows($method, [string]$text) {
    $r = $method.Invoke($null, @([string]$text))
    if ($null -eq $r) { throw "$($method.Name) returned null" }
    if (-not $r.GetType().FullName.StartsWith('System.Collections.Generic.List')) {
        throw "$($method.Name) returned $($r.GetType().FullName), not a List - the harness is unrolling it"
    }
    return $r
}
function Prop($obj, [string]$name) { return $obj.GetType().GetProperty($name).GetValue($obj) }

""
"=== FEAT-4: `claude mcp list`, real captured output ==="

# Captured verbatim on 2026-08-30 from a .mcp.json in TEMP holding one stdio and one http server.
$realTwo = @"
Checking MCP server health…

demo-stdio: node server.js - ⏸ Pending approval (run ``claude`` to approve)
demo-http: https://example.com/mcp (HTTP) - ⏸ Pending approval (run ``claude`` to approve)
"@

$rows = Rows $mcpParse $realTwo
Check 'both servers are found in the real two-server capture' ($rows.Count -eq 2) "$($rows.Count) row(s)"
if ($rows.Count -eq 2) {
    Check 'stdio name'      ((Prop $rows[0] 'Name') -ceq 'demo-stdio')
    Check 'stdio target is the whole command line' ((Prop $rows[0] 'Target') -ceq 'node server.js') "'$(Prop $rows[0] 'Target')'"
    Check 'stdio transport is inferred, not printed' ((Prop $rows[0] 'Transport') -ceq 'stdio')
    Check 'stdio status is the CLI''s own sentence' ((Prop $rows[0] 'Status') -ceq '⏸ Pending approval (run `claude` to approve)') "'$(Prop $rows[0] 'Status')'"
    Check 'stdio status classifies as Pending' ("$(Prop $rows[0] 'Kind')" -eq 'Pending')
    Check 'http name'       ((Prop $rows[1] 'Name') -ceq 'demo-http')
    Check 'http target has the marker stripped' ((Prop $rows[1] 'Target') -ceq 'https://example.com/mcp') "'$(Prop $rows[1] 'Target')'"
    Check 'http transport'  ((Prop $rows[1] 'Transport') -ceq 'HTTP')
}

# The health-check banner is not a server. Prove it is dropped for the right reason: the same text
# with the banner removed must yield the identical row count.
$withoutBanner = ($realTwo -split "`n" | Where-Object { $_ -notmatch 'Checking MCP server health' }) -join "`n"
Check 'the health-check banner changes nothing' ((Rows $mcpParse $withoutBanner).Count -eq $rows.Count)

""
"=== FEAT-4: the empty state is the CLI's own line ==="
$realEmpty = 'No MCP servers configured. Use `claude mcp add` to add a server.'
$emptyRows = Rows $mcpParse $realEmpty
Check 'the empty-state sentence parses as zero servers' ($emptyRows.Count -eq 0) "$($emptyRows.Count) row(s)"
# Positive control: the same parser, one line different, must find something.
Check 'CONTROL - the same parser does find a server when there is one' ((Rows $mcpParse "$realEmpty`nx: node a.js - ✓ Connected").Count -eq 1)
Check 'the extracted empty state is the CLI''s sentence verbatim' ($mcpEmpty.Invoke($null, @([string]$realEmpty)) -ceq $realEmpty)
Check 'a banner-only run falls back to the known wording' ($mcpEmpty.Invoke($null, @([string]"Checking MCP server health…`n`n")) -ceq $mcpType.GetField('DefaultEmptyState', $NS).GetValue($null))
Check 'the shipped constant still equals baseline''s sentence' ($mcpType.GetField('DefaultEmptyState', $NS).GetValue($null) -ceq $realEmpty)

""
"=== FEAT-4: every status in the CLI's vocabulary ==="
# Left column is the exact status the binary emits; right column is the Kind it must map to.
$vocabulary = @(
    @{ status = '✓ Connected'; kind = 'Connected' }
    @{ status = '! Connected · tools fetch failed'; kind = 'Warning' }
    @{ status = '! Needs authentication'; kind = 'Warning' }
    @{ status = '- Not configured'; kind = 'Warning' }
    @{ status = '✗ Failed to connect'; kind = 'Error' }
    @{ status = '✗ Connection error'; kind = 'Error' }
    @{ status = '⏸ Pending approval (run `claude` to approve)'; kind = 'Pending' }
    @{ status = '✗ Rejected (see disabledMcpjsonServers in settings)'; kind = 'Error' }
    @{ status = '⊘ Disabled for this project (re-enable via /mcp)'; kind = 'Disabled' }
)
foreach ($v in $vocabulary) {
    $got = "$($mcpClassify.Invoke($null, @([string]$v.status)))"
    Check "'$($v.status)' -> $($v.kind)" ($got -eq $v.kind) $(if ($got -ne $v.kind) { "got $got" } else { '' })
}
# "Connected · tools fetch failed" must lose to Warning, not win as Connected. Without this the
# ordering inside Classify could be reversed and every other check above would still pass.
Check 'a degraded Connected is NOT classified as Connected' ("$($mcpClassify.Invoke($null, @([string]'! Connected · tools fetch failed')))" -ne 'Connected')
Check 'an unknown status falls back to Unknown rather than guessing' ("$($mcpClassify.Invoke($null, @([string]'~ Something new in a future CLI')))" -eq 'Unknown')

""
"=== FEAT-4: the format's genuinely hard cases ==="

# A stdio command line that itself contains ' - '. The status is still the last field.
$hard = 'weird: node build/index.js --flag - value - ✗ Failed to connect — spawn ENOENT'
$r = (Rows $mcpParse $hard)[0]
Check 'a command line containing " - " keeps all of it' ((Prop $r 'Target') -ceq 'node build/index.js --flag - value') "'$(Prop $r 'Target')'"
Check 'and the status is still the last field' ((Prop $r 'Status') -ceq '✗ Failed to connect')
Check 'the em-dash detail is split off as the issue' ((Prop $r 'Issue') -ceq 'spawn ENOENT')
Check 'and the row reports that it has one' ((Prop $r 'HasIssue') -eq $true)

# The collision the parser exists to survive: the "- Not configured" status starts with the same
# characters as the separator, so the naive rightmost split lands one character late.
$collide = 'none: node a.js - - Not configured'
$r = (Rows $mcpParse $collide)[0]
Check 'the "- Not configured" collision leaves no stray dash on the target' ((Prop $r 'Target') -ceq 'node a.js') "'$(Prop $r 'Target')'"
Check 'and the status keeps its leading marker' ((Prop $r 'Status') -ceq '- Not configured') "'$(Prop $r 'Status')'"

# SSE and the proxy transport, which the real capture had no example of.
$r = (Rows $mcpParse 'asana: https://mcp.asana.com/sse (SSE) - ! Needs authentication')[0]
Check 'SSE transport is recognised' ((Prop $r 'Transport') -ceq 'SSE')
Check 'and its URL loses the marker' ((Prop $r 'Target') -ceq 'https://mcp.asana.com/sse')
$r = (Rows $mcpParse 'proxy: https://mcp-proxy.anthropic.com - ✓ Connected')[0]
Check 'an unmarked URL is not labelled stdio' ((Prop $r 'Transport') -ceq '') "'$(Prop $r 'Transport')'"
Check 'and it has no transport chip to show' ((Prop $r 'HasTransport') -eq $false)

# Lines that are not server rows must be ignored rather than half-parsed.
$noise = @"
Some future banner line with no separator
: leading colon and nothing else
name-with-no-separator https://x
ok: node a.js - ✓ Connected
"@
$rows = Rows $mcpParse $noise
Check 'unrecognised lines are dropped' ($rows.Count -eq 1) "$($rows.Count) row(s)"
Check 'and the one real row still survives them' ((Prop $rows[0] 'Name') -ceq 'ok')
Check 'empty input yields no rows' ((Rows $mcpParse '').Count -eq 0)

""
"=== FEAT-5: real captured JSON, nothing configured ==="
# `claude plugin list --json --available` and `claude plugin marketplace list --json`, run against
# the user's own (empty) configuration on 2026-08-30.
$emptyPlugins = "{`n  `"installed`": [],`n  `"available`": []`n}"
Check 'no installed plugins' ((Rows $parseInstalled $emptyPlugins).Count -eq 0)
Check 'no available plugins' ((Rows $parseAvailable $emptyPlugins).Count -eq 0)
Check 'no marketplaces' ((Rows $parseMarkets '[]').Count -eq 0)

""
"=== FEAT-5: real captured JSON, one installed and one available ==="
# Captured from a local fixture marketplace under a throwaway CLAUDE_CONFIG_DIR.
$realPlugins = @'
{
  "installed": [
    {
      "id": "demo-plugin@teron-demo-marketplace",
      "version": "0.1.0",
      "scope": "user",
      "enabled": true,
      "installPath": "C:\\Temp\\cfg\\plugins\\cache\\teron-demo-marketplace\\demo-plugin\\0.1.0",
      "installedAt": "2026-08-30T00:15:34.942Z",
      "lastUpdated": "2026-08-30T00:15:34.942Z"
    }
  ],
  "available": [
    {
      "pluginId": "second-plugin@teron-demo-marketplace",
      "name": "second-plugin",
      "description": "Another fixture, not installed",
      "marketplaceName": "teron-demo-marketplace",
      "version": "2.3.4",
      "source": "./second-plugin"
    }
  ]
}
'@
$installed = Rows $parseInstalled $realPlugins
Check 'the installed plugin is found' ($installed.Count -eq 1) "$($installed.Count)"
if ($installed.Count -eq 1) {
    $p = $installed[0]
    Check 'its id is split into name and marketplace' (((Prop $p 'Name') -ceq 'demo-plugin') -and ((Prop $p 'Marketplace') -ceq 'teron-demo-marketplace'))
    Check 'version, scope and enablement are read' (((Prop $p 'Version') -ceq '0.1.0') -and ((Prop $p 'Scope') -ceq 'user') -and ((Prop $p 'IsEnabled') -eq $true))
    Check 'it is marked installed' ((Prop $p 'IsInstalled') -eq $true)
    Check 'an installed row has no description (the CLI sends none)' ((Prop $p 'HasDescription') -eq $false)
    Check 'the detail line carries what the row cannot show elsewhere' ((Prop $p 'DetailLine') -ceq 'v0.1.0 · teron-demo-marketplace · user · enabled') "'$(Prop $p 'DetailLine')'"
    Check 'and Id round-trips to what `claude plugin install` takes' ((Prop $p 'Id') -ceq 'demo-plugin@teron-demo-marketplace')
}

$available = Rows $parseAvailable $realPlugins
Check 'the available plugin is found' ($available.Count -eq 1) "$($available.Count)"
if ($available.Count -eq 1) {
    $p = $available[0]
    Check 'available rows use pluginId/name/description' (((Prop $p 'Name') -ceq 'second-plugin') -and ((Prop $p 'Description') -ceq 'Another fixture, not installed'))
    Check 'an available row is not marked installed' ((Prop $p 'IsInstalled') -eq $false)
    Check 'its marketplace comes from marketplaceName' ((Prop $p 'Marketplace') -ceq 'teron-demo-marketplace')
    Check 'and it does have a description to show' ((Prop $p 'HasDescription') -eq $true)
}

# The installed list must not swallow the available one, or the panel would show ghosts as installed.
Check 'available plugins do not leak into the installed list' ((Prop $installed[0] 'Name') -cne 'second-plugin')

$realMarkets = @'
[
  {
    "name": "teron-demo-marketplace",
    "source": "directory",
    "path": "C:\\Temp\\plug-sandbox\\mkt",
    "installLocation": "C:\\Temp\\plug-sandbox\\mkt"
  }
]
'@
$markets = Rows $parseMarkets $realMarkets
Check 'the marketplace is found' ($markets.Count -eq 1) "$($markets.Count)"
if ($markets.Count -eq 1) {
    Check 'its name and source are read' (((Prop $markets[0] 'Name') -ceq 'teron-demo-marketplace') -and ((Prop $markets[0] 'Source') -ceq 'directory'))
    Check 'the detail line reads as a sentence, not a field dump' ((Prop $markets[0] 'DetailLine') -ceq 'Directory · C:\Temp\plug-sandbox\mkt') "'$(Prop $markets[0] 'DetailLine')'"
}

""
"=== FEAT-5: shapes an older or noisier CLI could produce ==="
# Without --available the command returns a bare array. Both shapes must work.
$bareArray = '[{"id":"a@b","version":"1.0.0","scope":"local","enabled":false}]'
$fromBare = Rows $parseInstalled $bareArray
Check 'the bare-array shape is accepted too' ($fromBare.Count -eq 1)
Check 'and a disabled plugin says so' ((Prop $fromBare[0] 'DetailLine') -ceq 'v1.0.0 · b · local · disabled') "'$(Prop $fromBare[0] 'DetailLine')'"
Check 'a bare array has no available section' ((Rows $parseAvailable $bareArray).Count -eq 0)

Check 'JSON preceded by chatter is still parsed' ((Rows $parseMarkets "npm notice a new version is available`n$realMarkets").Count -eq 1)
Check 'output with no JSON at all yields nothing' ((Rows $parseMarkets 'command not found').Count -eq 0)
Check 'CONTROL - the same call does yield something for real JSON' ((Rows $parseMarkets $realMarkets).Count -eq 1)
Check 'a plugin entry with no id is skipped rather than shown blank' ((Rows $parseInstalled '[{"version":"1.0.0"}]').Count -eq 0)
Check 'a marketplace with no name is skipped' ((Rows $parseMarkets '[{"source":"github"}]').Count -eq 0)
Check 'an id with no marketplace still yields a usable row' (((Rows $parseInstalled '[{"id":"solo","version":"1.0.0"}]')[0].Name) -ceq 'solo')

""
"=== FEAT-5: which empty-state sentence applies ==="
$noMarkets = $plugType.GetField('NoMarketplacesEmptyState', $NS).GetValue($null)
$noPlugins = $plugType.GetField('NoPluginsInstalledEmptyState', $NS).GetValue($null)
Check 'baseline''s sentence is stored verbatim' ($noMarkets -ceq 'No plugins available. Add a marketplace to discover plugins.')
Check 'and the CLI''s own sentence is stored verbatim' ($noPlugins -ceq 'No plugins installed. Use `claude plugin install` to install a plugin.')

$vm = [Activator]::CreateInstance($plugType)
Check 'with no marketplaces, the panel shows baseline''s sentence' ($vm.PluginsEmptyStateText -ceq $noMarkets)
$vm.Marketplaces.Add((Rows $parseMarkets $realMarkets)[0])
Check 'with a marketplace present, it switches to the CLI''s sentence' ($vm.PluginsEmptyStateText -ceq $noPlugins)
Check 'the tab strip starts on Plugins' (($vm.IsPluginsTab -eq $true) -and ($vm.IsMarketplacesTab -eq $false))
$vm.SelectedTab = [System.Enum]::Parse($asm.GetType('TeronClaudeCodeVS.ViewModels.PluginsTab'), 'Marketplaces')
Check 'and switching tabs flips both flags' (($vm.IsPluginsTab -eq $false) -and ($vm.IsMarketplacesTab -eq $true))
Check 'an empty plugin list is reported as empty' ($vm.IsPluginListEmpty -eq $true)
$vm.InstalledPlugins.Add($installed[0])
Check 'CONTROL - one installed plugin makes it non-empty' ($vm.IsPluginListEmpty -eq $false)

""
"=== the shared runner's text handling ==="
$esc = [char]27
Check 'ANSI colour codes are stripped' ($stripAnsi.Invoke($null, @([string]"$esc[32m✓ Connected$esc[0m")) -ceq '✓ Connected')
Check 'plain text passes through untouched' ($stripAnsi.Invoke($null, @([string]'demo: node a.js - ✓ Connected')) -ceq 'demo: node a.js - ✓ Connected')
Check 'null is tolerated' ($stripAnsi.Invoke($null, @([string]$null)) -ceq '')

""
"=== every binding the two new panels declare resolves to a real member ==="
# The one XAML risk a headless run can still cover: a typo in a binding path fails silently at
# runtime (WPF logs and shows blank), so it would survive a live look at the panel too.
$xaml = Get-Content 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\Core\ClaudeCodeChatControl.xaml' -Raw
$sessionType = $asm.GetType('TeronClaudeCodeVS.ViewModels.ChatSessionViewModel', $true)
$panelXaml = $xaml.Substring($xaml.IndexOf('x:Name="McpPopup"'))
$paths = [regex]::Matches($panelXaml, '\{Binding (McpServers|Plugins)\.([A-Za-z0-9_.]+?)(?:,|\})') |
    ForEach-Object { $_.Groups[1].Value + '.' + $_.Groups[2].Value } | Sort-Object -Unique
Check 'the panels declare bindings to resolve' ($paths.Count -ge 12) "$($paths.Count) distinct path(s)"
foreach ($path in $paths) {
    $t = $sessionType
    $ok = $true
    foreach ($segment in $path.Split('.')) {
        $member = $t.GetProperty($segment)
        if ($null -eq $member) { $ok = $false; break }
        $t = $member.PropertyType
    }
    Check "binding $path resolves" $ok
}
# Positive control: the same walker must reject a path that does not exist.
$t = $sessionType; $bogus = $true
foreach ($segment in 'McpServers.Serverz'.Split('.')) {
    $m = $t.GetProperty($segment)
    if ($null -eq $m) { $bogus = $false; break }
    $t = $m.PropertyType
}
Check 'CONTROL - the binding walker rejects a path that does not exist' ($bogus -eq $false)

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
