using ClaudeCodeVS.Core;
using ClaudeCodeVS.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ClaudeCodeVS.ViewModels
{
    public sealed class ModelOption
    {
        public string DisplayName { get; }

        /// <summary>Value passed to `--model`, or null to let the CLI pick its default.</summary>
        public string? Value { get; }

        public ModelOption(string displayName, string? value)
        {
            DisplayName = displayName;
            Value = value;
        }

        public override string ToString() => DisplayName;
    }

    public sealed class PermissionModeOption
    {
        public string DisplayName { get; }

        /// <summary>Value passed to `--permission-mode`.</summary>
        public string Value { get; }

        public PermissionModeOption(string displayName, string value)
        {
            DisplayName = displayName;
            Value = value;
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

        private ChatMessageViewModel? _currentAssistantMessage;
        private readonly Dictionary<int, ContentBlockViewModel> _blocksByIndex = new Dictionary<int, ContentBlockViewModel>();
        private readonly Dictionary<string, ToolCallViewModel> _toolCallsByUseId = new Dictionary<string, ToolCallViewModel>();

        // Tools the user has chosen to allow for the remainder of the current session.
        private readonly HashSet<string> _sessionPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<ChatMessageViewModel> Messages { get; } = new ObservableCollection<ChatMessageViewModel>();
        public ObservableCollection<string> SlashCommands { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> RawOutput { get; } = new ObservableCollection<string>();

        /// <summary>Account info and subscription rate-limit usage, loaded on demand.</summary>
        public AccountUsageViewModel AccountUsage { get; } = new AccountUsageViewModel();

        /// <summary>Resolved path to the claude executable; empty until <see cref="Initialize"/> succeeds.</summary>
        public string ClaudePath => _claudePath;

        /// <summary>
        /// Raised on the UI thread whenever a permission request card is added to the chat.
        /// The chat view should force-scroll to the bottom so the user sees the prompt.
        /// </summary>
        public event EventHandler? PermissionRequestAdded;

        public IReadOnlyList<ModelOption> Models { get; } = new[]
        {
            new ModelOption("Default", null),
            new ModelOption("Sonnet", "sonnet"),
            new ModelOption("Opus", "opus"),
            new ModelOption("Haiku", "haiku"),
            new ModelOption("Fable", "fable"),
        };

        public IReadOnlyList<PermissionModeOption> PermissionModes { get; } = new[]
        {
            new PermissionModeOption("Default", "default"),
            new PermissionModeOption("Accept Edits", "acceptEdits"),
            new PermissionModeOption("Plan Mode", "plan"),
            new PermissionModeOption("Auto (background safety checks)", "auto"),
            new PermissionModeOption("Bypass Permissions", "bypassPermissions"),
        };

        public IReadOnlyList<ThinkingLevelOption> ThinkingLevels { get; } = new[]
        {
            new ThinkingLevelOption("Auto (CLI default)", null),
            new ThinkingLevelOption("Low", "low"),
            new ThinkingLevelOption("Medium", "medium"),
            new ThinkingLevelOption("High", "high"),
            new ThinkingLevelOption("Max", "max"),
        };

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
                    OnPropertyChanged(nameof(CanSend));
            }
        }

        public bool CanSend => !IsBusy && ClaudeNotFoundMessage == null;

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

        public ChatSessionViewModel()
        {
            _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _selectedModel = Models[0];
            _selectedPermissionMode = PermissionModes[0];
            _selectedThinkingLevel = ThinkingLevels[0];
        }

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

            _session = new ClaudeCodeSession();
            Hook(_session);
            _session.Start(
                _claudePath, _workingDirectory,
                SelectedModel.Value, SelectedPermissionMode.Value,
                _lastSessionId, SelectedThinkingLevel.EffortArg);

            IsBusy = false;
            StatusText = "Starting Claude Code…";
        }

        /// <summary>Clears the conversation and starts a brand-new session (no `--resume`).</summary>
        public void NewSession()
        {
            _lastSessionId = null;
            Messages.Clear();
            RawOutput.Clear();
            _sessionPermissions.Clear();

            _sessionTurns = 0;
            _sessionCostUsd = 0;
            _sessionInputTokens = 0;
            _sessionOutputTokens = 0;
            OnPropertyChanged(nameof(SessionUsageText));

            StartSession();
        }

        /// <summary>Stops the running process. The next sent message resumes the conversation.</summary>
        public void StopSession()
        {
            // Show an "interrupted" marker in the chat if a response was in flight.
            if (IsBusy && _currentAssistantMessage != null)
                _currentAssistantMessage.Blocks.Add(new InterruptedBlockViewModel());

            StopSessionCore();
            ResetTurnState();
            IsBusy = false;
            StatusText = "Stopped";
        }

        public async Task SendMessageAsync(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || ClaudeNotFoundMessage != null)
                return;

            if (_session == null || !_session.IsRunning)
                StartSession();

            var userMessage = new ChatMessageViewModel(ChatRole.User);
            userMessage.Blocks.Add(new TextBlockViewModel { Text = text });
            Messages.Add(userMessage);

            ResetTurnState();
            IsBusy = true;
            StatusText = "Working…";

            await _session!.SendUserMessageAsync(text).ConfigureAwait(false);
        }

        private void StopSessionCore()
        {
            if (_session == null) return;

            _lastSessionId = _session.LastSessionId ?? _lastSessionId;
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
            session.MessageStarted += (s, e) => Post(OnMessageStarted);
            session.BlockStarted += (s, e) => Post(() => OnBlockStarted(e));
            session.TextDelta += (s, e) => Post(() => OnTextDelta(e));
            session.ThinkingDelta += (s, e) => Post(() => OnThinkingDelta(e));
            session.AssistantSnapshot += (s, e) => Post(() => OnAssistantSnapshot(e));
            session.ToolResult += (s, e) => Post(() => OnToolResult(e));
            session.TurnCompleted += (s, e) => Post(() => OnTurnCompleted(e));
            session.PermissionRequested += (s, e) => Post(() => OnPermissionRequested(e));
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
            SlashCommands.Clear();
            foreach (var cmd in init.SlashCommands)
                SlashCommands.Add(cmd);

            StatusText = "Ready";
        }

        private void OnStatusChanged(StatusMessage status)
        {
            if (!string.IsNullOrEmpty(status.Status))
                StatusText = status.Status;
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

            ContentBlockViewModel block;
            if (e.BlockType == "thinking")
            {
                block = new ThinkingBlockViewModel();
            }
            else if (e.BlockType == "tool_use")
            {
                var call = new ToolCallViewModel(e.ToolUseId ?? "", e.ToolName ?? "Tool");
                if (!string.IsNullOrEmpty(e.ToolUseId))
                    _toolCallsByUseId[e.ToolUseId!] = call;
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
            EnsureAssistantMessage();

            _toolCallsByUseId.TryGetValue(e.ToolUseId ?? "", out ToolCallViewModel? call);
            if (call != null)
                call.Status = ToolCallStatus.AwaitingApproval;

            // If the user previously chose "Allow for session" for this tool, auto-allow silently.
            if (_sessionPermissions.Contains(e.ToolName))
            {
                if (call != null) call.Status = ToolCallStatus.Running;
                _ = RespondToPermissionAsync(e, allow: true);
                return;
            }

            string title = e.Title ?? $"Allow {ToolPresentation.GetDisplayName(e.ToolName)}?";
            var request = new PermissionRequestViewModel(e.ToolName, title, e.Input,
                (allow, forSession) =>
                {
                    if (allow && forSession)
                        _sessionPermissions.Add(e.ToolName);
                    return RespondToPermissionAsync(e, allow);
                });
            _currentAssistantMessage!.Blocks.Add(request);

            StatusText = "Waiting for your approval…";
            PermissionRequestAdded?.Invoke(this, EventArgs.Empty);
        }

        private async Task RespondToPermissionAsync(PermissionRequestEvent e, bool allow)
        {
            if (_session == null) return;

            if (!string.IsNullOrEmpty(e.ToolUseId) && _toolCallsByUseId.TryGetValue(e.ToolUseId!, out var call))
                call.Status = allow ? ToolCallStatus.Running : ToolCallStatus.Error;

            StatusText = "Working…";
            await _session.RespondToPermissionAsync(e.RequestId, allow, allow ? e.Input : null).ConfigureAwait(false);
        }

        private void OnTurnCompleted(ResultMessage result)
        {
            EnsureAssistantMessage();

            if (result.IsError && _currentAssistantMessage!.Blocks.Count == 0 && !string.IsNullOrEmpty(result.ResultText))
                _currentAssistantMessage.Blocks.Add(new TextBlockViewModel { Text = result.ResultText! });

            var parts = new List<string> { result.IsError ? "Error" : "Done", FormatDuration(result.DurationMs) };

            if (result.TotalCostUsd.HasValue)
                parts.Add($"${result.TotalCostUsd.Value:0.0000}");

            if (result.InputTokens.HasValue || result.OutputTokens.HasValue)
                parts.Add($"{result.InputTokens ?? 0:N0} in / {result.OutputTokens ?? 0:N0} out tok");

            _currentAssistantMessage!.Blocks.Add(new ResultFooterViewModel(string.Join(" · ", parts), result.IsError));

            _sessionTurns++;
            _sessionCostUsd += result.TotalCostUsd ?? 0;
            _sessionInputTokens += result.InputTokens ?? 0;
            _sessionOutputTokens += result.OutputTokens ?? 0;
            OnPropertyChanged(nameof(SessionUsageText));

            ResetTurnState();
            IsBusy = false;
            StatusText = result.IsError ? "Error" : "Ready";
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
                StatusText = "Claude Code exited unexpectedly.";
                IsBusy = false;
            }
        }

        private static string FormatDuration(long ms) => ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0}s";

        public void Dispose() => StopSessionCore();
    }
}
