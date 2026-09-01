using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TeronClaudeCodeVS.Core;
using TeronClaudeCodeVS.Tests.Infrastructure;
using TeronClaudeCodeVS.ViewModels;
using Xunit;

namespace TeronClaudeCodeVS.Tests.Phases
{
    /// <summary>
    /// Phase G (FEAT-4 MCP servers, FEAT-5 plugins), ported from
    /// <c>comparison-audit/scripts/phase-g-unit.ps1</c>.
    /// <para>
    /// Both features parse output the CLI produces, so every fixture here was captured verbatim
    /// from a real <c>claude</c> run on 2026-08-30 rather than written to match my reading of the
    /// format. The awkward cases - a command line that itself contains the separator, and the
    /// status whose marker collides with it - are the ones the parser exists for.
    /// </para>
    /// </summary>
    public sealed class McpAndPluginsTests
    {
        // ─── FEAT-4: `claude mcp list`, real captured output ────────────────────────────────────

        private const string RealTwoServers =
            "Checking MCP server health…\n" +
            "\n" +
            "demo-stdio: node server.js - ⏸ Pending approval (run `claude` to approve)\n" +
            "demo-http: https://example.com/mcp (HTTP) - ⏸ Pending approval (run `claude` to approve)";

        [Fact]
        public void Both_servers_are_found_in_the_real_two_server_capture()
        {
            List<McpServerEntry> rows = McpServersViewModel.Parse(RealTwoServers);

            Assert.Equal(2, rows.Count);

            Assert.Equal("demo-stdio", rows[0].Name);
            Assert.Equal("node server.js", rows[0].Target);
            Assert.Equal("stdio", rows[0].Transport);   // inferred, never printed by the CLI
            Assert.Equal("⏸ Pending approval (run `claude` to approve)", rows[0].Status);
            Assert.Equal(McpStatusKind.Pending, rows[0].Kind);

            Assert.Equal("demo-http", rows[1].Name);
            Assert.Equal("https://example.com/mcp", rows[1].Target);
            Assert.Equal("HTTP", rows[1].Transport);
        }

        [Fact]
        public void The_health_check_banner_is_dropped_for_the_right_reason()
        {
            // Not "the banner produced no row" - the same text without it must yield the identical
            // count, which is what shows the banner was recognised rather than accidentally parsed
            // into something harmless.
            string withoutBanner = string.Join("\n",
                RealTwoServers.Split('\n').Where(line => !line.Contains("Checking MCP server health")));

            Assert.Equal(McpServersViewModel.Parse(RealTwoServers).Count,
                         McpServersViewModel.Parse(withoutBanner).Count);
        }

        // ─── FEAT-4: the empty state is the CLI's own line ──────────────────────────────────────

        private const string RealEmptyState = "No MCP servers configured. Use `claude mcp add` to add a server.";

        [Fact]
        public void The_empty_state_sentence_parses_as_zero_servers()
        {
            Assert.Empty(McpServersViewModel.Parse(RealEmptyState));

            // CONTROL: the same parser, one line different, must find something - otherwise the
            // assertion above would also pass against a parser that had stopped working.
            Assert.Single(McpServersViewModel.Parse(RealEmptyState + "\nx: node a.js - ✓ Connected"));
        }

        [Fact]
        public void The_empty_state_is_taken_from_the_CLI_verbatim_with_a_known_fallback()
        {
            Assert.Equal(RealEmptyState, McpServersViewModel.ExtractEmptyState(RealEmptyState));
            Assert.Equal(McpServersViewModel.DefaultEmptyState,
                         McpServersViewModel.ExtractEmptyState("Checking MCP server health…\n\n"));

            // The shipped constant must still be baseline's sentence, character for character.
            Assert.Equal(RealEmptyState, McpServersViewModel.DefaultEmptyState);
        }

        // ─── FEAT-4: every status in the CLI's vocabulary ───────────────────────────────────────

        [Theory]
        [InlineData("✓ Connected", McpStatusKind.Connected)]
        [InlineData("! Connected · tools fetch failed", McpStatusKind.Warning)]
        [InlineData("! Needs authentication", McpStatusKind.Warning)]
        [InlineData("- Not configured", McpStatusKind.Warning)]
        [InlineData("✗ Failed to connect", McpStatusKind.Error)]
        [InlineData("✗ Connection error", McpStatusKind.Error)]
        [InlineData("⏸ Pending approval (run `claude` to approve)", McpStatusKind.Pending)]
        [InlineData("✗ Rejected (see disabledMcpjsonServers in settings)", McpStatusKind.Error)]
        [InlineData("⊘ Disabled for this project (re-enable via /mcp)", McpStatusKind.Disabled)]
        public void Every_status_the_binary_emits_classifies(string status, McpStatusKind expected)
        {
            Assert.Equal(expected, McpServersViewModel.Classify(status));
        }

        [Fact]
        public void A_degraded_connected_status_loses_to_warning_and_the_unknown_falls_back()
        {
            // Without this, the ordering inside Classify could be reversed and every case above
            // would still pass.
            Assert.NotEqual(McpStatusKind.Connected, McpServersViewModel.Classify("! Connected · tools fetch failed"));
            Assert.Equal(McpStatusKind.Unknown, McpServersViewModel.Classify("~ Something new in a future CLI"));
        }

        // ─── FEAT-4: the format's genuinely hard cases ──────────────────────────────────────────

        [Fact]
        public void A_command_line_containing_the_separator_keeps_all_of_it()
        {
            McpServerEntry row = Assert.Single(McpServersViewModel.Parse(
                "weird: node build/index.js --flag - value - ✗ Failed to connect — spawn ENOENT"));

            Assert.Equal("node build/index.js --flag - value", row.Target);
            Assert.Equal("✗ Failed to connect", row.Status);
            Assert.Equal("spawn ENOENT", row.Issue);
            Assert.True(row.HasIssue);
        }

        [Fact]
        public void The_not_configured_status_does_not_collide_with_the_separator()
        {
            // "- Not configured" starts with the same characters as the " - " separator, so the
            // naive rightmost split lands one character late and leaves a stray dash behind.
            McpServerEntry row = Assert.Single(McpServersViewModel.Parse("none: node a.js - - Not configured"));

            Assert.Equal("node a.js", row.Target);
            Assert.Equal("- Not configured", row.Status);
        }

        [Fact]
        public void Sse_is_recognised_and_an_unmarked_url_is_not_called_stdio()
        {
            McpServerEntry sse = Assert.Single(McpServersViewModel.Parse(
                "asana: https://mcp.asana.com/sse (SSE) - ! Needs authentication"));

            Assert.Equal("SSE", sse.Transport);
            Assert.Equal("https://mcp.asana.com/sse", sse.Target);

            McpServerEntry proxy = Assert.Single(McpServersViewModel.Parse(
                "proxy: https://mcp-proxy.anthropic.com - ✓ Connected"));

            Assert.Equal("", proxy.Transport);
            Assert.False(proxy.HasTransport);
        }

        [Fact]
        public void Unrecognised_lines_are_dropped_without_taking_the_real_row_with_them()
        {
            const string noise =
                "Some future banner line with no separator\n" +
                ": leading colon and nothing else\n" +
                "name-with-no-separator https://x\n" +
                "ok: node a.js - ✓ Connected";

            McpServerEntry row = Assert.Single(McpServersViewModel.Parse(noise));
            Assert.Equal("ok", row.Name);

            Assert.Empty(McpServersViewModel.Parse(""));
        }

        // ─── FEAT-5: real captured JSON ─────────────────────────────────────────────────────────

        private const string EmptyPluginsJson = "{\n  \"installed\": [],\n  \"available\": []\n}";

        private const string RealPluginsJson = @"{
  ""installed"": [
    {
      ""id"": ""demo-plugin@teron-demo-marketplace"",
      ""version"": ""0.1.0"",
      ""scope"": ""user"",
      ""enabled"": true,
      ""installPath"": ""C:\\Temp\\cfg\\plugins\\cache\\teron-demo-marketplace\\demo-plugin\\0.1.0"",
      ""installedAt"": ""2026-08-30T00:15:34.942Z"",
      ""lastUpdated"": ""2026-08-30T00:15:34.942Z""
    }
  ],
  ""available"": [
    {
      ""pluginId"": ""second-plugin@teron-demo-marketplace"",
      ""name"": ""second-plugin"",
      ""description"": ""Another fixture, not installed"",
      ""marketplaceName"": ""teron-demo-marketplace"",
      ""version"": ""2.3.4"",
      ""source"": ""./second-plugin""
    }
  ]
}";

        private const string RealMarketplacesJson = @"[
  {
    ""name"": ""teron-demo-marketplace"",
    ""source"": ""directory"",
    ""path"": ""C:\\Temp\\plug-sandbox\\mkt"",
    ""installLocation"": ""C:\\Temp\\plug-sandbox\\mkt""
  }
]";

        [Fact]
        public void An_empty_configuration_yields_nothing_anywhere()
        {
            Assert.Empty(PluginsViewModel.ParseInstalled(EmptyPluginsJson));
            Assert.Empty(PluginsViewModel.ParseAvailable(EmptyPluginsJson));
            Assert.Empty(PluginsViewModel.ParseMarketplaces("[]"));
        }

        [Fact]
        public void An_installed_plugin_is_read_in_full()
        {
            PluginEntry plugin = Assert.Single(PluginsViewModel.ParseInstalled(RealPluginsJson));

            Assert.Equal("demo-plugin", plugin.Name);
            Assert.Equal("teron-demo-marketplace", plugin.Marketplace);
            Assert.Equal("0.1.0", plugin.Version);
            Assert.Equal("user", plugin.Scope);
            Assert.True(plugin.IsEnabled);
            Assert.True(plugin.IsInstalled);
            Assert.False(plugin.HasDescription);   // the CLI sends none for installed rows
            Assert.Equal("v0.1.0 · teron-demo-marketplace · user · enabled", plugin.DetailLine);

            // Id has to round-trip to what `claude plugin install` takes.
            Assert.Equal("demo-plugin@teron-demo-marketplace", plugin.Id);
        }

        [Fact]
        public void An_available_plugin_uses_the_other_field_names_and_is_not_marked_installed()
        {
            PluginEntry plugin = Assert.Single(PluginsViewModel.ParseAvailable(RealPluginsJson));

            Assert.Equal("second-plugin", plugin.Name);
            Assert.Equal("Another fixture, not installed", plugin.Description);
            Assert.Equal("teron-demo-marketplace", plugin.Marketplace);
            Assert.False(plugin.IsInstalled);
            Assert.True(plugin.HasDescription);

            // If the installed list swallowed the available one, the panel would show ghosts as
            // installed.
            Assert.NotEqual("second-plugin", Assert.Single(PluginsViewModel.ParseInstalled(RealPluginsJson)).Name);
        }

        [Fact]
        public void A_marketplace_reads_as_a_sentence_rather_than_a_field_dump()
        {
            MarketplaceEntry market = Assert.Single(PluginsViewModel.ParseMarketplaces(RealMarketplacesJson));

            Assert.Equal("teron-demo-marketplace", market.Name);
            Assert.Equal("directory", market.Source);
            Assert.Equal(@"Directory · C:\Temp\plug-sandbox\mkt", market.DetailLine);
        }

        // ─── FEAT-5: shapes an older or noisier CLI could produce ───────────────────────────────

        [Fact]
        public void The_bare_array_shape_is_accepted_too()
        {
            // Without --available the command returns a bare array rather than an object.
            const string bare = @"[{""id"":""a@b"",""version"":""1.0.0"",""scope"":""local"",""enabled"":false}]";

            PluginEntry plugin = Assert.Single(PluginsViewModel.ParseInstalled(bare));
            Assert.Equal("v1.0.0 · b · local · disabled", plugin.DetailLine);

            Assert.Empty(PluginsViewModel.ParseAvailable(bare));
        }

        [Fact]
        public void Json_preceded_by_chatter_is_still_parsed_and_non_json_is_not()
        {
            Assert.Single(PluginsViewModel.ParseMarketplaces(
                "npm notice a new version is available\n" + RealMarketplacesJson));

            Assert.Empty(PluginsViewModel.ParseMarketplaces("command not found"));

            // CONTROL for the line above.
            Assert.Single(PluginsViewModel.ParseMarketplaces(RealMarketplacesJson));
        }

        [Fact]
        public void Entries_missing_their_identifier_are_skipped_rather_than_shown_blank()
        {
            Assert.Empty(PluginsViewModel.ParseInstalled(@"[{""version"":""1.0.0""}]"));
            Assert.Empty(PluginsViewModel.ParseMarketplaces(@"[{""source"":""github""}]"));

            // An id with no marketplace is still usable, so it must not be dropped with them.
            Assert.Equal("solo", Assert.Single(
                PluginsViewModel.ParseInstalled(@"[{""id"":""solo"",""version"":""1.0.0""}]")).Name);
        }

        // ─── FEAT-5: which empty-state sentence applies ─────────────────────────────────────────

        [Fact]
        public void The_two_empty_state_sentences_are_stored_verbatim()
        {
            Assert.Equal("No plugins available. Add a marketplace to discover plugins.",
                         PluginsViewModel.NoMarketplacesEmptyState);
            Assert.Equal("No plugins installed. Use `claude plugin install` to install a plugin.",
                         PluginsViewModel.NoPluginsInstalledEmptyState);
        }

        [Fact]
        public void The_panel_switches_sentence_once_a_marketplace_exists()
        {
            var vm = new PluginsViewModel();
            Assert.Equal(PluginsViewModel.NoMarketplacesEmptyState, vm.PluginsEmptyStateText);

            vm.Marketplaces.Add(Assert.Single(PluginsViewModel.ParseMarketplaces(RealMarketplacesJson)));
            Assert.Equal(PluginsViewModel.NoPluginsInstalledEmptyState, vm.PluginsEmptyStateText);
        }

        [Fact]
        public void The_tab_strip_starts_on_plugins_and_switching_flips_both_flags()
        {
            var vm = new PluginsViewModel();

            Assert.True(vm.IsPluginsTab);
            Assert.False(vm.IsMarketplacesTab);

            vm.SelectedTab = PluginsTab.Marketplaces;

            Assert.False(vm.IsPluginsTab);
            Assert.True(vm.IsMarketplacesTab);
        }

        [Fact]
        public void An_empty_plugin_list_is_reported_as_empty()
        {
            var vm = new PluginsViewModel();
            Assert.True(vm.IsPluginListEmpty);

            // CONTROL: one installed plugin makes it non-empty.
            vm.InstalledPlugins.Add(Assert.Single(PluginsViewModel.ParseInstalled(RealPluginsJson)));
            Assert.False(vm.IsPluginListEmpty);
        }

        // ─── The shared runner's text handling ──────────────────────────────────────────────────

        [Fact]
        public void Ansi_colour_codes_are_stripped_and_plain_text_is_untouched()
        {
            const char esc = (char)27;

            Assert.Equal("✓ Connected", ClaudeCliQuery.StripAnsi($"{esc}[32m✓ Connected{esc}[0m"));
            Assert.Equal("demo: node a.js - ✓ Connected", ClaudeCliQuery.StripAnsi("demo: node a.js - ✓ Connected"));
            Assert.Equal("", ClaudeCliQuery.StripAnsi(null));
        }

        // ─── Every binding the two panels declare resolves to a real member ─────────────────────

        [Fact]
        public void Every_binding_path_in_the_two_new_panels_resolves()
        {
            // The one XAML risk a headless run can still cover: a typo in a binding path fails
            // silently at run time - WPF logs it and shows a blank - so it would survive a live
            // look at the panel too.
            string xaml = File.ReadAllText(Fixtures.ProjectFile("Core", "ClaudeCodeChatControl.xaml"));
            string panels = xaml.Substring(xaml.IndexOf("x:Name=\"McpPopup\"", StringComparison.Ordinal));

            string[] paths = Regex.Matches(panels, @"\{Binding (McpServers|Plugins)\.([A-Za-z0-9_.]+?)(?:,|\})")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value + "." + m.Groups[2].Value)
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray();

            Assert.True(paths.Length >= 12, $"only {paths.Length} distinct binding path(s) found - the regex probably stopped matching");

            foreach (string path in paths)
                Assert.True(Resolves(path), $"binding {path} does not resolve to a real member");

            // CONTROL: the same walker must reject a path that does not exist.
            Assert.False(Resolves("McpServers.Serverz"));
        }

        private static bool Resolves(string path)
        {
            Type type = typeof(ChatSessionViewModel);

            foreach (string segment in path.Split('.'))
            {
                PropertyInfo? property = type.GetProperty(segment);
                if (property == null)
                    return false;

                type = property.PropertyType;
            }

            return true;
        }
    }
}
