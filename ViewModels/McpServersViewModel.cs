using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using TeronClaudeCodeVS.Core;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>How an MCP server's status line should read to the eye. Drives colour only.</summary>
    public enum McpStatusKind
    {
        Unknown,
        Connected,
        Warning,
        Error,
        Pending,
        Disabled,
    }

    /// <summary>One row of `claude mcp list`.</summary>
    public sealed class McpServerEntry(string name, string target, string transport, string status, string? issue, McpStatusKind kind)
    {

        /// <summary>Server name as configured, e.g. "github".</summary>
        public string Name { get; } = name;

        /// <summary>The URL for a remote server, or the full command line for a stdio one.</summary>
        public string Target { get; } = target;

        /// <summary>"HTTP", "SSE", "stdio", or "" when the CLI printed no transport marker.</summary>
        public string Transport { get; } = transport;

        /// <summary>Status exactly as the CLI printed it, glyph included, e.g. "✓ Connected".</summary>
        public string Status { get; } = status;

        /// <summary>The detail the CLI appends after an em dash on a failure, when there is one.</summary>
        public string? Issue { get; } = issue;

        public McpStatusKind Kind { get; } = kind;

        public bool HasIssue => !string.IsNullOrEmpty(Issue);
        public bool HasTransport => Transport.Length > 0;
    }

    /// <summary>
    /// FEAT-4. Backs the MCP servers panel - one of only two real GUI surfaces in baseline's
    /// "Customize" section (the other five are terminal hand-offs; see TerminalHandoffCatalog).
    ///
    /// <para><b>Why this parses text rather than JSON.</b> Every other CLI query in this extension
    /// asks for `--json`. `claude mcp list` has no such flag - confirmed against the shipped CLI's
    /// own `claude mcp list --help`, whose only option is `-h`. So the panel parses the printed
    /// lines, and the format below was not guessed: it was read out of the CLI binary's own
    /// renderer, which builds each line as</para>
    /// <code>
    ///   sse:            `${name}: ${url} (SSE) - ${o}`
    ///   http:           `${name}: ${url} (HTTP) - ${o}`
    ///   claudeai-proxy: `${name}: ${url} - ${o}`
    ///   stdio:          `${name}: ${command} ${args.join(" ")} - ${o}`
    ///   where          o = issue ? `${status} — ${issue}` : status      (that dash is an em dash)
    /// </code>
    /// <para>and whose complete status vocabulary is: <c>✓ Connected</c>,
    /// <c>! Connected · tools fetch failed</c>, <c>! Needs authentication</c>,
    /// <c>- Not configured</c>, <c>✗ Failed to connect</c>, <c>✗ Connection error</c>,
    /// <c>⏸ Pending approval (run `claude` to approve)</c>,
    /// <c>✗ Rejected (see disabledMcpjsonServers in settings)</c> and
    /// <c>⊘ Disabled for this project (re-enable via /mcp)</c>. The status text is never
    /// re-worded here - it is the CLI's own sentence, and it stays that way.</para>
    ///
    /// <para><b>The empty state is the CLI's string, not a copy of it.</b> Baseline's panel shows
    /// "No MCP servers configured. Use `claude mcp add` to add a server." because that is precisely
    /// what the command prints when nothing is configured (verified live). We surface whatever the
    /// command actually said, so the two cannot drift apart. The constant below is only the
    /// fallback for a run that produced no output at all.</para>
    /// </summary>
    public sealed class McpServersViewModel : ObservableObject
    {
        /// <summary>Printed by the CLI while it health-checks; never a server row.</summary>
        private const string HealthCheckLine = "Checking MCP server health";

        /// <summary>Fallback only - normally the CLI's own line is shown verbatim. See class remarks.</summary>
        public const string DefaultEmptyState = "No MCP servers configured. Use `claude mcp add` to add a server.";

        public const string LearnMoreUrl = "https://code.claude.com/docs/en/mcp";

        public ObservableCollection<McpServerEntry> Servers { get; } = [];

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }

        private string? _loadError;
        public string? LoadError { get => _loadError; private set => SetField(ref _loadError, value); }

        private bool _hasLoaded;
        public bool HasLoaded { get => _hasLoaded; private set => SetField(ref _hasLoaded, value); }

        private string _emptyStateText = DefaultEmptyState;
        public string EmptyStateText { get => _emptyStateText; private set => SetField(ref _emptyStateText, value); }

        /// <summary>
        /// The directory the query ran in, shown under the title. `mcp list` resolves project-scoped
        /// servers relative to it, so which directory was used is part of the answer.
        /// </summary>
        private string _scopeDirectory = "";
        public string ScopeDirectory { get => _scopeDirectory; private set => SetField(ref _scopeDirectory, value); }

        /// <summary>
        /// Runs `claude mcp list` and replaces the list with what it reported. Never throws.
        /// The health check contacts every configured server, so the timeout is generous.
        /// </summary>
        public async Task RefreshAsync(string? claudePath, string workingDirectory)
        {
            if (IsLoading) return;

            IsLoading = true;
            LoadError = null;
            ScopeDirectory = workingDirectory ?? "";

            try
            {
                ClaudeCliResult result = await ClaudeCliQuery
                    .RunAsync(claudePath, "mcp list", workingDirectory, timeoutMs: 30000)
                    .ConfigureAwait(true);

                // A non-zero exit with usable stdout still tells the user something, but an outright
                // failure to run must not be dressed up as "no servers configured".
                if (!result.Succeeded && string.IsNullOrWhiteSpace(result.StdOut))
                {
                    Servers.Clear();
                    LoadError = result.ErrorMessage;
                    EmptyStateText = DefaultEmptyState;
                    return;
                }

                Servers.Clear();
                foreach (McpServerEntry entry in Parse(result.StdOut))
                    Servers.Add(entry);

                EmptyStateText = Servers.Count == 0 ? ExtractEmptyState(result.StdOut) : DefaultEmptyState;
                HasLoaded = true;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>Parses `claude mcp list` output into rows. Lines it does not recognise are ignored.</summary>
        internal static List<McpServerEntry> Parse(string? output)
        {
            var entries = new List<McpServerEntry>();
            if (string.IsNullOrWhiteSpace(output)) return entries;

            foreach (string rawLine in output!.Split('\n'))
            {
                string line = rawLine.Trim('\r', ' ', '\t');
                if (line.Length == 0) continue;
                if (line.StartsWith(HealthCheckLine, StringComparison.Ordinal)) continue;

                McpServerEntry? entry = ParseLine(line);
                if (entry != null) entries.Add(entry);
            }

            return entries;
        }

        private static McpServerEntry? ParseLine(string line)
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0) return null;

            string name = line.Substring(0, colon).Trim();
            string rest = line.Substring(colon + 2);

            // The status is always last, after the final " - ". No status in the CLI's vocabulary
            // contains that sequence (a status's own detail is appended after an em dash instead),
            // so searching from the right is safe even when a stdio command line contains " - ".
            int sep = rest.LastIndexOf(" - ", StringComparison.Ordinal);
            if (sep < 0) return null;

            string target = rest.Substring(0, sep).Trim();
            string statusPart = rest.Substring(sep + 3).Trim();

            // One status in the CLI's vocabulary begins with the separator's own characters:
            // "- Not configured". Rendered, that makes "name: cmd - - Not configured", and the
            // rightmost " - " lands one character too late, leaving a stray dash on the target and
            // eating the status's leading marker. Detected by exactly that stray dash, and undone.
            if (target.EndsWith(" -", StringComparison.Ordinal))
            {
                target = target.Substring(0, target.Length - 2).TrimEnd();
                statusPart = "- " + statusPart;
            }

            if (name.Length == 0 || target.Length == 0 || statusPart.Length == 0) return null;

            string transport = "";
            if (target.EndsWith("(HTTP)", StringComparison.Ordinal))
            {
                transport = "HTTP";
                target = target.Substring(0, target.Length - "(HTTP)".Length).Trim();
            }
            else if (target.EndsWith("(SSE)", StringComparison.Ordinal))
            {
                transport = "SSE";
                target = target.Substring(0, target.Length - "(SSE)".Length).Trim();
            }
            else if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // No marker and not a URL: the CLI's stdio branch, which prints "command args".
                transport = "stdio";
            }

            string status = statusPart;
            string? issue = null;
            int emDash = statusPart.IndexOf(" — ", StringComparison.Ordinal);
            if (emDash > 0)
            {
                status = statusPart.Substring(0, emDash).Trim();
                issue = statusPart.Substring(emDash + 3).Trim();
                if (issue.Length == 0) issue = null;
            }

            return new McpServerEntry(name, target, transport, status, issue, Classify(status));
        }

        /// <summary>
        /// Classifies by the words, never by the leading glyph: the CLI picks its tick and cross
        /// characters from the terminal's unicode support, so the glyph is not stable but the
        /// sentence is. Order matters - "Connected · tools fetch failed" is a warning, not a
        /// success, and must be tested before the plain "Connected".
        /// </summary>
        internal static McpStatusKind Classify(string status)
        {
            if (status.IndexOf("Pending approval", StringComparison.OrdinalIgnoreCase) >= 0)
                return McpStatusKind.Pending;
            // Rejection is tested before disablement, and this is not a stylistic choice: the
            // rejection status names the setting that caused it - "✗ Rejected (see
            // disabledMcpjsonServers in settings)" - so a case-insensitive search for "disabled"
            // matches it first and reports a rejected server as merely switched off.
            if (status.IndexOf("Rejected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                status.IndexOf("Failed to connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                status.IndexOf("Connection error", StringComparison.OrdinalIgnoreCase) >= 0)
                return McpStatusKind.Error;
            if (status.IndexOf("Disabled for this project", StringComparison.OrdinalIgnoreCase) >= 0)
                return McpStatusKind.Disabled;
            if (status.IndexOf("tools fetch failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                status.IndexOf("Needs authentication", StringComparison.OrdinalIgnoreCase) >= 0 ||
                status.IndexOf("Not configured", StringComparison.OrdinalIgnoreCase) >= 0)
                return McpStatusKind.Warning;
            if (status.IndexOf("Connected", StringComparison.OrdinalIgnoreCase) >= 0)
                return McpStatusKind.Connected;
            return McpStatusKind.Unknown;
        }

        /// <summary>
        /// The CLI's own "nothing configured" sentence, so the panel says exactly what the command
        /// said. Falls back to the known wording when the command printed nothing at all.
        /// </summary>
        internal static string ExtractEmptyState(string? output)
        {
            if (string.IsNullOrWhiteSpace(output)) return DefaultEmptyState;

            foreach (string rawLine in output!.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith(HealthCheckLine, StringComparison.Ordinal)) continue;
                return line;
            }

            return DefaultEmptyState;
        }
    }
}
