# Phase G (FEAT-4, FEAT-5) end to end: the real view models, driving the real Claude CLI, with no
# Visual Studio instance and no window.
#
# phase-g-unit.ps1 proves the parsers against captured output. What it cannot reach is everything
# between the panel and the CLI - and that is where the interesting failures live:
#
#   * `claude mcp list` reports project-scoped servers relative to the CURRENT DIRECTORY. If the
#     working directory is not plumbed through, the panel silently shows nothing on a solution that
#     has servers, and no parser test can tell. This script proves it by running the same view model
#     twice against two directories and requiring different answers.
#   * "the command failed" and "there is nothing configured" must not look alike. A run with a bogus
#     CLI path must produce an error, not a serene empty state.
#
# Safety: every fixture lives in TEMP. The plugin half runs with CLAUDE_CONFIG_DIR pointed at a
# throwaway directory, so no plugin or marketplace is ever added to the user's own configuration -
# and the script asserts, before and after, that the real ~/.claude.json is untouched.
param(
    [string]$BinDir = 'd:\Projects\Visual Studio Projects\Teron_Extensions\Teron_ClaudeCode_VS\bin\Debug\net481'
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$script:pass = 0
$script:fail = 0
function Check([string]$label, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; "  PASS  $label $detail" }
    else { $script:fail++; "  FAIL  $label $detail" }
}

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

$NS = [System.Reflection.BindingFlags]'NonPublic,Public,Static'
$mcpType = $asm.GetType('TeronClaudeCodeVS.ViewModels.McpServersViewModel', $true)
$plugType = $asm.GetType('TeronClaudeCodeVS.ViewModels.PluginsViewModel', $true)
$queryType = $asm.GetType('TeronClaudeCodeVS.Core.ClaudeCliQuery', $true)
$locatorType = $asm.GetType('TeronClaudeCodeVS.Core.ClaudeCliLocator', $true)
"loaded: $($asm.GetName().Name) $($asm.GetName().Version)"

# Resolve the CLI the way the extension itself does, rather than hard-coding a path.
$claude = $locatorType.GetMethod('Find', $NS).Invoke($null, @([string]$null))
if ([string]::IsNullOrEmpty($claude)) { throw 'ClaudeCliLocator found no CLI on this machine - nothing to verify against.' }
"claude:  $claude"

# RefreshAsync returns a plain Task, and GetResult() on one still yields a VoidTaskResult that
# PowerShell would print. Swallow that, but pass a real result (RunAsync's) straight through.
function Await($task) {
    $r = $task.GetAwaiter().GetResult()
    if ($null -ne $r -and $r.GetType().Name -ne 'VoidTaskResult') { return $r }
}

# ─── The user's own configuration must come out of this untouched ───────────────────────────────
$realConfig = Join-Path $env:USERPROFILE '.claude.json'
$realBefore = if (Test-Path $realConfig) { (Get-Item $realConfig).LastWriteTimeUtc } else { $null }

# ─── Fixtures, rebuilt from scratch so the script is repeatable ──────────────────────────────────
$root = Join-Path $env:TEMP 'teron-phase-g'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
$withServers = Join-Path $root 'with-servers'
$withoutServers = Join-Path $root 'no-servers'
$marketDir = Join-Path $root 'mkt'
$configDir = Join-Path $root 'cfg'
foreach ($d in @($withServers, $withoutServers, $marketDir, $configDir)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

# Two project-scoped MCP servers, written directly as .mcp.json in the CLI's own schema (the same
# file `claude mcp add --scope project` produces - captured from a real run of it).
@'
{
  "mcpServers": {
    "demo-stdio": { "type": "stdio", "command": "node", "args": ["server.js"], "env": {} },
    "demo-http": { "type": "http", "url": "https://example.com/mcp" }
  }
}
'@ | Set-Content -Path (Join-Path $withServers '.mcp.json') -Encoding UTF8

New-Item -ItemType Directory -Force -Path (Join-Path $marketDir '.claude-plugin') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $marketDir 'demo-plugin\.claude-plugin') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $marketDir 'second-plugin\.claude-plugin') | Out-Null
@'
{
  "name": "teron-demo-marketplace",
  "owner": { "name": "Terongorus" },
  "plugins": [
    { "name": "demo-plugin", "source": "./demo-plugin", "description": "A local fixture plugin", "version": "0.1.0" },
    { "name": "second-plugin", "source": "./second-plugin", "description": "Another fixture, not installed", "version": "2.3.4" }
  ]
}
'@ | Set-Content -Path (Join-Path $marketDir '.claude-plugin\marketplace.json') -Encoding UTF8
'{ "name": "demo-plugin", "description": "A local fixture plugin", "version": "0.1.0" }' |
    Set-Content -Path (Join-Path $marketDir 'demo-plugin\.claude-plugin\plugin.json') -Encoding UTF8
'{ "name": "second-plugin", "description": "Another fixture, not installed", "version": "2.3.4" }' |
    Set-Content -Path (Join-Path $marketDir 'second-plugin\.claude-plugin\plugin.json') -Encoding UTF8

""
"=== FEAT-4: the real CLI, in a directory that has MCP servers ==="
$mcp = [Activator]::CreateInstance($mcpType)
Await $mcp.RefreshAsync($claude, $withServers)

Check 'the panel is not left spinning' ($mcp.IsLoading -eq $false)
Check 'the run reported no error' ($null -eq $mcp.LoadError) "$($mcp.LoadError)"
Check 'both project-scoped servers were found' ($mcp.Servers.Count -eq 2) "$($mcp.Servers.Count) server(s)"
if ($mcp.Servers.Count -eq 2) {
    $names = ($mcp.Servers | ForEach-Object { $_.Name }) -join ','
    Check 'and they are the two that were configured' ($names -ceq 'demo-stdio,demo-http') "got '$names'"
    Check 'the stdio one kept its command line' (($mcp.Servers[0].Target) -ceq 'node server.js') "'$($mcp.Servers[0].Target)'"
    Check 'the http one was recognised as HTTP' (($mcp.Servers[1].Transport) -ceq 'HTTP')
    Check 'every row carries a status the CLI actually printed' (($mcp.Servers | Where-Object { $_.Status.Length -gt 0 }).Count -eq 2)
}
Check 'the scope directory is reported back to the user' ($mcp.ScopeDirectory -ceq $withServers)
Check 'the run is marked loaded' ($mcp.HasLoaded -eq $true)

""
"=== FEAT-4: the SAME view model, one directory over ==="
# This is the check the parser tests cannot make. If the working directory were not plumbed through
# to the child process, this second run would return the same two servers as the first.
Await $mcp.RefreshAsync($claude, $withoutServers)
Check 'a directory with no .mcp.json reports no servers' ($mcp.Servers.Count -eq 0) "$($mcp.Servers.Count) server(s)"
Check 'and the empty state is the CLI''s own sentence' ($mcp.EmptyStateText -ceq 'No MCP servers configured. Use `claude mcp add` to add a server.') "'$($mcp.EmptyStateText)'"
Check 'with no error, because nothing went wrong' ($null -eq $mcp.LoadError) "$($mcp.LoadError)"
Check 'the scope directory moved with it' ($mcp.ScopeDirectory -ceq $withoutServers)

# And back again, to prove the difference is the directory and not the order of the runs.
Await $mcp.RefreshAsync($claude, $withServers)
Check 'CONTROL - going back to the first directory finds them again' ($mcp.Servers.Count -eq 2) "$($mcp.Servers.Count) server(s)"

""
"=== FEAT-4: a failure must not read as an empty state ==="
$broken = [Activator]::CreateInstance($mcpType)
Await $broken.RefreshAsync((Join-Path $root 'no-such-claude.exe'), $withServers)
Check 'a CLI that cannot run produces an error' ($null -ne $broken.LoadError) "'$($broken.LoadError)'"
Check 'and no servers' ($broken.Servers.Count -eq 0)
Check 'and is NOT marked as a successful load' ($broken.HasLoaded -eq $false)

$noPath = [Activator]::CreateInstance($mcpType)
Await $noPath.RefreshAsync($null, $withServers)
Check 'no CLI path at all says so in words the user can act on' (($noPath.LoadError -ne $null) -and ($noPath.LoadError -match 'not found')) "'$($noPath.LoadError)'"

""
"=== the shared runner: timeout is reported as a timeout ==="
$timed = Await $queryType.GetMethod('RunAsync', $NS).Invoke($null, @([string]$claude, [string]'mcp list', [string]$withServers, [int]1))
Check 'a 1ms budget times out' ($timed.TimedOut -eq $true)
Check 'the timeout is not mistaken for success' ($timed.Succeeded -eq $false)
Check 'and it says which command ran out of time' ($timed.ErrorMessage -match 'mcp list') "'$($timed.ErrorMessage)'"

$fine = Await $queryType.GetMethod('RunAsync', $NS).Invoke($null, @([string]$claude, [string]'mcp list', [string]$withServers, [int]30000))
Check 'CONTROL - the same call with a real budget succeeds' ($fine.Succeeded -eq $true) "exit $($fine.ExitCode), timedOut=$($fine.TimedOut)"

""
"=== FEAT-5: the real CLI against the user's own (unmodified) configuration ==="
$plugins = [Activator]::CreateInstance($plugType)
Await $plugins.RefreshAsync($claude, $withoutServers)
Check 'the query succeeded' ($null -eq $plugins.LoadError) "$($plugins.LoadError)"
Check 'nothing is installed on this machine' ($plugins.InstalledPlugins.Count -eq 0) "$($plugins.InstalledPlugins.Count)"
Check 'no marketplaces are configured' ($plugins.Marketplaces.Count -eq 0) "$($plugins.Marketplaces.Count)"
Check 'so the panel shows baseline''s sentence' ($plugins.PluginsEmptyStateText -ceq 'No plugins available. Add a marketplace to discover plugins.') "'$($plugins.PluginsEmptyStateText)'"
Check 'and the plugin list reports itself empty' ($plugins.IsPluginListEmpty -eq $true)

""
"=== FEAT-5: a throwaway config with a real marketplace and a real installed plugin ==="
# CLAUDE_CONFIG_DIR is inherited by the child process, so everything below lands in TEMP.
$env:CLAUDE_CONFIG_DIR = $configDir
try {
    & $claude plugin marketplace add $marketDir 2>&1 | Out-Null
    & $claude plugin install "demo-plugin@teron-demo-marketplace" 2>&1 | Out-Null

    $sandboxed = [Activator]::CreateInstance($plugType)
    Await $sandboxed.RefreshAsync($claude, $withoutServers)

    Check 'the query succeeded' ($null -eq $sandboxed.LoadError) "$($sandboxed.LoadError)"
    Check 'the installed plugin is listed' ($sandboxed.InstalledPlugins.Count -eq 1) "$($sandboxed.InstalledPlugins.Count)"
    if ($sandboxed.InstalledPlugins.Count -eq 1) {
        Check 'with its name split off the id' (($sandboxed.InstalledPlugins[0].Name) -ceq 'demo-plugin') "'$($sandboxed.InstalledPlugins[0].Name)'"
        Check 'and its real version' (($sandboxed.InstalledPlugins[0].Version) -ceq '0.1.0')
        Check 'and its real scope' (($sandboxed.InstalledPlugins[0].Scope) -ceq 'user')
    }
    Check 'the uninstalled one is listed as available' ($sandboxed.AvailablePlugins.Count -eq 1) "$($sandboxed.AvailablePlugins.Count)"
    if ($sandboxed.AvailablePlugins.Count -eq 1) {
        Check 'with the marketplace''s own description' (($sandboxed.AvailablePlugins[0].Description) -ceq 'Another fixture, not installed') "'$($sandboxed.AvailablePlugins[0].Description)'"
    }
    Check 'the marketplace is listed' ($sandboxed.Marketplaces.Count -eq 1) "$($sandboxed.Marketplaces.Count)"
    if ($sandboxed.Marketplaces.Count -eq 1) {
        Check 'as a local directory source' (($sandboxed.Marketplaces[0].Source) -ceq 'directory') "'$($sandboxed.Marketplaces[0].Source)'"
        Check 'pointing at the fixture' (($sandboxed.Marketplaces[0].Path) -ceq $marketDir) "'$($sandboxed.Marketplaces[0].Path)'"
    }
    Check 'the list no longer reports itself empty' ($sandboxed.IsPluginListEmpty -eq $false)
    # The branch that only exists because baseline's sentence is wrong once a marketplace exists.
    Check 'and the empty-state sentence has switched to the CLI''s' ($sandboxed.PluginsEmptyStateText -ceq 'No plugins installed. Use `claude plugin install` to install a plugin.') "'$($sandboxed.PluginsEmptyStateText)'"
}
finally {
    Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
}

""
"=== nothing of the user's was touched ==="
$realAfter = if (Test-Path $realConfig) { (Get-Item $realConfig).LastWriteTimeUtc } else { $null }
Check 'the user''s own ~/.claude.json is unchanged' ($realBefore -eq $realAfter) "$realBefore -> $realAfter"
$leaked = [Activator]::CreateInstance($plugType)
Await $leaked.RefreshAsync($claude, $withoutServers)
Check 'and their real configuration still has no marketplaces' ($leaked.Marketplaces.Count -eq 0) "$($leaked.Marketplaces.Count)"
Check 'and no installed plugins' ($leaked.InstalledPlugins.Count -eq 0) "$($leaked.InstalledPlugins.Count)"

Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

""
"=== summary ==="
"  passed: $script:pass    failed: $script:fail"
if ($script:fail -gt 0) { "  RESULT: FAILURES PRESENT" } else { "  RESULT: all checks passed" }
