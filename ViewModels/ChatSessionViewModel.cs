using TeronClaudeCodeVS.Core;
using TeronClaudeCodeVS.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TeronClaudeCodeVS.ViewModels
{
    public sealed class ModelOption
    {
        public string DisplayName { get; }

        /// <summary>Value passed to `--model`, or null to let the CLI pick its default.</summary>
        public string? Value { get; }

        /// <summary>
        /// UX-1: one-line decision-support subtitle shown under the name in the model picker.
        /// The wording is lifted from the CLI's own model table (read out of the shipped binary)
        /// rather than invented here, so the cost and credit implications a user sees in this
        /// window are the same ones the CLI itself states. Null hides the line.
        /// </summary>
        public string? Description { get; }

        public ModelOption(string displayName, string? value, string? description = null)
        {
            DisplayName = displayName;
            Value = value;
            Description = description;
        }

        public override string ToString() => DisplayName;
    }

    public sealed class PermissionModeOption
    {
        public string DisplayName { get; }

        /// <summary>Value passed to `--permission-mode`, or null to omit the flag (CLI default).</summary>
        public string? Value { get; }

        /// <summary>
        /// UX-2: one-line explanation of what the mode actually does, taken from the CLI's own
        /// documented permission-mode semantics rather than inferred from the mode's name. That
        /// distinction matters most for "Don't Ask", whose name reads like auto-approve but whose
        /// real behaviour is to deny anything not already pre-approved. Null hides the line.
        /// </summary>
        public string? Description { get; }

        public PermissionModeOption(string displayName, string? value, string? description = null)
        {
            DisplayName = displayName;
            Value = value;
            Description = description;
        }

        public override string ToString() => DisplayName;
    }

    public sealed class ThinkingLevelOption
    {
        public string DisplayName { get; }

        /// <summary>Value passed to the CLI's <c>--effort</c> flag, or null to omit the flag (CLI default).</summary>
        public string? EffortArg { get; }

        public ThinkingLevelOption(string displayName, string? effortArg)
        {
            DisplayName = displayName;
            EffortArg = effortArg;
        }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// Display-density level for the transcript. Original UI design - not a real-extension feature
    /// (confirmed via direct research against the installed VS Code extension bundle, 2026-08-27).
    /// Summary = final text + result footer only, thinking hidden, tool-calls collapsed with no
    /// expand affordance. Normal = today's default (thinking/tool-calls collapsed but user-
    /// expandable). Thinking = like Normal but thinking blocks default to expanded. Verbose = like
    /// Thinking, plus tool-call input/output/diffs also default to expanded.
    /// </summary>
    public enum TranscriptViewMode
    {
        Summary,
        Normal,
        Thinking,
        Verbose
    }

    public sealed class TranscriptModeOption
    {
        public string DisplayName { get; }
        public TranscriptViewMode Value { get; }

        public TranscriptModeOption(string displayName, TranscriptViewMode value)
        {
            DisplayName = displayName;
            Value = value;
        }

        public override string ToString() => DisplayName;
    }

    /// <summary>A pasted screenshot staged in the input box, waiting to be sent with the next message.</summary>
    public sealed class PendingImageAttachment
    {
        /// <summary>Full-resolution PNG data sent to the CLI as an `image`/`base64` content block.</summary>
        public string Base64Png { get; }

        /// <summary>Same bitmap, used for the small chip preview above the input box.</summary>
        public BitmapSource Thumbnail { get; }

        /// <summary>UX-9: file name for a dropped image, or a stand-in label for a clipboard paste.</summary>
        public string Name { get; }

        /// <summary>
        /// UX-9: pixel size of the image as staged, e.g. "1920\u00D71080". Read off the bitmap
        /// rather than stored separately - the "thumbnail" is the full-resolution decode in both
        /// the paste and the drop path, so these are the true dimensions of what will be sent,
        /// not the display size of the chip.
        /// </summary>
        public string DimensionsText => $"{Thumbnail.PixelWidth}\u00D7{Thumbnail.PixelHeight}";

        public PendingImageAttachment(string base64Png, BitmapSource thumbnail, string name)
        {
            Base64Png = base64Png;
            Thumbnail = thumbnail;
            Name = name;
        }
    }

    /// <summary>A dropped file (not an image) staged in the input box, waiting to be sent with the next message.</summary>
    public sealed class PendingFileAttachment
    {
        public string Title { get; }

        /// <summary>True for a PDF (Content is base64 bytes); false for text/code (Content is raw text).</summary>
        public bool IsPdf { get; }

        public string Content { get; }

        /// <summary>UX-9: glyph distinguishing a PDF from a text/code file at a glance.</summary>
        public string Icon => IsPdf ? "\U0001F4D5" : "\U0001F4C4";

        public PendingFileAttachment(string title, bool isPdf, string content)
        {
            Title = title;
            IsPdf = isPdf;
            Content = content;
        }
    }

    /// <summary>
    /// Drives a <see cref="ClaudeCodeSession"/> and projects its NDJSON event stream into
    /// observable view models the chat UI binds to directly.
    /// </summary>
    public sealed class ChatSessionViewModel : ObservableObject, IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private ClaudeCodeSession? _session;
        private string _claudePath = "";
        private string _workingDirectory = "";
        private string? _lastSessionId;

        /// <summary>The text of the most recently sent turn, kept until it completes successfully -
        /// used to offer a verbatim "Try again" after a failed/interrupted turn instead of relying
        /// on the user retyping (or a vague "Continue") to convey what was actually being asked.</summary>
        private string? _lastSentText;

        private ChatMessageViewModel? _currentAssistantMessage;
        private readonly Dictionary<int, ContentBlockViewModel> _blocksByIndex = [];
        private readonly Dictionary<string, ToolCallViewModel> _toolCallsByUseId = [];

        // Tools the user has chosen to allow for the remainder of the current session.
        private readonly HashSet<string> _sessionPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pending plan-approval cards, keyed by the CLI-written plan file path, so a comment
        // submitted from that file's native-tab adornment (see Controls/PlanCommentAdornment.cs)
        // can be routed back to the right chat card via AddPlanComment.
        private readonly Dictionary<string, PlanApprovalViewModel> _planApprovalsByFilePath =
            new Dictionary<string, PlanApprovalViewModel>(StringComparer.OrdinalIgnoreCase);

        // Session history
        private readonly List<SessionHistoryEntry> _allSessions;
        private string? _pendingSessionTitle;

        // Advanced CLI-flag settings, read once from Options at startup (no live chat-UI toggle
        // for these, unlike model/permission-mode/effort) - see SetAdvancedOptions.
        private ClaudeSessionStartOptions _advancedOptions = new ClaudeSessionStartOptions();

        public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
        public ObservableCollection<string> SlashCommands { get; } = [];

        /// <summary>GAP-1: the five "Customize" rows in the palette. Static - never changes.</summary>
        public IReadOnlyList<TerminalHandoffEntry> TerminalHandoffs => TerminalHandoffCatalog.Entries;
        public ObservableCollection<string> RawOutput { get; } = [];
        public ObservableCollection<SessionHistoryEntry> SessionHistory { get; } = [];

        /// <summary>Pasted screenshots staged above the input box, sent with the next message.</summary>
        public ObservableCollection<PendingImageAttachment> PendingImages { get; } = [];

        public bool HasPendingImages => PendingImages.Count > 0;

        public void AddPendingImage(string base64Png, BitmapSource thumbnail, string name = "Pasted image") =>
            PendingImages.Add(new PendingImageAttachment(base64Png, thumbnail, name));

        public void RemovePendingImage(PendingImageAttachment attachment) =>
            PendingImages.Remove(attachment);

        /// <summary>Dropped text/code/PDF files staged above the input box, sent with the next message.</summary>
        public ObservableCollection<PendingFileAttachment> PendingFiles { get; } = [];

        public bool HasPendingFiles => PendingFiles.Count > 0;

        public void AddPendingFile(string title, bool isPdf, string content) =>
            PendingFiles.Add(new PendingFileAttachment(title, isPdf, content));

        public void RemovePendingFile(PendingFileAttachment attachment) =>
            PendingFiles.Remove(attachment);

        /// <summary>
        /// Currently-running tool calls (Task subagents, background Bash shells, or any other
        /// in-flight tool) - feeds both the status line's running-task count and the background-
        /// tasks panel. Maintained via <see cref="OnToolCallStatusChanged"/> rather than re-scanning
        /// <see cref="Messages"/>, since nothing in the CLI's wire protocol itself distinguishes a
        /// "background task" from any other tool call (confirmed via direct research against the
        /// installed VS Code extension, 2026-08-27) - this is purely a client-side projection.
        /// </summary>
        public ObservableCollection<ToolCallViewModel> RunningToolCalls { get; } = [];

        public int RunningTaskCount => RunningToolCalls.Count;
        public bool HasRunningTasks => RunningToolCalls.Count > 0;

        private void OnToolCallStatusChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ToolCallViewModel.Status) || sender is not ToolCallViewModel call)
                return;

            if (call.Status == ToolCallStatus.Running)
            {
                if (!RunningToolCalls.Contains(call))
                    RunningToolCalls.Add(call);
            }
            else
            {
                RunningToolCalls.Remove(call);
            }

            OnPropertyChanged(nameof(RunningTaskCount));
            OnPropertyChanged(nameof(HasRunningTasks));
        }

        /// <summary>Account info and subscription rate-limit usage, loaded on demand.</summary>
        public AccountUsageViewModel AccountUsage { get; } = new AccountUsageViewModel();

        /// <summary>Resolved path to the claude executable; empty until <see cref="Initialize"/> succeeds.</summary>
        public string ClaudePath => _claudePath;

        /// <summary>UX-10: "v0.3.0" for the palette footer, so a bug report can name a version.</summary>
        public string ExtensionVersionText => "v" + ExtensionVersion.Current;

        /// <summary>
        /// UX-3: the permission card currently awaiting an answer, or null. Tracked as a field
        /// rather than searched for on each keystroke, so the input box's number-key handler stays
        /// O(1) however long the transcript grows.
        /// </summary>
        private PermissionRequestViewModel? _pendingPermissionRequest;
        public PermissionRequestViewModel? PendingPermissionRequest
        {
            get => _pendingPermissionRequest;
            private set => SetField(ref _pendingPermissionRequest, value);
        }

        private bool _isSessionHistoryVisible;
        public bool IsSessionHistoryVisible
        {
            get => _isSessionHistoryVisible;
            set => SetField(ref _isSessionHistoryVisible, value);
        }

        /// <summary>
        /// Raised on the UI thread whenever a permission request card is added to the chat.
        /// The chat view should force-scroll to the bottom so the user sees the prompt.
        /// </summary>
        public event EventHandler? PermissionRequestAdded;

        /// <summary>
        /// Raised on the UI thread when a plan is ready for review, carrying the CLI-written plan
        /// file's path. The chat view opens it as a real native VS document tab (see
        /// Core/ClaudeCodeChatControl.xaml.cs) - matching the real extension's behavior of
        /// surfacing the plan as a separate document instead of only inline in the chat.
        /// </summary>
        public event EventHandler<string>? PlanFileReadyToOpen;

        // UX-1: these subtitles are the CLI's own strings, read out of the shipped binary. The
        // "~2\u00D7 usage vs Sonnet" and "Requires usage credits" notes are shown unconditionally
        // because the account's plan is not visible from here, whereas the CLI shows them
        // plan-conditionally. Over-warning about cost is the safe direction to be wrong in.
        public IReadOnlyList<ModelOption> Models { get; } = new[]
        {
            new ModelOption("Default", null,
                "Use the model your CLI is already configured for"),
            new ModelOption("Sonnet", "sonnet",
                "Sonnet 5 \u00B7 Efficient for routine tasks"),
            new ModelOption("Opus", "opus",
                "Opus 5 \u00B7 Best for everyday, complex tasks \u00B7 ~2\u00D7 usage vs Sonnet"),
            new ModelOption("Haiku", "haiku",
                "Haiku 4.5 \u00B7 Fastest for quick answers"),
            new ModelOption("Fable", "fable",
                "Fable 5 \u00B7 Most capable for your hardest and longest-running tasks \u00B7 Requires usage credits"),
        };

        // UX-2: descriptions for all seven modes. The five baseline also exposes use baseline's
        // exact wording; "CLI Default" and "Don't Ask" are ours, written from the CLI's own
        // documented semantics - "dontAsk" means do not prompt and deny anything not already
        // pre-approved - because baseline ships no picker entry for either.
        public IReadOnlyList<PermissionModeOption> PermissionModes { get; } = new[]
        {
            new PermissionModeOption("CLI Default", null,
                "Standard behaviour \u2014 prompts before dangerous operations"),
            new PermissionModeOption("Accept Edits", "acceptEdits",
                "Claude will edit your selected text or the whole file"),
            new PermissionModeOption("Manual", "manual",
                "Claude will ask for approval before making each edit"),
            new PermissionModeOption("Don't Ask", "dontAsk",
                "Never prompts \u2014 denies anything not already pre-approved"),
            new PermissionModeOption("Plan Mode", "plan",
                "Claude will explore the code and present a plan before editing"),
            new PermissionModeOption("Auto (background safety checks)", "auto",
                "Claude will approve actions that pass a safety check and pause for anything risky"),
            new PermissionModeOption("Bypass Permissions", "bypassPermissions",
                "Claude will not ask for approval before running potentially dangerous commands"),
        };

        public IReadOnlyList<ThinkingLevelOption> ThinkingLevels { get; } = new[]
        {
            new ThinkingLevelOption("Auto (CLI default)", null),
            new ThinkingLevelOption("Low", "low"),
            new ThinkingLevelOption("Medium", "medium"),
            new ThinkingLevelOption("High", "high"),
            new ThinkingLevelOption("X-High", "xhigh"),
            new ThinkingLevelOption("Max", "max"),
        };

        public IReadOnlyList<TranscriptModeOption> TranscriptModes { get; } = new[]
        {
            new TranscriptModeOption("Summary", TranscriptViewMode.Summary),
            new TranscriptModeOption("Normal", TranscriptViewMode.Normal),
            new TranscriptModeOption("Thinking", TranscriptViewMode.Thinking),
            new TranscriptModeOption("Verbose", TranscriptViewMode.Verbose),
        };

        private TranscriptModeOption _currentTranscriptMode;
        public TranscriptModeOption CurrentTranscriptMode
        {
            get => _currentTranscriptMode;
            set
            {
                if (SetField(ref _currentTranscriptMode, value))
                    ReapplyTranscriptMode();
            }
        }

        /// <summary>
        /// Re-applies the current mode's default expansion to every block already in the
        /// transcript, so toggling mid-conversation feels consistent rather than only affecting
        /// blocks streamed in afterward.
        /// </summary>
        private void ReapplyTranscriptMode()
        {
            TranscriptViewMode mode = CurrentTranscriptMode.Value;
            bool expandThinking = mode is TranscriptViewMode.Thinking or TranscriptViewMode.Verbose;
            bool expandToolCalls = mode is TranscriptViewMode.Verbose;

            foreach (ChatMessageViewModel message in Messages)
            {
                foreach (ContentBlockViewModel block in message.Blocks)
                {
                    if (block is ThinkingBlockViewModel thinking)
                        thinking.IsExpanded = expandThinking;
                    else if (block is ToolCallViewModel toolCall)
                        toolCall.IsExpanded = expandToolCalls;
                }
            }
        }

        private ModelOption _selectedModel;
        public ModelOption SelectedModel
        {
            get => _selectedModel;
            set
            {
                if (SetField(ref _selectedModel, value))
                    RestartIfIdle();
            }
        }

        private PermissionModeOption _selectedPermissionMode;
        public PermissionModeOption SelectedPermissionMode
        {
            get => _selectedPermissionMode;
            set
            {
                if (SetField(ref _selectedPermissionMode, value))
                    RestartIfIdle();
            }
        }

        /// <summary>
        /// UX-2: advances to the next permission mode, wrapping at the end. Baseline cycles its
        /// three modes with Shift+Tab; we cycle all seven of ours through the same chord. Setting
        /// the property restarts an idle session exactly as picking from the menu does, so the two
        /// entry points cannot drift apart.
        /// </summary>
        public void CycleToNextPermissionMode()
        {
            // IReadOnlyList has no IndexOf, and a seven-item scan is cheaper than materialising a
            // list on every keypress.
            int index = -1;
            for (int i = 0; i < PermissionModes.Count; i++)
            {
                if (ReferenceEquals(PermissionModes[i], SelectedPermissionMode)) { index = i; break; }
            }

            SelectedPermissionMode = PermissionModes[(index + 1) % PermissionModes.Count];
        }

        private ThinkingLevelOption _selectedThinkingLevel;
        public ThinkingLevelOption SelectedThinkingLevel
        {
            get => _selectedThinkingLevel;
            set
            {
                if (SetField(ref _selectedThinkingLevel, value))
                    RestartIfIdle();
            }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetField(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(CanSend));

                    // No stopwatch exists on the CLI's own wire protocol for an in-flight turn -
                    // DurationMs only arrives after the turn completes, on ResultMessage - so this
                    // is a purely client-side ticking clock for the status line's "11m0s" display.
                    if (value)
                    {
                        _busyStartedAtUtc = DateTime.UtcNow;
                        ElapsedText = "0s";
                        _elapsedTimer.Start();
                    }
                    else
                    {
                        _elapsedTimer.Stop();
                        ElapsedText = "";
                    }
                }
            }
        }

        private readonly DispatcherTimer _elapsedTimer;
        private DateTime _busyStartedAtUtc;

        private string _elapsedText = "";
        public string ElapsedText
        {
            get => _elapsedText;
            private set => SetField(ref _elapsedText, value);
        }

        private void UpdateElapsedText()
        {
            TimeSpan elapsed = DateTime.UtcNow - _busyStartedAtUtc;
            ElapsedText = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds}s"
                : $"{elapsed.Seconds}s";
        }

        // Deliberately independent of IsBusy: the CLI queues a `user` line written while a turn
        // is still running and processes it after (confirmed live 2026-08-26) - blocking send
        // here just makes the input box eat the keystroke with no feedback while busy.
        public bool CanSend => ClaudeNotFoundMessage == null;

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            private set => SetField(ref _statusText, value);
        }

        private bool _isRawOutputVisible;
        public bool IsRawOutputVisible
        {
            get => _isRawOutputVisible;
            set => SetField(ref _isRawOutputVisible, value);
        }

        private string? _claudeNotFoundMessage;
        public string? ClaudeNotFoundMessage
        {
            get => _claudeNotFoundMessage;
            private set
            {
                if (SetField(ref _claudeNotFoundMessage, value))
                    OnPropertyChanged(nameof(CanSend));
            }
        }

        public string WorkingDirectory => _workingDirectory;

        private int _sessionTurns;
        private double _sessionCostUsd;
        private long _sessionInputTokens;
        private long _sessionOutputTokens;

        /// <summary>Human-readable summary of cumulative cost/token usage for the current session.</summary>
        public string SessionUsageText
        {
            get
            {
                if (_sessionTurns == 0)
                    return "No usage yet this session.";

                return $"{_sessionTurns} turn{(_sessionTurns == 1 ? "" : "s")} · " +
                       $"${_sessionCostUsd:0.0000} · " +
                       $"{_sessionInputTokens:N0} in / {_sessionOutputTokens:N0} out tokens";
            }
        }

        /// <summary>Compact "6.2k tokens"-style total, for the status line (SessionUsageText is too long there).</summary>
        public string SessionTokensShortText => FormatTokenCount(_sessionInputTokens + _sessionOutputTokens) + " tokens";

        public ChatSessionViewModel()
        {
            _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _elapsedTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal,
                (s, e) =>
                {
                    UpdateElapsedText();
                    foreach (ToolCallViewModel call in RunningToolCalls)
                        call.RefreshElapsedText();
                }, _dispatcher);
            _selectedModel = Models[0];
            // "Accept Edits" is the extension's own startup default (not the CLI's) - selected by
            // value rather than array index so reordering PermissionModes above can't silently
            // change this.
            _selectedPermissionMode = PermissionModes.First(m => m.Value == "acceptEdits");
            _selectedThinkingLevel = ThinkingLevels[0];
            _currentTranscriptMode = TranscriptModes[1]; // Normal

            _allSessions = SessionHistoryStore.Load();
            foreach (var e in _allSessions)
                SessionHistory.Add(e);

            PendingImages.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasPendingImages));
            PendingFiles.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasPendingFiles));

            PlanCommentRegistry.CommentSubmitted += OnPlanCommentSubmitted;
        }

        /// <summary>
        /// Parses the Options page's raw multi-line/token strings into <see cref="_advancedOptions"/>.
        /// Call once at startup, before the first <see cref="StartSession"/>. Directory/file-path
        /// lists split on newline only (a path can contain spaces); tool-name lists split on any
        /// whitespace, matching the CLI's own "comma or space-separated" acceptance.
        /// </summary>
        public void SetAdvancedOptions(string additionalDirectories, string allowedTools, string disallowedTools,
            string appendSystemPrompt, string systemPrompt, string mcpConfigPaths, bool strictMcpConfig)
        {
            _advancedOptions = new ClaudeSessionStartOptions
            {
                AdditionalDirectories = SplitLines(additionalDirectories),
                AllowedTools = SplitTokens(allowedTools),
                DisallowedTools = SplitTokens(disallowedTools),
                AppendSystemPrompt = string.IsNullOrWhiteSpace(appendSystemPrompt) ? null : appendSystemPrompt,
                SystemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
                McpConfigPaths = SplitLines(mcpConfigPaths),
                StrictMcpConfig = strictMcpConfig
            };
        }

        private static IReadOnlyList<string> SplitLines(string text) =>
            string.IsNullOrWhiteSpace(text)
                ? []
                : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToArray();

        private static IReadOnlyList<string> SplitTokens(string text) =>
            string.IsNullOrWhiteSpace(text)
                ? []
                : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Resolves the `claude` executable. Returns false if it couldn't be found.</summary>
        public bool Initialize(string? claudeExecutableOverride, string workingDirectory)
        {
            _workingDirectory = workingDirectory;

            string? path = ClaudeCliLocator.Find(claudeExecutableOverride);
            if (path == null)
            {
                ClaudeNotFoundMessage =
                    "Claude Code CLI was not found. Install it from https://docs.claude.com/en/docs/claude-code, " +
                    "make sure 'claude' is on your PATH (or set a custom path in Tools → Options → Claude Code), " +
                    "then reopen this window.";
                StatusText = "Claude Code CLI not found";
                return false;
            }

            _claudePath = path;
            return true;
        }

        /// <summary>(Re)starts the `claude` process, resuming the previous session if one exists.</summary>
        public void StartSession()
        {
            if (ClaudeNotFoundMessage != null)
                return;

            StopSessionCore();
            ResetTurnState();

            (int Port, string AuthToken)? ideServer;
            if (ClaudeCodePackage.Instance == null)
            {
                ideServer = null;
                RawOutput.Add("[ide-server-diag] ClaudeCodePackage.Instance is NULL - IDE companion server not started");
            }
            else
            {
                var server = ClaudeCodePackage.Instance.GetOrStartIdeServer();
                ideServer = server != null ? (server.Port, server.AuthToken) : ((int, string)?)null;
                RawOutput.Add($"[ide-server-diag] {ClaudeCodePackage.Instance.LastIdeServerDiagnostic}; ideServer={(ideServer.HasValue ? $"port={ideServer.Value.Port}" : "null")}");
            }
            TrimRawOutput();

            _session = new ClaudeCodeSession();
            Hook(_session);
            _session.Start(
                _claudePath, _workingDirectory,
                SelectedModel.Value, SelectedPermissionMode.Value,
                _lastSessionId, SelectedThinkingLevel.EffortArg,
                _advancedOptions, ideServer);

            IsBusy = false;
            StatusText = "Starting Claude Code…";
        }

        /// <summary>Clears the conversation and starts a brand-new session (no `--resume`).</summary>
        public void NewSession()
        {
            _lastSessionId = null;
            _pendingSessionTitle = null;
            Messages.Clear();
            RawOutput.Clear();
            _sessionPermissions.Clear();

            _sessionTurns = 0;
            _sessionCostUsd = 0;
            _sessionInputTokens = 0;
            _sessionOutputTokens = 0;
            OnPropertyChanged(nameof(SessionUsageText));
            OnPropertyChanged(nameof(SessionTokensShortText));

            StartSession();
        }

        /// <summary>
        /// Interrupts the in-flight turn via the CLI's control_request protocol, keeping the
        /// process alive (confirmed live: the same process accepts a normal follow-up turn
        /// afterward with no --resume needed). Falls back to killing the process only if the
        /// session isn't running or the interrupt times out with no response.
        /// </summary>
        public async Task StopSessionAsync()
        {
            // Show an "interrupted" marker in the chat if a response was in flight.
            if (IsBusy && _currentAssistantMessage != null)
                _currentAssistantMessage.Blocks.Add(new InterruptedBlockViewModel());

            if (_session != null && _session.IsRunning)
            {
                var response = await _session.SendInterruptAsync().ConfigureAwait(true);
                if (response != null)
                {
                    ResetTurnState();
                    IsBusy = false;
                    StatusText = "Stopped";
                    return;
                }
                // No control_response within the timeout - the process may be wedged; fall back
                // to the kill path below rather than leaving the UI stuck in a busy state.
            }

            StopSessionCore();
            ResetTurnState();
            IsBusy = false;
            StatusText = "Stopped";
        }

        public async Task SendMessageAsync(string text)
        {
            text = text.Trim();
            bool hasImages = PendingImages.Count > 0;
            bool hasFiles = PendingFiles.Count > 0;
            if ((text.Length == 0 && !hasImages && !hasFiles) || ClaudeNotFoundMessage != null)
                return;

            if (_session == null || !_session.IsRunning)
                StartSession();

            ChatMessageViewModel userMessage = new ChatMessageViewModel(ChatRole.User);
            foreach (PendingImageAttachment image in PendingImages)
                userMessage.Blocks.Add(new ImageAttachmentViewModel(image.Thumbnail));
            foreach (PendingFileAttachment file in PendingFiles)
                userMessage.Blocks.Add(new FileAttachmentViewModel(file.Title));
            if (text.Length > 0)
                userMessage.Blocks.Add(new TextBlockViewModel { Text = text });
            Messages.Add(userMessage);

            // Record the first message as the session title.
            if (_pendingSessionTitle == null)
                _pendingSessionTitle = text.Length <= 60 ? text : text.Substring(0, 57) + "…";

            _lastSentText = text;

            List<string>? imagesBase64Png = hasImages ? PendingImages.Select(p => p.Base64Png).ToList() : null;
            List<PendingFileContent>? files = hasFiles
                ? PendingFiles.Select(f => new PendingFileContent(f.Title, f.IsPdf, f.Content)).ToList()
                : null;
            PendingImages.Clear();
            PendingFiles.Clear();

            // Deliberately no ResetTurnState() here: the CLI queues additional `user` lines
            // written while a turn is still in flight and runs them sequentially on its own
            // (confirmed live 2026-08-26, `queued_turn_count` on the result) - clearing
            // _currentAssistantMessage/_blocksByIndex here would corrupt whichever turn is
            // still actively streaming. The next turn's own state gets set up naturally by
            // OnMessageStarted/EnsureAssistantMessage when its message_start actually arrives.
            IsBusy = true;
            StatusText = "Working…";

            await _session!.SendUserMessageAsync(text, imagesBase64Png, files).ConfigureAwait(false);
        }

        private void StopSessionCore()
        {
            if (_session == null) return;
            _session.Dispose();
            _session = null;
        }

        private void ResetTurnState()
        {
            _currentAssistantMessage = null;
            _blocksByIndex.Clear();
            _toolCallsByUseId.Clear();
        }

        private void RestartIfIdle()
        {
            if (_session != null && _session.IsRunning && !IsBusy && Messages.Count > 0)
                StartSession();
        }

        private void Hook(ClaudeCodeSession session)
        {
            session.SessionInitialized += (s, e) => Post(() => OnSessionInitialized(e));
            session.StatusChanged += (s, e) => Post(() => OnStatusChanged(e));
            session.CompactBoundary += (s, e) => Post(() => OnCompactBoundary(e));
            session.MessageStarted += (s, e) => Post(OnMessageStarted);
            session.BlockStarted += (s, e) => Post(() => OnBlockStarted(e));
            session.TextDelta += (s, e) => Post(() => OnTextDelta(e));
            session.ThinkingDelta += (s, e) => Post(() => OnThinkingDelta(e));
            session.AssistantSnapshot += (s, e) => Post(() => OnAssistantSnapshot(e));
            session.ToolResult += (s, e) => Post(() => OnToolResult(e));
            session.TurnCompleted += (s, e) => Post(() => OnTurnCompleted(e));
            session.PermissionRequested += (s, e) => Post(() => OnPermissionRequested(e));
            session.AskUserQuestionRequested += (s, e) => Post(() => OnAskUserQuestionRequested(e));
            session.RateLimitUpdated += (s, e) => Post(() => AccountUsage.UpdateRateLimit(e));
            session.RawLineReceived += (s, e) => Post(() => OnRawLine(e));
            session.ErrorReceived += (s, e) => Post(() => OnErrorLine(e));
            session.ProcessExited += (s, e) => Post(OnProcessExited);
        }

        // Dispatcher.BeginInvoke is the correct way to marshal from the session's background
        // read-loop threads to the UI thread here; this is plain WPF, not VS UI-thread-affinity
        // code, so the JoinableTaskFactory.SwitchToMainThreadAsync suggestion doesn't apply.
#pragma warning disable VSTHRD001, VSTHRD110
        private void Post(Action action) => _dispatcher.BeginInvoke(action);
#pragma warning restore VSTHRD001, VSTHRD110

        private void OnSessionInitialized(InitMessage init)
        {
            // UX-5: the CLI emits commands in skill/source order, which reads as arbitrary in a
            // ~50-entry list. Sorting here rather than in the view keeps the palette and the "/"
            // autocomplete - which both bind this one collection - in the same order.
            SlashCommands.Clear();
            foreach (var cmd in init.SlashCommands
                         .Concat(ExtensionSlashCommands)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
                SlashCommands.Add(cmd);

            StatusText = "Ready";
        }

        private void OnStatusChanged(StatusMessage status)
        {
            if (status.CompactResult == "failed")
            {
                AddSystemNotice($"Compact failed · {status.CompactError ?? "unknown error"}", isError: true);
                return;
            }

            switch (status.Status)
            {
                case "requesting":
                    StatusText = "Working…";
                    break;
                case "compacting":
                    StatusText = "Compacting…";
                    break;
                case string s when !string.IsNullOrEmpty(s):
                    StatusText = s;
                    break;
            }
        }

        private void OnCompactBoundary(CompactBoundaryEvent e)
        {
            string freed = e.TokensFreed.HasValue ? FormatTokenCount(e.TokensFreed.Value) : "some";
            AddSystemNotice($"Compacted chat · {e.Trigger} · {freed} tokens freed", isError: false);
        }

        /// <summary>
        /// GAP-3. The three commands baseline offers that the CLI does not: measured against the
        /// shipped binary (v2.1.251) on 2026-08-29, its headless `init` event lists 50 slash
        /// commands and none of these is among them, so they are injected by the *extension*, not
        /// passed through. That answers GAP-3's open question - they had to be implemented, and
        /// all three turned out to be backed by real control-request subtypes the CLI itself
        /// handles (`side_question`, `submit_feedback`, `remote_control`) rather than by anything
        /// proprietary to VS Code.
        ///
        /// `rc` is baseline's own alias for `remote-control` and is intercepted too, but is left
        /// out of the palette so the list does not carry the same action twice.
        /// </summary>
        public static IReadOnlyList<string> ExtensionSlashCommands { get; } =
            ["btw", "feedback", "remote-control"];

        /// <summary>Descriptions for the injected commands - baseline's own wording.</summary>
        public static string? DescribeExtensionCommand(string name) => name switch
        {
            "btw" => "Ask a quick side question without interrupting the main conversation",
            "feedback" => "Send feedback to Anthropic or report a bug",
            "remote-control" => "View and control this session from claude.ai/code",
            _ => null,
        };

        /// <summary>True once `/remote-control` has successfully enabled the bridge.</summary>
        private bool _remoteControlEnabled;

        /// <summary>
        /// GAP-1. Shows the terminal hand-off card for one catalog entry. Nothing launches until
        /// the user picks "Continue in Terminal".
        /// </summary>
        public void ShowTerminalHandoff(TerminalHandoffEntry entry)
        {
            var card = new ChoiceCardViewModel(
                entry.DialogTitle,
                entry.DialogDescription,
                "claude " + entry.SlashCommand,
                "1  Continue in Terminal",
                "2  Never mind",
                accepted => Task.FromResult(accepted
                    ? OpenInTerminal(entry.SlashCommand)
                    : "Never mind."));

            AddCard(card);
        }

        /// <summary>GAP-2. Opens an interactive CLI session, optionally pre-typing a command.</summary>
        public string OpenInTerminal(string? initialPrompt)
        {
            string? error = TerminalLauncher.OpenClaude(_claudePath, _workingDirectory, initialPrompt);
            if (error != null)
                return error;

            return initialPrompt == null
                ? "Opened Claude in a terminal."
                : "Opened Claude in a terminal running " + initialPrompt + ".";
        }

        /// <summary>
        /// GAP-3 `/btw`. Asks a side question over the CLI's own `side_question` control request,
        /// so the answer sees this session's context without being added to its transcript.
        /// </summary>
        public async Task AskSideQuestionAsync(string question)
        {
            if (_session == null || !_session.IsRunning)
            {
                AddSystemNotice("/btw needs a running session - send a message first.", isError: true);
                return;
            }

            var block = new SideQuestionViewModel(question);
            AddCard(block);

            ControlResponseEvent? response = await _session.SendSideQuestionAsync(question).ConfigureAwait(true);

            if (response == null)
            {
                block.StatusText = "No answer came back before the request timed out.";
                return;
            }

            if (!response.IsSuccess)
            {
                block.StatusText = response.Error ?? "The side question was rejected.";
                return;
            }

            string answer = response.Response.Value<string>("response") ?? "";
            if (string.IsNullOrWhiteSpace(answer))
            {
                block.StatusText = "The side question returned an empty answer.";
                return;
            }

            block.StatusText = null;
            block.Answer = answer;
        }

        /// <summary>
        /// GAP-3 `/feedback`. Confirms first: the CLI attaches this session's transcript to the
        /// report and uploads it to Anthropic, which leaves the machine and cannot be undone, so
        /// it never fires on the command alone.
        /// </summary>
        public void StartFeedback(string description)
        {
            if (_session == null || !_session.IsRunning)
            {
                AddSystemNotice("/feedback needs a running session - send a message first.", isError: true);
                return;
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                AddSystemNotice(
                    "Describe the problem after the command, e.g. /feedback the diff view scrolls to the top on every edit.",
                    isError: true);
                return;
            }

            string report = description.Trim();

            var card = new ChoiceCardViewModel(
                "Send this feedback to Anthropic?",
                "Your description is uploaded together with this session's transcript. Do not send anything you would not want shared.",
                report,
                "1  Send feedback",
                "2  Never mind",
                async accepted =>
                {
                    if (!accepted)
                        return "Never mind.";

                    ControlResponseEvent? response =
                        await _session.SubmitFeedbackAsync(report).ConfigureAwait(true);

                    if (response == null)
                        return "No response came back before the request timed out - nothing was sent.";
                    if (!response.IsSuccess)
                        return response.Error ?? "The feedback request was rejected.";

                    string? unavailable = response.Response.Value<string>("unavailable_reason");
                    if (!string.IsNullOrEmpty(unavailable))
                        return "Feedback is unavailable: " + unavailable;

                    string? id = response.Response.Value<string>("feedback_id");
                    if (string.IsNullOrEmpty(id))
                    {
                        string? reason = response.Response.Value<string>("failure_reason");
                        return "Feedback could not be sent" + (string.IsNullOrEmpty(reason) ? "." : ": " + reason);
                    }

                    return "Feedback sent - reference " + id;
                });

            AddCard(card);
        }

        /// <summary>
        /// GAP-3 `/remote-control`. Confirms first when turning the bridge ON: it publishes this
        /// session to claude.ai/code, where it can be driven from another device. Turning it back
        /// off only reduces exposure, so that direction is not gated.
        /// </summary>
        public void ToggleRemoteControl()
        {
            if (_session == null || !_session.IsRunning)
            {
                AddSystemNotice("/remote-control needs a running session - send a message first.", isError: true);
                return;
            }

            if (_remoteControlEnabled)
            {
                _ = ApplyRemoteControlAsync(false);
                return;
            }

            var card = new ChoiceCardViewModel(
                "Enable Remote Control for this session?",
                "This session becomes visible and drivable from claude.ai/code on any device signed in to your account. Run /remote-control again to turn it off.",
                null,
                "1  Enable Remote Control",
                "2  Never mind",
                async accepted =>
                {
                    if (!accepted)
                        return "Never mind.";
                    return await ApplyRemoteControlAsync(true).ConfigureAwait(true);
                });

            AddCard(card);
        }

        private async Task<string> ApplyRemoteControlAsync(bool enable)
        {
            ControlResponseEvent? response =
                await _session!.SetRemoteControlAsync(enable).ConfigureAwait(true);

            if (response == null)
            {
                const string timeout = "Remote Control did not respond before the request timed out.";
                if (!enable)
                    AddSystemNotice(timeout, isError: true);
                return timeout;
            }

            if (!response.IsSuccess)
            {
                // Baseline's own wording for a failed bridge.
                string error = "Remote Control error: " + (response.Error ?? "unknown") +
                               " \u00b7 Run /remote-control to dismiss";
                if (!enable)
                    AddSystemNotice(error, isError: true);
                return error;
            }

            _remoteControlEnabled = enable;

            if (!enable)
            {
                const string off = "Remote Control disabled.";
                AddSystemNotice(off, isError: false);
                return off;
            }

            string? url = response.Response.Value<string>("session_url");
            return "Remote Control is active \u00b7 Continue here, on your phone, or at " +
                   (string.IsNullOrEmpty(url) ? "claude.ai/code" : url);
        }

        /// <summary>
        /// Appends a standalone card to the transcript as its own system-role message, so it sits
        /// between turns instead of attaching to whatever the assistant last said.
        /// </summary>
        private void AddCard(ContentBlockViewModel block)
        {
            if (block is ChoiceCardViewModel card)
            {
                PendingChoiceCard = card;
                card.Resolved += (_, _) =>
                {
                    if (ReferenceEquals(PendingChoiceCard, card))
                        PendingChoiceCard = null;
                };
            }

            var message = new ChatMessageViewModel(ChatRole.System);
            message.Blocks.Add(block);
            Messages.Add(message);
        }

        /// <summary>
        /// The unresolved choice card, if one is showing - what the 1/2 keys answer. Tracked as a
        /// field rather than found by walking the transcript, because the key handler consults it
        /// on every keystroke in the input box. Mirrors PendingPermissionRequest.
        /// </summary>
        private ChoiceCardViewModel? _pendingChoiceCard;
        public ChoiceCardViewModel? PendingChoiceCard
        {
            get => _pendingChoiceCard;
            private set => SetField(ref _pendingChoiceCard, value);
        }

        /// <summary>
        /// Appends a one-line system notice to the transcript. Internal rather than private so the
        /// chat control can report a failure the user would otherwise never see (see BUG-1).
        /// </summary>
        internal void AddSystemNotice(string text, bool isError)
        {
            var notice = new ChatMessageViewModel(ChatRole.System);
            notice.Blocks.Add(new ResultFooterViewModel(text, isError));
            Messages.Add(notice);
        }

        private static string FormatTokenCount(long n)
        {
            if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.#") + "m";
            if (n >= 1_000) return (n / 1_000.0).ToString("0.#") + "k";
            return n.ToString();
        }

        private void OnMessageStarted()
        {
            _blocksByIndex.Clear();
            EnsureAssistantMessage();
        }

        private void EnsureAssistantMessage()
        {
            if (_currentAssistantMessage == null)
            {
                _currentAssistantMessage = new ChatMessageViewModel(ChatRole.Assistant);
                Messages.Add(_currentAssistantMessage);
            }
        }

        private void OnBlockStarted(ContentBlockStartEvent e)
        {
            EnsureAssistantMessage();

            TranscriptViewMode mode = CurrentTranscriptMode.Value;

            ContentBlockViewModel block;
            if (e.BlockType == "thinking")
            {
                block = new ThinkingBlockViewModel
                {
                    IsExpanded = mode is TranscriptViewMode.Thinking or TranscriptViewMode.Verbose
                };
            }
            else if (e.BlockType == "tool_use")
            {
                ToolCallViewModel call = new ToolCallViewModel(e.ToolUseId ?? "", e.ToolName ?? "Tool")
                {
                    IsExpanded = mode is TranscriptViewMode.Verbose,
                    OwnerMessage = _currentAssistantMessage
                };
                if (!string.IsNullOrEmpty(e.ToolUseId))
                    _toolCallsByUseId[e.ToolUseId!] = call;

                // Tracks currently-running tool calls for the status line's running-task count and
                // the background-tasks panel (both just render this one collection - no duplicate
                // tracking) - starts Running (the constructor's own default), so add it immediately
                // rather than relying solely on the PropertyChanged hook below to catch a later
                // transition that already happened.
                call.PropertyChanged += OnToolCallStatusChanged;
                RunningToolCalls.Add(call);
                OnPropertyChanged(nameof(RunningTaskCount));
                OnPropertyChanged(nameof(HasRunningTasks));

                block = call;
            }
            else
            {
                block = new TextBlockViewModel();
            }

            _blocksByIndex[e.Index] = block;
            _currentAssistantMessage!.Blocks.Add(block);
        }

        private void OnTextDelta(TextDeltaEvent e)
        {
            if (_blocksByIndex.TryGetValue(e.Index, out var block) && block is TextBlockViewModel text)
                text.Append(e.Delta);
        }

        private void OnThinkingDelta(ThinkingDeltaEvent e)
        {
            if (_blocksByIndex.TryGetValue(e.Index, out var block) && block is ThinkingBlockViewModel thinking)
                thinking.Append(e.Delta);
        }

        private void OnAssistantSnapshot(AssistantSnapshotEvent e)
        {
            foreach (var token in e.Content)
            {
                if (token is not JObject block) continue;
                if (block.Value<string>("type") != "tool_use") continue;

                string id = block.Value<string>("id") ?? "";
                if (id.Length == 0) continue;

                if (_toolCallsByUseId.TryGetValue(id, out var call))
                    call.Input = block["input"] as JObject;
            }
        }

        private void OnToolResult(ToolResultEvent e)
        {
            if (_toolCallsByUseId.TryGetValue(e.ToolUseId, out var call))
            {
                call.Output = e.ResultText;
                call.Status = e.IsError ? ToolCallStatus.Error : ToolCallStatus.Done;
            }
        }

        private void OnPermissionRequested(PermissionRequestEvent e)
        {
            // Diagnostic: always log entry so Raw output confirms this handler fires.
            RawOutput.Add($"[permission] {e.ToolName} requested (id={e.RequestId}, tool_use_id={e.ToolUseId})");
            TrimRawOutput();

            try
            {
                EnsureAssistantMessage();

                _toolCallsByUseId.TryGetValue(e.ToolUseId ?? "", out ToolCallViewModel? call);
                if (call != null)
                    call.Status = ToolCallStatus.AwaitingApproval;

                // The built-in AskUserQuestion tool's can_use_tool request is flagged
                // requires_user_interaction - confirmed live (2026-08-26) it's not a plain
                // allow/deny gate, it needs real answer data or the tool just reports "the user
                // did not answer" and the model falls back to asking in plain text. Skip the
                // Allow/Deny card entirely and go straight to the same interactive question UI
                // used for the dedicated ask_user_question control_request subtype.
                if (e.RequiresUserInteraction && e.ToolName == "AskUserQuestion")
                {
                    OnAskUserQuestionToolRequested(e);
                    return;
                }

                // ExitPlanMode also flags requires_user_interaction (confirmed live, 2026-08-27)
                // and has its own distinct approval semantics (auto-accept / manually approve /
                // keep planning, not Allow/Allow-for-session/Deny) - see docs/Phase 4.
                if (e.ToolName == "ExitPlanMode")
                {
                    OnExitPlanModeRequested(e);
                    return;
                }

                // If the user previously chose "Allow for session" for this tool, auto-allow silently.
                if (_sessionPermissions.Contains(e.ToolName))
                {
                    if (call != null) call.Status = ToolCallStatus.Running;
                    _ = RespondToPermissionAsync(e, allow: true);
                    return;
                }

                string title = e.Title ?? $"Allow {ToolPresentation.GetDisplayName(e.ToolName)}?";
                PermissionRequestViewModel request = new PermissionRequestViewModel(e.ToolName, title, e.Input,
                    (allow, forSession, denyMessage) =>
                    {
                        if (allow && forSession)
                            _sessionPermissions.Add(e.ToolName);

                        // UX-3: a resolved card is no longer the keyboard target.
                        PendingPermissionRequest = null;
                        return RespondToPermissionAsync(e, allow, denyMessage);
                    });

                if (_currentAssistantMessage == null)
                    EnsureAssistantMessage();
                _currentAssistantMessage!.Blocks.Add(request);
                PendingPermissionRequest = request;

                StatusText = "⚠ Approval required — see chat";
                PermissionRequestAdded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // Card creation failed — deny so the CLI doesn't hang indefinitely.
                RawOutput.Add($"[permission-request error] {ex.GetType().Name}: {ex.Message}");
                TrimRawOutput();
                _ = RespondToPermissionAsync(e, allow: false);
            }
        }

        /// <summary>
        /// Handles the built-in AskUserQuestion tool's can_use_tool request (distinct from
        /// OnAskUserQuestionRequested below, which handles the separate ask_user_question
        /// control_request subtype - same question schema, different wire mechanism and reply
        /// shape). Reuses AskUserQuestionViewModel for the UI; the reply must be a can_use_tool
        /// control_response with the answers embedded in updatedInput (confirmed live,
        /// 2026-08-26 - see docs/Phase 3, the answers dict alone or nested elsewhere is silently
        /// ignored and the tool reports "the user did not answer").
        /// </summary>
        private void OnAskUserQuestionToolRequested(PermissionRequestEvent e)
        {
            try
            {
                EnsureAssistantMessage();

                var questions = ClaudeMessage.ParseQuestions(e.Input["questions"] as JArray);
                AskUserQuestionViewModel vm = new AskUserQuestionViewModel(questions,
                    async answers => await RespondToAskUserQuestionToolAsync(e, answers).ConfigureAwait(false));

                _currentAssistantMessage!.Blocks.Add(vm);
                StatusText = "⚠ Claude has a question — see chat";
                PermissionRequestAdded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                RawOutput.Add($"[ask-user-question-tool error] {ex.GetType().Name}: {ex.Message}");
                TrimRawOutput();
                _ = RespondToPermissionAsync(e, allow: false);
            }
        }

        private async Task RespondToAskUserQuestionToolAsync(PermissionRequestEvent e, Dictionary<string, string> answers)
        {
            if (_session == null) return;

            bool allow = answers.Count > 0;

            if (!string.IsNullOrEmpty(e.ToolUseId) && _toolCallsByUseId.TryGetValue(e.ToolUseId!, out var call))
                call.Status = allow ? ToolCallStatus.Running : ToolCallStatus.Error;

            StatusText = "Working…";

            JObject? updatedInput = null;
            if (allow)
            {
                updatedInput = (JObject)e.Input.DeepClone();
                JObject answersObj = new JObject();
                foreach (var kv in answers)
                    answersObj[kv.Key] = kv.Value;
                updatedInput["answers"] = answersObj;
            }

            await _session.RespondToPermissionAsync(e.RequestId, allow, updatedInput).ConfigureAwait(false);
        }

        /// <summary>
        /// Handles the built-in ExitPlanMode tool's can_use_tool request. The real extension does
        /// not gate this behind a generic Allow/Allow-for-session/Deny card: it opens the CLI-
        /// written plan file (input.planFilePath - confirmed live, 2026-08-27, the CLI already
        /// writes the plan to ~/.claude/plans/ before asking) as a native VS tab, and shows a
        /// distinct approval card (see docs/Phase 4).
        /// </summary>
        private void OnExitPlanModeRequested(PermissionRequestEvent e)
        {
            try
            {
                EnsureAssistantMessage();

                string plan = e.Input["plan"]?.ToString() ?? "";
                string? planFilePath = e.Input["planFilePath"]?.ToString();

                PlanApprovalViewModel vm = new PlanApprovalViewModel(plan, planFilePath ?? "",
                    async (allow, autoAccept, denyMessage) =>
                        await RespondToExitPlanModeAsync(e, allow, autoAccept, denyMessage).ConfigureAwait(false),
                    () =>
                    {
                        if (!string.IsNullOrEmpty(planFilePath))
                            PlanFileReadyToOpen?.Invoke(this, planFilePath!);
                    });

                if (!string.IsNullOrEmpty(planFilePath))
                {
                    _planApprovalsByFilePath[planFilePath!] = vm;
                    PlanCommentRegistry.RegisterActivePlan(planFilePath!);
                    PlanFileReadyToOpen?.Invoke(this, planFilePath!);
                }

                _currentAssistantMessage!.Blocks.Add(vm);
                StatusText = "⚠ Plan ready for review — see chat";
                PermissionRequestAdded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                RawOutput.Add($"[exit-plan-mode error] {ex.GetType().Name}: {ex.Message}");
                TrimRawOutput();
                _ = RespondToPermissionAsync(e, allow: false);
            }
        }

        /// <summary>
        /// Routes a comment submitted from the plan-preview tab's selection adornment (see
        /// Controls/PlanCommentAdornment.cs) back into the matching PlanApprovalViewModel card.
        /// </summary>
        public void AddPlanComment(string planFilePath, string quotedExcerpt, string commentText)
        {
            if (_planApprovalsByFilePath.TryGetValue(planFilePath, out PlanApprovalViewModel? vm))
                vm.AddComment(quotedExcerpt, commentText);
        }

        /// <summary>
        /// PlanCommentRegistry.CommentSubmitted fires from the MEF adornment's WPF event handlers,
        /// already on the UI thread - Post() is used anyway for consistency with every other event
        /// source this class hooks (BeginInvoke onto an already-current dispatcher is a harmless
        /// no-op queue, not a bug).
        /// </summary>
        private void OnPlanCommentSubmitted(string planFilePath, string quotedExcerpt, string commentText)
            => Post(() => AddPlanComment(planFilePath, quotedExcerpt, commentText));

        private async Task RespondToExitPlanModeAsync(PermissionRequestEvent e, bool allow, bool autoAccept, string? denyMessage)
        {
            if (_session == null) return;

            if (!string.IsNullOrEmpty(e.ToolUseId) && _toolCallsByUseId.TryGetValue(e.ToolUseId!, out var call))
                call.Status = allow ? ToolCallStatus.Running : ToolCallStatus.Error;

            if (allow && autoAccept)
            {
                // Matches the real CLI's `acceptEdits` mode scope (edit-type tools only).
                // Synthesized entirely client-side, reusing the existing "Allow for session"
                // mechanism - the can_use_tool wire protocol offers no updatedPermissions/
                // suggestions field to relay for mode-switching (confirmed live, 2026-08-27) and
                // there's no way to change --permission-mode without restarting the CLI process
                // mid-turn.
                _sessionPermissions.Add("Edit");
                _sessionPermissions.Add("Write");
                _sessionPermissions.Add("NotebookEdit");
                _sessionPermissions.Add("MultiEdit");
            }

            StatusText = "Working…";
            await _session.RespondToPermissionAsync(e.RequestId, allow, updatedInput: allow ? e.Input : null, denyMessage: denyMessage).ConfigureAwait(false);
        }

        private void OnAskUserQuestionRequested(AskUserQuestionEvent e)
        {
            try
            {
                EnsureAssistantMessage();

                AskUserQuestionViewModel vm = new AskUserQuestionViewModel(e.Questions,
                    async answers =>
                    {
                        StatusText = "Working…";
                        if (_session != null)
                            await _session.RespondToAskUserQuestionAsync(e.RequestId, answers).ConfigureAwait(false);
                    });

                _currentAssistantMessage!.Blocks.Add(vm);
                StatusText = "⚠ Claude has a question — see chat";
                PermissionRequestAdded?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                RawOutput.Add($"[ask-user-question error] {ex.GetType().Name}: {ex.Message}");
                TrimRawOutput();
            }
        }

        private async Task RespondToPermissionAsync(PermissionRequestEvent e, bool allow, string? denyMessage = null)
        {
            if (_session == null) return;

            if (!string.IsNullOrEmpty(e.ToolUseId) && _toolCallsByUseId.TryGetValue(e.ToolUseId!, out var call))
                call.Status = allow ? ToolCallStatus.Running : ToolCallStatus.Error;

            StatusText = "Working…";
            await _session.RespondToPermissionAsync(e.RequestId, allow, allow ? e.Input : null, denyMessage)
                .ConfigureAwait(false);
        }

        private void OnTurnCompleted(ResultMessage result)
        {
            EnsureAssistantMessage();

            // Update session ID for future resumes only after a successful turn.
            // On startup failure (numTurns == 0 + error, e.g. bad resume ID) clear it so the
            // next message starts fresh instead of retrying the same stale ID.
            if (!result.IsError && !string.IsNullOrEmpty(result.SessionId))
                _lastSessionId = result.SessionId;
            else if (result.IsError && result.NumTurns == 0)
                _lastSessionId = null;

            if (result.IsError && _currentAssistantMessage!.Blocks.Count == 0)
            {
                string msg = result.ResultText
                    ?? (result.Errors.Count > 0 ? string.Join("\n", result.Errors) : "An error occurred.");
                _currentAssistantMessage.Blocks.Add(new TextBlockViewModel { Text = msg });
            }

            if (result.IsError)
            {
                AddRetryNotice(
                    new[] { result.ResultText }.Concat(result.Errors),
                    "This turn didn't complete. Your message is still here - you can try it again.");
            }
            else
            {
                _lastSentText = null;
            }

            List<string> parts = new List<string> { result.IsError ? "Error" : "Done", FormatDuration(result.DurationMs) };

            if (result.TotalCostUsd.HasValue)
                parts.Add($"${result.TotalCostUsd.Value:0.0000}");

            if (result.InputTokens.HasValue || result.OutputTokens.HasValue)
                parts.Add($"{result.InputTokens ?? 0:N0} in / {result.OutputTokens ?? 0:N0} out tok");

            _currentAssistantMessage!.Blocks.Add(new ResultFooterViewModel(string.Join(" · ", parts), result.IsError));

            // Persist to session history after each completed turn.
            string? sid = _session?.LastSessionId ?? result.SessionId;
            if (!string.IsNullOrEmpty(sid))
                SaveOrUpdateSession(sid!);

            _sessionTurns++;
            _sessionCostUsd += result.TotalCostUsd ?? 0;
            _sessionInputTokens += result.InputTokens ?? 0;
            _sessionOutputTokens += result.OutputTokens ?? 0;
            OnPropertyChanged(nameof(SessionUsageText));
            OnPropertyChanged(nameof(SessionTokensShortText));

            ResetTurnState();

            // A queued turn (sent while this one was still running) is already in flight on the
            // CLI side and needs no new send from us - just stay busy until the real last one
            // finishes, so the UI doesn't flash "Ready" between queued turns.
            if (result.QueuedTurnCount == 0)
            {
                IsBusy = false;
                StatusText = result.IsError ? "Error" : "Ready";
            }
        }

        private void OnRawLine(string line)
        {
            RawOutput.Add(line);
            TrimRawOutput();
        }

        private void OnErrorLine(string line)
        {
            RawOutput.Add("stderr: " + line);
            TrimRawOutput();
        }

        private void TrimRawOutput()
        {
            const int max = 2000;
            while (RawOutput.Count > max)
                RawOutput.RemoveAt(0);
        }

        private void OnProcessExited()
        {
            if (IsBusy)
            {
                EnsureAssistantMessage();
                AddRetryNotice(
                    RawOutput.Where(l => l.StartsWith("stderr: ", StringComparison.Ordinal)),
                    "Claude Code exited unexpectedly. Your message is still here - you can try it again.");
                ResetTurnState();

                StatusText = "Claude Code exited unexpectedly.";
                IsBusy = false;
            }
        }

        /// <summary>
        /// Offers a verbatim "Try again" for the most recently sent turn after it fails or the
        /// process dies mid-turn. Confirmed live (2026-08-26) that a killed-mid-turn process still
        /// has its abandoned user message in the CLI's own session log on --resume, but the model
        /// only picks it up correctly for content it can reference explicitly - a bare "Continue"
        /// still leaves it guessing. Resending the exact original text sidesteps that entirely,
        /// instead of relying on the CLI's own resume fidelity for every failure mode (a genuine
        /// quota rejection may never even reach the API to be logged in the first place).
        /// </summary>
        private void AddRetryNotice(IEnumerable<string?> rateLimitHintSources, string fallbackText)
        {
            if (_lastSentText == null || _currentAssistantMessage == null) return;

            string retryText = _lastSentText;
            bool looksLikeRateLimit = rateLimitHintSources.Any(ContainsRateLimitHint);
            string notice = looksLikeRateLimit && AccountUsage.HasRateLimitData
                ? $"You've hit your usage limit · resets {AccountUsage.SessionResetLabel}"
                : fallbackText;

            _currentAssistantMessage.Blocks.Add(new RetryNoticeViewModel(notice, () => _ = SendMessageAsync(retryText)));
        }

        private static bool ContainsRateLimitHint(string? text)
        {
            if (text == null || text.Length == 0) return false;
            return text.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("usage limit", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("session limit", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("quota", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("out of credits", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FormatDuration(long ms) => ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0}s";

        // ─── Session history ──────────────────────────────────────────────────────

        private void SaveOrUpdateSession(string sessionId)
        {
            var existing = _allSessions.FirstOrDefault(e => e.SessionId == sessionId);
            if (existing != null)
            {
                existing.LastUsed = DateTime.UtcNow;
                int idx = _allSessions.IndexOf(existing);
                if (idx > 0)
                {
                    _allSessions.RemoveAt(idx);
                    _allSessions.Insert(0, existing);
                    SessionHistory.Move(SessionHistory.IndexOf(existing), 0);
                }
            }
            else
            {
                SessionHistoryEntry entry = new SessionHistoryEntry
                {
                    SessionId = sessionId,
                    Title = _pendingSessionTitle ?? "Untitled",
                    LastUsed = DateTime.UtcNow,
                    WorkingDirectory = _workingDirectory
                };
                _allSessions.Insert(0, entry);
                SessionHistory.Insert(0, entry);
                while (_allSessions.Count > 100)
                {
                    _allSessions.RemoveAt(_allSessions.Count - 1);
                    SessionHistory.RemoveAt(SessionHistory.Count - 1);
                }
            }
            SessionHistoryStore.Save(_allSessions);
        }

        public void ResumeSessionEntry(SessionHistoryEntry entry)
        {
            IsSessionHistoryVisible = false;
            _lastSessionId = entry.SessionId;
            _pendingSessionTitle = entry.Title;
            Messages.Clear();
            RawOutput.Clear();
            _sessionPermissions.Clear();
            _sessionTurns = 0;
            _sessionCostUsd = 0;
            _sessionInputTokens = 0;
            _sessionOutputTokens = 0;
            OnPropertyChanged(nameof(SessionUsageText));
            OnPropertyChanged(nameof(SessionTokensShortText));

            // The live wire never replays history on --resume (confirmed live against the real
            // CLI) - hydrate the visible transcript from the CLI's own on-disk record instead,
            // best-effort. A hydration failure should never block actually resuming the session.
            try
            {
                foreach (var msg in TranscriptReplay.Load(entry.WorkingDirectory, entry.SessionId))
                    Messages.Add(msg);
            }
            catch (Exception ex)
            {
                RawOutput.Add($"[transcript replay error] {ex.GetType().Name}: {ex.Message}");
            }

            StartSession();
        }

        public void DeleteSessionEntry(SessionHistoryEntry entry)
        {
            _allSessions.Remove(entry);
            SessionHistory.Remove(entry);
            SessionHistoryStore.Save(_allSessions);
        }

        public void CommitSessionEntryTitle(SessionHistoryEntry entry, string newTitle)
        {
            entry.Title = string.IsNullOrWhiteSpace(newTitle) ? "Untitled" : newTitle.Trim();
            entry.IsEditing = false;
            SessionHistoryStore.Save(_allSessions);
        }

        public void Dispose()
        {
            _elapsedTimer.Stop();
            PlanCommentRegistry.CommentSubmitted -= OnPlanCommentSubmitted;
            StopSessionCore();
        }
    }
}
