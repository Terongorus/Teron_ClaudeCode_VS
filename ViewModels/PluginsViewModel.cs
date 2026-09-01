using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using TeronClaudeCodeVS.Core;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>One installed or available plugin.</summary>
    public sealed class PluginEntry(string name, string marketplace, string version, string? description, string? scope, bool isInstalled, bool isEnabled)
    {

        /// <summary>Plugin name without its marketplace, e.g. "code-review".</summary>
        public string Name { get; } = name;

        /// <summary>The marketplace it came from, e.g. "anthropics".</summary>
        public string Marketplace { get; } = marketplace;

        public string Version { get; } = version;

        /// <summary>Only the marketplace catalog carries descriptions; installed rows have none.</summary>
        public string? Description { get; } = description;

        /// <summary>"user", "project" or "local" for an installed plugin; null for an available one.</summary>
        public string? Scope { get; } = scope;

        public bool IsInstalled { get; } = isInstalled;
        public bool IsEnabled { get; } = isEnabled;

        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

        /// <summary>What `claude plugin install` would be given, e.g. "code-review@anthropics".</summary>
        public string Id => Marketplace.Length > 0 ? Name + "@" + Marketplace : Name;

        /// <summary>Second line of a row: version, scope and enablement, whichever apply.</summary>
        public string DetailLine
        {
            get
            {
                var parts = new List<string>();
                if (Version.Length > 0) parts.Add("v" + Version);
                if (Marketplace.Length > 0) parts.Add(Marketplace);
                if (!string.IsNullOrEmpty(Scope)) parts.Add(Scope!);
                if (IsInstalled) parts.Add(IsEnabled ? "enabled" : "disabled");
                return string.Join(" · ", parts);
            }
        }
    }

    /// <summary>One configured marketplace.</summary>
    public sealed class MarketplaceEntry(string name, string source, string? path)
    {
        public string Name { get; } = name;

        /// <summary>Raw source kind from the CLI, e.g. "directory", "github", "git".</summary>
        public string Source { get; } = source;

        /// <summary>Local path or remote URL, when the CLI reported one.</summary>
        public string? Path { get; } = path;

        public string DetailLine
        {
            get
            {
                string kind = Source.Length > 0
                    ? char.ToUpperInvariant(Source[0]) + Source.Substring(1)
                    : "Unknown";
                return string.IsNullOrWhiteSpace(Path) ? kind : kind + " · " + Path;
            }
        }
    }

    /// <summary>Which half of the plugins panel is showing.</summary>
    public enum PluginsTab
    {
        Plugins,
        Marketplaces,
    }

    /// <summary>
    /// FEAT-5. Backs the Manage plugins panel: baseline's second real GUI surface in "Customize",
    /// a modal with a <b>Plugins / Marketplaces</b> tab strip. TerminalHandoffCatalog explains why
    /// baseline skips `plugins` when building its hand-off rows - this panel is the reason.
    ///
    /// <para><b>Both queries ask for JSON</b>, unlike the MCP panel: `claude plugin list` and
    /// `claude plugin marketplace list` each accept `--json` (confirmed against the shipped CLI's
    /// own `--help`), so there is no text to parse and no format to guess. `--available` is only
    /// honoured together with `--json`, and it changes the shape of the answer - the bare form
    /// returns an array of installed plugins, and `--json --available` returns
    /// <c>{ "installed": [...], "available": [...] }</c>. Both shapes are accepted below, because
    /// one of them is what an older CLI would print.</para>
    ///
    /// <para><b>Measured field names</b> (against a local fixture marketplace, 2026-08-30):
    /// installed rows carry <c>id</c> ("name@marketplace"), <c>version</c>, <c>scope</c>,
    /// <c>enabled</c>, <c>installPath</c>, <c>installedAt</c>, <c>lastUpdated</c> - note there is
    /// no description on an installed row. Available rows carry <c>pluginId</c>, <c>name</c>,
    /// <c>description</c>, <c>marketplaceName</c>, <c>version</c> and <c>source</c>. Marketplaces
    /// carry <c>name</c>, <c>source</c>, <c>path</c> and <c>installLocation</c>.</para>
    ///
    /// <para><b>On the empty state.</b> FEAT-5's acceptance criterion is baseline's own sentence,
    /// "No plugins available. Add a marketplace to discover plugins." - which is right when there
    /// is no marketplace to discover anything from, and misleading once there is one. So it is used
    /// verbatim in exactly the case it describes, and the CLI's own "No plugins installed…"
    /// sentence is used when marketplaces exist but nothing is installed. This is a deliberate,
    /// documented divergence, not a transcription slip.</para>
    /// </summary>
    public sealed class PluginsViewModel : ObservableObject
    {
        /// <summary>Baseline's wording, shown when there is no marketplace configured at all.</summary>
        public const string NoMarketplacesEmptyState = "No plugins available. Add a marketplace to discover plugins.";

        /// <summary>The CLI's own wording, shown when marketplaces exist but nothing is installed.</summary>
        public const string NoPluginsInstalledEmptyState = "No plugins installed. Use `claude plugin install` to install a plugin.";

        /// <summary>
        /// The CLI prints exactly "No marketplaces configured"; the second sentence is ours, added
        /// because a panel that only states a fact leaves the reader with nowhere to go.
        /// </summary>
        public const string NoMarketplacesText = "No marketplaces configured. Use `claude plugin marketplace add` to add one.";

        public const string LearnMoreUrl = "https://code.claude.com/docs/en/plugins";

        public ObservableCollection<PluginEntry> InstalledPlugins { get; } = [];
        public ObservableCollection<PluginEntry> AvailablePlugins { get; } = [];
        public ObservableCollection<MarketplaceEntry> Marketplaces { get; } = [];

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }

        private string? _loadError;
        public string? LoadError { get => _loadError; private set => SetField(ref _loadError, value); }

        private bool _hasLoaded;
        public bool HasLoaded { get => _hasLoaded; private set => SetField(ref _hasLoaded, value); }

        // ── Tab strip ─────────────────────────────────────────────────────────

        private PluginsTab _selectedTab = PluginsTab.Plugins;
        public PluginsTab SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (SetField(ref _selectedTab, value))
                {
                    OnPropertyChanged(nameof(IsPluginsTab));
                    OnPropertyChanged(nameof(IsMarketplacesTab));
                }
            }
        }

        public bool IsPluginsTab => SelectedTab == PluginsTab.Plugins;
        public bool IsMarketplacesTab => SelectedTab == PluginsTab.Marketplaces;

        // ── Empty states ──────────────────────────────────────────────────────

        /// <summary>True when the Plugins tab has nothing at all to list.</summary>
        public bool IsPluginListEmpty => InstalledPlugins.Count == 0 && AvailablePlugins.Count == 0;

        /// <summary>See the class remarks: which sentence applies depends on whether a marketplace exists.</summary>
        public string PluginsEmptyStateText =>
            Marketplaces.Count == 0 ? NoMarketplacesEmptyState : NoPluginsInstalledEmptyState;

        public string MarketplacesEmptyStateText => NoMarketplacesText;

        /// <summary>
        /// Runs both list commands and replaces the three collections. Never throws.
        /// Generous timeout: listing available plugins can refresh a marketplace catalog first.
        /// </summary>
        public async Task RefreshAsync(string? claudePath, string workingDirectory)
        {
            if (IsLoading) return;

            IsLoading = true;
            LoadError = null;

            try
            {
                ClaudeCliResult pluginsResult = await ClaudeCliQuery
                    .RunAsync(claudePath, "plugin list --json --available", workingDirectory, timeoutMs: 30000)
                    .ConfigureAwait(true);

                ClaudeCliResult marketResult = await ClaudeCliQuery
                    .RunAsync(claudePath, "plugin marketplace list --json", workingDirectory, timeoutMs: 30000)
                    .ConfigureAwait(true);

                var errors = new List<string>();
                if (!pluginsResult.Succeeded && pluginsResult.ErrorMessage != null) errors.Add(pluginsResult.ErrorMessage);
                if (!marketResult.Succeeded && marketResult.ErrorMessage != null) errors.Add(marketResult.ErrorMessage);
                LoadError = errors.Count == 0 ? null : string.Join(" ", errors);

                Replace(InstalledPlugins, ParseInstalled(pluginsResult.StdOut));
                Replace(AvailablePlugins, ParseAvailable(pluginsResult.StdOut));
                Replace(Marketplaces, ParseMarketplaces(marketResult.StdOut));

                OnPropertyChanged(nameof(IsPluginListEmpty));
                OnPropertyChanged(nameof(PluginsEmptyStateText));

                HasLoaded = errors.Count == 0;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
        {
            target.Clear();
            foreach (T item in items) target.Add(item);
        }

        // ── Parsing ───────────────────────────────────────────────────────────

        /// <summary>
        /// Accepts both shapes `claude plugin list --json` can produce: a bare array of installed
        /// plugins, or the `{installed, available}` object that `--available` switches it to.
        /// </summary>
        internal static List<PluginEntry> ParseInstalled(string? json)
        {
            var entries = new List<PluginEntry>();
            JToken? root = TryParse(json);
            if (root == null) return entries;

            JToken? array = root as JArray ?? root["installed"];
            if (array is not JArray installed) return entries;

            foreach (JToken item in installed)
            {
                if (item is not JObject obj) continue;

                string id = (string?)obj["id"] ?? (string?)obj["pluginId"] ?? (string?)obj["name"] ?? "";
                if (id.Length == 0) continue;

                SplitId(id, out string name, out string marketplace);
                entries.Add(new PluginEntry(
                    name,
                    (string?)obj["marketplaceName"] ?? marketplace,
                    (string?)obj["version"] ?? "",
                    (string?)obj["description"],
                    (string?)obj["scope"],
                    isInstalled: true,
                    isEnabled: (bool?)obj["enabled"] ?? true));
            }

            return entries;
        }

        /// <summary>Available-but-not-installed plugins. Empty unless `--available` was passed.</summary>
        internal static List<PluginEntry> ParseAvailable(string? json)
        {
            var entries = new List<PluginEntry>();
            JToken? root = TryParse(json);
            if (root is not JObject obj) return entries;
            if (obj["available"] is not JArray available) return entries;

            foreach (JToken item in available)
            {
                if (item is not JObject entry) continue;

                string id = (string?)entry["pluginId"] ?? (string?)entry["id"] ?? "";
                string name = (string?)entry["name"] ?? "";
                if (name.Length == 0 && id.Length == 0) continue;

                SplitId(id.Length > 0 ? id : name, out string fromId, out string marketplace);
                entries.Add(new PluginEntry(
                    name.Length > 0 ? name : fromId,
                    (string?)entry["marketplaceName"] ?? marketplace,
                    (string?)entry["version"] ?? "",
                    (string?)entry["description"],
                    scope: null,
                    isInstalled: false,
                    isEnabled: false));
            }

            return entries;
        }

        internal static List<MarketplaceEntry> ParseMarketplaces(string? json)
        {
            var entries = new List<MarketplaceEntry>();
            JToken? root = TryParse(json);

            JToken? array = root as JArray ?? root?["marketplaces"];
            if (array is not JArray marketplaces) return entries;

            foreach (JToken item in marketplaces)
            {
                if (item is not JObject obj) continue;

                string name = (string?)obj["name"] ?? "";
                if (name.Length == 0) continue;

                entries.Add(new MarketplaceEntry(
                    name,
                    (string?)obj["source"] ?? "",
                    (string?)obj["path"] ?? (string?)obj["url"] ?? (string?)obj["installLocation"]));
            }

            return entries;
        }

        /// <summary>"code-review@anthropics" -> ("code-review", "anthropics").</summary>
        internal static void SplitId(string id, out string name, out string marketplace)
        {
            int at = id.LastIndexOf('@');
            if (at > 0 && at < id.Length - 1)
            {
                name = id.Substring(0, at);
                marketplace = id.Substring(at + 1);
            }
            else
            {
                name = id;
                marketplace = "";
            }
        }

        /// <summary>
        /// Parses CLI stdout as JSON, tolerating anything the CLI printed before it (an update
        /// notice, say). Returns null when there is no JSON in there at all.
        /// </summary>
        private static JToken? TryParse(string? output)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;

            string text = output!.Trim();
            try { return JToken.Parse(text); }
            catch { }

            int start = text.IndexOfAny(['{', '[']);
            if (start < 0) return null;

            char open = text[start];
            char close = open == '{' ? '}' : ']';
            int end = text.LastIndexOf(close);
            if (end <= start) return null;

            try { return JToken.Parse(text.Substring(start, end - start + 1)); }
            catch { return null; }
        }
    }
}
