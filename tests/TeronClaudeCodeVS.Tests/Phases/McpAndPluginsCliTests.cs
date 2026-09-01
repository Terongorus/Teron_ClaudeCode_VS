using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TeronClaudeCodeVS.Core;
using TeronClaudeCodeVS.Tests.Infrastructure;
using TeronClaudeCodeVS.ViewModels;
using Xunit;

namespace TeronClaudeCodeVS.Tests.Phases
{
    /// <summary>
    /// Phase G against the real CLI, ported from <c>comparison-audit/scripts/phase-g-vm.ps1</c>.
    /// <para>
    /// <see cref="McpAndPluginsTests"/> feeds captured text to the parsers. These spawn the actual
    /// <c>claude</c> binary and assert on what the view models make of what it really says - which
    /// is the only way to catch the failure the parser tests cannot see: a working directory that
    /// never reaches the child process. Every case therefore runs the same view model against two
    /// different directories and requires the answers to differ.
    /// </para>
    /// <para>
    /// The user's own configuration is off limits. The plugin cases run under a throwaway
    /// <c>CLAUDE_CONFIG_DIR</c>, and both classes of test assert afterwards that
    /// <c>~/.claude.json</c> was never written and that the real configuration still has nothing
    /// installed.
    /// </para>
    /// </summary>
    public sealed class McpAndPluginsCliTests : IDisposable
    {
        private readonly string _root;
        private readonly string _withServers;
        private readonly string _withoutServers;
        private readonly string _marketplace;
        private readonly string _configDir;
        private readonly string _claude;

        private readonly string _realConfigPath;
        private readonly DateTime? _realConfigWrittenBefore;

        public McpAndPluginsCliTests()
        {
            _claude = ClaudeCliLocator.Find(null)
                ?? throw new InvalidOperationException("ClaudeCliLocator found no CLI on this machine - nothing to verify against.");

            _realConfigPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
            _realConfigWrittenBefore = File.Exists(_realConfigPath)
                ? File.GetLastWriteTimeUtc(_realConfigPath)
                : (DateTime?)null;

            _root = Path.Combine(Path.GetTempPath(), "teron-phase-g-" + Guid.NewGuid().ToString("N"));
            _withServers = Path.Combine(_root, "with-servers");
            _withoutServers = Path.Combine(_root, "no-servers");
            _marketplace = Path.Combine(_root, "mkt");
            _configDir = Path.Combine(_root, "cfg");

            foreach (string directory in new[] { _withServers, _withoutServers, _marketplace, _configDir })
                Directory.CreateDirectory(directory);

            SeedMcpServers();
            SeedMarketplace();
        }

        public void Dispose()
        {
            // Nothing of the user's may have moved. Asserted here rather than in one test so it
            // holds however the class is filtered or re-ordered.
            DateTime? after = File.Exists(_realConfigPath) ? File.GetLastWriteTimeUtc(_realConfigPath) : (DateTime?)null;
            Assert.Equal(_realConfigWrittenBefore, after);

            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }

        // ─── FEAT-4: the real CLI, in a directory that has MCP servers ──────────────────────────

        [Fact]
        public async Task The_panel_finds_the_project_scoped_servers_that_are_really_there()
        {
            var vm = new McpServersViewModel();
            await vm.RefreshAsync(_claude, _withServers);

            Assert.False(vm.IsLoading, "the panel must not be left spinning");
            Assert.Null(vm.LoadError);
            Assert.Equal(2, vm.Servers.Count);

            Assert.Equal(new[] { "demo-stdio", "demo-http" }, vm.Servers.Select(s => s.Name));
            Assert.Equal("node server.js", vm.Servers[0].Target);
            Assert.Equal("HTTP", vm.Servers[1].Transport);
            Assert.All(vm.Servers, s => Assert.NotEmpty(s.Status));

            Assert.Equal(_withServers, vm.ScopeDirectory);
            Assert.True(vm.HasLoaded);
        }

        [Fact]
        public async Task The_same_view_model_one_directory_over_reports_nothing()
        {
            // This is the check the parser tests cannot make. If the working directory were not
            // plumbed through to the child process, the second run would return the first's
            // servers, and going back would prove the difference was the directory and not the
            // order of the runs.
            var vm = new McpServersViewModel();

            await vm.RefreshAsync(_claude, _withServers);
            Assert.Equal(2, vm.Servers.Count);

            await vm.RefreshAsync(_claude, _withoutServers);
            Assert.Empty(vm.Servers);
            Assert.Equal(McpServersViewModel.DefaultEmptyState, vm.EmptyStateText);
            Assert.Null(vm.LoadError);
            Assert.Equal(_withoutServers, vm.ScopeDirectory);

            // CONTROL.
            await vm.RefreshAsync(_claude, _withServers);
            Assert.Equal(2, vm.Servers.Count);
        }

        // ─── FEAT-4: a failure must not read as an empty state ──────────────────────────────────

        [Fact]
        public async Task A_cli_that_cannot_run_produces_an_error_rather_than_an_empty_list()
        {
            var vm = new McpServersViewModel();
            await vm.RefreshAsync(Path.Combine(_root, "no-such-claude.exe"), _withServers);

            Assert.NotNull(vm.LoadError);
            Assert.Empty(vm.Servers);
            Assert.False(vm.HasLoaded, "a failed run must not be marked as a successful load");
        }

        [Fact]
        public async Task No_cli_path_at_all_says_so_in_words_the_user_can_act_on()
        {
            var vm = new McpServersViewModel();
            await vm.RefreshAsync(null, _withServers);

            Assert.NotNull(vm.LoadError);
            Assert.Contains("not found", vm.LoadError);
        }

        // ─── The shared runner: a timeout is reported as a timeout ──────────────────────────────

        [Fact]
        public async Task A_timeout_is_reported_as_one_and_not_as_success()
        {
            ClaudeCliResult timed = await ClaudeCliQuery.RunAsync(_claude, "mcp list", _withServers, timeoutMs: 1);

            Assert.True(timed.TimedOut);
            Assert.False(timed.Succeeded);
            Assert.NotNull(timed.ErrorMessage);
            Assert.Contains("mcp list", timed.ErrorMessage);

            // CONTROL: the same call with a real budget succeeds, so the assertions above are
            // about the budget rather than about the command being broken.
            ClaudeCliResult fine = await ClaudeCliQuery.RunAsync(_claude, "mcp list", _withServers, timeoutMs: 30000);
            Assert.True(fine.Succeeded, $"exit {fine.ExitCode}, timedOut={fine.TimedOut}");
        }

        // ─── FEAT-5: the user's own, unmodified configuration ───────────────────────────────────

        [Fact]
        public async Task Against_the_real_configuration_the_panel_matches_ground_truth_on_disk()
        {
            // Written 2026-08-30 against a machine with nothing configured, this asserted flat
            // emptiness. It no longer holds: the CLI itself auto-installs an official marketplace
            // now (`officialMarketplaceAutoInstalled` in ~/.claude.json), unrelated to anything this
            // project did. Asserting a fixed count would just be pinning today's number, so this
            // reads the same ground truth the CLI itself would - ~/.claude/plugins/known_marketplaces.json
            // - independently, the same way Truth.Scan works in the Phase F tests.
            int realMarketplaceCount = RealKnownMarketplaceCount();

            var vm = new PluginsViewModel();
            await vm.RefreshAsync(_claude, _withoutServers);

            Assert.Null(vm.LoadError);
            Assert.Equal(realMarketplaceCount, vm.Marketplaces.Count);

            string expectedEmptyState = realMarketplaceCount == 0
                ? PluginsViewModel.NoMarketplacesEmptyState
                : PluginsViewModel.NoPluginsInstalledEmptyState;

            if (vm.InstalledPlugins.Count == 0)
                Assert.Equal(expectedEmptyState, vm.PluginsEmptyStateText);

            // IsPluginListEmpty covers both lists, not just installed - the official marketplace
            // ships plugins available to install even when none are, which is exactly the state
            // "empty" is supposed to mean false for.
            Assert.Equal(vm.InstalledPlugins.Count == 0 && vm.AvailablePlugins.Count == 0, vm.IsPluginListEmpty);
        }

        /// <summary>
        /// Reads the same file the real CLI reads for its own marketplace list, independently of
        /// <see cref="PluginsViewModel"/> - so a count mismatch means the view model disagrees with
        /// disk, not that both sides share one wrong assumption.
        /// </summary>
        private static int RealKnownMarketplaceCount()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @".claude\plugins\known_marketplaces.json");

            if (!File.Exists(path))
                return 0;

            var parsed = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            return parsed.Properties().Count();
        }

        // ─── FEAT-5: a throwaway config with a real marketplace and a real installed plugin ─────

        [Fact]
        public async Task A_real_install_into_a_sandboxed_config_shows_up_in_every_list()
        {
            // CLAUDE_CONFIG_DIR is inherited by the child process, so all of this lands in TEMP.
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _configDir);

            try
            {
                RunClaude("plugin marketplace add \"" + _marketplace + "\"");
                RunClaude("plugin install demo-plugin@teron-demo-marketplace");

                var vm = new PluginsViewModel();
                await vm.RefreshAsync(_claude, _withoutServers);

                Assert.Null(vm.LoadError);

                PluginEntry installed = Assert.Single(vm.InstalledPlugins);
                Assert.Equal("demo-plugin", installed.Name);
                Assert.Equal("0.1.0", installed.Version);
                Assert.Equal("user", installed.Scope);

                PluginEntry available = Assert.Single(vm.AvailablePlugins);
                Assert.Equal("Another fixture, not installed", available.Description);

                MarketplaceEntry market = Assert.Single(vm.Marketplaces);
                Assert.Equal("directory", market.Source);
                Assert.Equal(_marketplace, market.Path);

                Assert.False(vm.IsPluginListEmpty);

                // The branch that exists only because baseline's sentence is wrong once a
                // marketplace is present.
                Assert.Equal(PluginsViewModel.NoPluginsInstalledEmptyState, vm.PluginsEmptyStateText);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);
            }

            // And with the sandbox gone, the user's real configuration is exactly as it was before
            // this test touched anything - compared by count against the same on-disk ground truth
            // used above, not against an assumption that it started at zero.
            var afterwards = new PluginsViewModel();
            await afterwards.RefreshAsync(_claude, _withoutServers);

            Assert.Equal(RealKnownMarketplaceCount(), afterwards.Marketplaces.Count);
            Assert.DoesNotContain(afterwards.Marketplaces, m => m.Name == "teron-demo-marketplace");
            Assert.DoesNotContain(afterwards.InstalledPlugins, p => p.Name == "demo-plugin");
        }

        // ─── fixtures ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Two project-scoped servers, written directly as .mcp.json in the CLI's own schema - the
        /// same file <c>claude mcp add --scope project</c> produces, captured from a real run of it.
        /// </summary>
        private void SeedMcpServers() => Write(Path.Combine(_withServers, ".mcp.json"), @"{
  ""mcpServers"": {
    ""demo-stdio"": { ""type"": ""stdio"", ""command"": ""node"", ""args"": [""server.js""], ""env"": {} },
    ""demo-http"": { ""type"": ""http"", ""url"": ""https://example.com/mcp"" }
  }
}");

        private void SeedMarketplace()
        {
            Directory.CreateDirectory(Path.Combine(_marketplace, ".claude-plugin"));
            Directory.CreateDirectory(Path.Combine(_marketplace, @"demo-plugin\.claude-plugin"));
            Directory.CreateDirectory(Path.Combine(_marketplace, @"second-plugin\.claude-plugin"));

            Write(Path.Combine(_marketplace, @".claude-plugin\marketplace.json"), @"{
  ""name"": ""teron-demo-marketplace"",
  ""owner"": { ""name"": ""Terongorus"" },
  ""plugins"": [
    { ""name"": ""demo-plugin"", ""source"": ""./demo-plugin"", ""description"": ""A local fixture plugin"", ""version"": ""0.1.0"" },
    { ""name"": ""second-plugin"", ""source"": ""./second-plugin"", ""description"": ""Another fixture, not installed"", ""version"": ""2.3.4"" }
  ]
}");

            Write(Path.Combine(_marketplace, @"demo-plugin\.claude-plugin\plugin.json"),
                @"{ ""name"": ""demo-plugin"", ""description"": ""A local fixture plugin"", ""version"": ""0.1.0"" }");

            Write(Path.Combine(_marketplace, @"second-plugin\.claude-plugin\plugin.json"),
                @"{ ""name"": ""second-plugin"", ""description"": ""Another fixture, not installed"", ""version"": ""2.3.4"" }");
        }

        private static void Write(string path, string content)
            => File.WriteAllText(path, content, new UTF8Encoding(false));

        /// <summary>Runs a setup command through the CLI. Its output is not the subject of any test.</summary>
        private void RunClaude(string arguments)
        {
            var startInfo = new ProcessStartInfo(_claude, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _root,
            };

            using Process process = Process.Start(startInfo)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();

            if (!process.WaitForExit(60000))
                throw new TimeoutException($"`claude {arguments}` did not finish within 60s.");
        }
    }
}
