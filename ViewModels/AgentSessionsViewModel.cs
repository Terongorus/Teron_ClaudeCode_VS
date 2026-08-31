using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TeronClaudeCodeVS.Core;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>
    /// FEAT-9. One row of `claude agents --json`.
    ///
    /// <para><b>Four of these fields are optional, and which ones are missing is what tells you the
    /// session's shape.</b> That was established by watching the same session through its whole
    /// life rather than by reading a schema:</para>
    /// <code>
    ///   interactive, alive:      pid, cwd, kind, startedAt, sessionId, name
    ///   background, alive:       pid, id, cwd, kind, startedAt, sessionId, name, status, state
    ///   background, stopped:          id, cwd, kind, startedAt, sessionId, name,         state
    /// </code>
    /// <para>So <c>pid</c> present means "a process is running it right now", <c>id</c> present
    /// means "this is a background agent and has a short id the CLI's own commands accept", and
    /// <c>status</c> only ever accompanies a live background one. Treating any of them as required
    /// produces a parser that works until the first stopped agent.</para>
    /// </summary>
    public sealed class AgentSessionEntry
    {
        public AgentSessionEntry(string sessionId, string? shortId, string name, string cwd, string kind,
            int? pid, string? status, string? state, DateTime startedUtc, string relativeAge, bool isCurrentFolder)
        {
            SessionId = sessionId;
            ShortId = shortId;
            Name = name;
            Cwd = cwd;
            Kind = kind;
            Pid = pid;
            Status = status;
            State = state;
            StartedUtc = startedUtc;
            RelativeAge = relativeAge;
            IsCurrentFolder = isCurrentFolder;
        }

        /// <summary>The full session uuid - what `--resume` takes.</summary>
        public string SessionId { get; }

        /// <summary>The 8-character id `claude attach|logs|stop` take, or null for an interactive session.</summary>
        public string? ShortId { get; }

        /// <summary>The CLI's own generated name, e.g. "teron-extensions-81" or "reply to pong".</summary>
        public string Name { get; }

        public string Cwd { get; }

        /// <summary>"interactive" or "background".</summary>
        public string Kind { get; }

        /// <summary>Null once the process is gone, which is the only reliable "is it running" signal.</summary>
        public int? Pid { get; }

        public string? Status { get; }
        public string? State { get; }
        public DateTime StartedUtc { get; }
        public string RelativeAge { get; }

        /// <summary>True when this session was started inside the folder open in the IDE.</summary>
        public bool IsCurrentFolder { get; }

        public bool IsBackground => string.Equals(Kind, "background", StringComparison.OrdinalIgnoreCase);
        public bool IsRunning => Pid.HasValue;

        /// <summary>
        /// The arguments the terminal hand-off should run for this row, or null when there is no
        /// sensible one. Which command it is depends entirely on the state:
        /// <list type="bullet">
        /// <item>a <b>live background</b> agent has a short id and the CLI's own
        /// `claude attach &lt;id&gt;`, described by it as opening the session "in this terminal";</item>
        /// <item>anything <b>not running</b> is resumed the ordinary way, `claude --resume
        /// &lt;sessionId&gt;`, started in the directory the session belongs to;</item>
        /// <item>a <b>live interactive</b> session is already open in a window somewhere - there is
        /// no CLI command that joins one, and inventing a second process is exactly what should not
        /// happen, so it gets no action.</item>
        /// </list>
        /// </summary>
        public IReadOnlyList<string>? TerminalArgs
        {
            get
            {
                if (IsRunning)
                    return IsBackground && !string.IsNullOrEmpty(ShortId)
                        ? new List<string> { "attach", ShortId! }
                        : null;

                return new List<string> { "--resume", SessionId };
            }
        }

        public bool CanOpenInTerminal => TerminalArgs != null;

        /// <summary>"claude attach e6e765fd" - shown as the button's tooltip, so the hand-off is not a mystery.</summary>
        public string TerminalCommandText
        {
            get
            {
                IReadOnlyList<string>? args = TerminalArgs;
                return args == null
                    ? "This session is already open in another window."
                    : "Runs `claude " + string.Join(" ", args) + "` in " + Cwd;
            }
        }

        /// <summary>
        /// Two independent conditions, both real.
        ///
        /// <para>A running session is excluded because resuming it here would put a second process
        /// on one conversation - the CLI's own `attach` exists precisely so that does not happen.</para>
        ///
        /// <para>A session from another folder is excluded because this panel resumes into the
        /// folder open in the IDE, and that folder is not incidental: it is where `--resume` looks
        /// for the transcript, what `@`-references resolve against, and what the IDE companion
        /// server reports. Silently re-pointing the panel at another directory would break all
        /// three, so a session from elsewhere is handed to a terminal that can genuinely start in
        /// its own directory.</para>
        /// </summary>
        public bool CanOpenHere => !IsRunning && IsCurrentFolder;

        /// <summary>"background · done · 4m ago", assembled once so the row template stays a binding.</summary>
        public string DetailLine
        {
            get
            {
                var parts = new List<string> { Kind };
                if (!string.IsNullOrEmpty(State)) parts.Add(State!);
                else if (IsRunning) parts.Add("running");
                parts.Add(RelativeAge);
                return string.Join(" · ", parts);
            }
        }

        /// <summary>Why "Open here" is unavailable, or null when it is available.</summary>
        public string? OpenHereBlockedReason
        {
            get
            {
                if (CanOpenHere) return null;
                if (IsRunning)
                    return $"This session is running right now (pid {Pid}). Stop it, or attach to it in a " +
                           "terminal, rather than opening a second process against the same conversation.";
                return "This session was started in " + Cwd + ", and this panel resumes into the folder open " +
                       "in the IDE. Open it in a terminal instead, which can start in its own directory.";
            }
        }

        /// <summary>The prompt itself is the row - see RewindPoint.ToString for what this prevents.</summary>
        public override string ToString() => Name;
    }

    /// <summary>
    /// FEAT-9. The two halves of "sessions elsewhere" that the CLI actually exposes, and an honest
    /// account of the half it does not.
    ///
    /// <para><b>Running sessions - real.</b> `claude agents --json --all` prints every session on
    /// this machine, interactive and background, as a JSON array, and explicitly "does not require a
    /// TTY", which is what makes it usable from a tool window. <c>--all</c> was not taken on faith:
    /// with a background agent alive both forms return it, and only after `claude stop` does the
    /// plain form drop it while <c>--all</c> keeps it. So <c>--all</c> means "include agents whose
    /// process has exited", and without it a finished agent is simply gone.</para>
    ///
    /// <para><b>Cloud sessions by id or URL - real, but not in this panel.</b> `claude --cloud
    /// &lt;id|url&gt;` attaches to a cloud session, and the CLI accepts `session_…`, `cse_…` and
    /// `https://claude.ai/code/&lt;id&gt;`. It refuses one thing: <c>--cloud &lt;session_id&gt; does
    /// not support --output-format stream-json</c>, in its own words - and stream-json is the entire
    /// protocol this chat panel speaks. A cloud session therefore cannot be rendered here at all, so
    /// the Cloud tab hands off to a terminal instead of pretending otherwise.</para>
    ///
    /// <para><b>Listing the account's cloud sessions - genuinely not available.</b> There is no
    /// `claude cloud list`. The complete command list is agents, auth, auto-mode, doctor, gateway,
    /// import, install, mcp, plugin, project, setup-token, ultrareview and update; the only
    /// cloud-facing flags anywhere in `--help` are <c>--cloud</c> and <c>--environment</c>, and
    /// neither enumerates. Baseline's History ▸ Web tab lists sessions by machine name because the
    /// extension talks to an account endpoint the CLI does not expose. That gap is stated in the tab
    /// rather than filled with something that looks like it.</para>
    /// </summary>
    public sealed class AgentSessionsViewModel : ObservableObject
    {
        public const string EmptyStateText =
            "No other Claude Code sessions are running on this machine.";

        /// <summary>
        /// The CLI's own rule for a cloud session id, transcribed from its validator: a `session_`
        /// or `cse_` prefix, only URL-safe characters, and something after the prefix.
        /// </summary>
        private const string CloudIdCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-";

        public ObservableCollection<AgentSessionEntry> Sessions { get; } = new ObservableCollection<AgentSessionEntry>();

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; private set => SetField(ref _isLoading, value); }

        private string? _loadError;
        public string? LoadError { get => _loadError; private set => SetField(ref _loadError, value); }

        private bool _hasLoaded;
        public bool HasLoaded { get => _hasLoaded; private set => SetField(ref _hasLoaded, value); }

        public bool IsEmpty => HasLoaded && !IsLoading && LoadError == null && Sessions.Count == 0;

        public async Task RefreshAsync(string? claudePath, string workingDirectory)
        {
            IsLoading = true;
            LoadError = null;

            ClaudeCliResult result = await ClaudeCliQuery
                .RunAsync(claudePath, "agents --json --all", workingDirectory, timeoutMs: 20000)
                .ConfigureAwait(true);

            Sessions.Clear();

            if (!result.Succeeded)
            {
                LoadError = result.ErrorMessage;
            }
            else
            {
                try
                {
                    foreach (AgentSessionEntry entry in Parse(result.StdOut, workingDirectory, DateTime.UtcNow))
                        Sessions.Add(entry);
                }
                catch (Exception ex)
                {
                    LoadError = "`claude agents --json` returned output this panel could not read: " + ex.Message;
                }
            }

            HasLoaded = true;
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }

        /// <summary>
        /// Parses the array, newest first, with sessions started inside the open folder ahead of the
        /// rest. Sorting rather than filtering is deliberate: `--cwd` would push the filtering into
        /// the CLI, but it matches a whole subtree, so a session in a sibling project silently
        /// disappears instead of being listed as what it is.
        /// </summary>
        internal static List<AgentSessionEntry> Parse(string json, string workingDirectory, DateTime nowUtc)
        {
            var entries = new List<AgentSessionEntry>();
            if (string.IsNullOrWhiteSpace(json)) return entries;

            JArray array = JArray.Parse(json);
            foreach (JToken token in array)
            {
                if (!(token is JObject row)) continue;

                string sessionId = (string?)row["sessionId"] ?? "";
                if (sessionId.Length == 0) continue;

                string cwd = (string?)row["cwd"] ?? "";
                DateTime started = FromEpochMilliseconds((long?)row["startedAt"]);

                entries.Add(new AgentSessionEntry(
                    sessionId: sessionId,
                    shortId: (string?)row["id"],
                    name: (string?)row["name"] ?? sessionId,
                    cwd: cwd,
                    kind: (string?)row["kind"] ?? "interactive",
                    pid: (int?)row["pid"],
                    status: (string?)row["status"],
                    state: (string?)row["state"],
                    startedUtc: started,
                    relativeAge: RewindPoint.DescribeAge(started, nowUtc),
                    isCurrentFolder: IsSameFolder(cwd, workingDirectory)));
            }

            entries.Sort((a, b) =>
            {
                if (a.IsCurrentFolder != b.IsCurrentFolder) return a.IsCurrentFolder ? -1 : 1;
                return b.StartedUtc.CompareTo(a.StartedUtc);
            });

            return entries;
        }

        /// <summary>
        /// Epoch milliseconds - what `startedAt` carries. <c>DateTimeOffset.FromUnixTimeMilliseconds</c>
        /// throws outside its range, and a row with a nonsense timestamp should still be listed.
        /// </summary>
        private static DateTime FromEpochMilliseconds(long? value)
        {
            if (value == null) return DateTime.UtcNow;
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value.Value).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Case- and separator-insensitive, because the CLI reports `d:\Projects\…` while the IDE
        /// hands us whatever the solution file said.
        /// </summary>
        internal static bool IsSameFolder(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path) =>
            path.Replace('/', '\\').TrimEnd('\\');

        /// <summary>
        /// Turns whatever was pasted into the id `--cloud` wants, or null when it is not one.
        ///
        /// <para>A URL is reduced to its last path segment - `https://claude.ai/code/session_x` and
        /// the bare `session_x` both reach the CLI as the same thing, and both were accepted by it.
        /// The rule applied afterwards is the CLI's own, transcribed from its validator rather than
        /// invented: a `session_` or `cse_` prefix, characters from [A-Za-z0-9_-] only, and a
        /// non-empty remainder.</para>
        ///
        /// <para>This gates the button, not the argument. What the user typed is what gets passed,
        /// and a server-side rejection is shown in the CLI's words - the client-side rule exists to
        /// catch a typo before a terminal opens, not to be the authority on what a valid id is.</para>
        /// </summary>
        internal static string? NormalizeCloudId(string? pasted)
        {
            if (string.IsNullOrWhiteSpace(pasted)) return null;
            string text = pasted!.Trim();

            if (text.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                try
                {
                    var uri = new Uri(text);
                    string[] segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (segments.Length == 0) return null;
                    text = segments[segments.Length - 1];
                }
                catch (UriFormatException)
                {
                    return null;
                }
            }

            bool tagged = text.StartsWith("session_", StringComparison.Ordinal)
                       || text.StartsWith("cse_", StringComparison.Ordinal);
            if (!tagged) return null;

            int underscore = text.IndexOf('_');
            if (underscore < 0 || underscore == text.Length - 1) return null;

            foreach (char c in text)
            {
                if (CloudIdCharacters.IndexOf(c) < 0) return null;
            }

            return text;
        }

        /// <summary>The hint under the paste box, in the CLI's own vocabulary.</summary>
        public static string DescribeCloudInput(string? pasted)
        {
            if (string.IsNullOrWhiteSpace(pasted))
                return "Paste a session ID (session_… or cse_…) or a claude.ai/code link.";

            return NormalizeCloudId(pasted) != null
                ? "Opens in a terminal — cloud sessions cannot stream into this panel."
                : "That is not a cloud session ID or URL.";
        }

        /// <summary>Kept next to the parser it belongs to, so the format string has one home.</summary>
        internal static string FormatStartedTooltip(DateTime startedUtc) =>
            "Started " + startedUtc.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
    }
}
