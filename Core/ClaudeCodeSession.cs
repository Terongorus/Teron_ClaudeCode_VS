using TeronClaudeCodeVS.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>
    /// The optional, less-frequently-changed CLI flags for <see cref="ClaudeCodeSession.Start"/>,
    /// bundled to keep that method's signature from growing an unwieldy parameter list as CLI
    /// flag parity expands. Model/permission-mode/effort/resume stay as direct parameters since
    /// they're the ones already switchable live from the chat UI.
    /// </summary>
    public sealed class ClaudeSessionStartOptions
    {
        /// <summary>Extra directories the CLI is allowed to read/write, via --add-dir.</summary>
        public IReadOnlyList<string>? AdditionalDirectories { get; set; }

        /// <summary>Tool names to allow, via --allowedTools.</summary>
        public IReadOnlyList<string>? AllowedTools { get; set; }

        /// <summary>Tool names to deny, via --disallowedTools.</summary>
        public IReadOnlyList<string>? DisallowedTools { get; set; }

        /// <summary>Text appended to the default system prompt, via --append-system-prompt.</summary>
        public string? AppendSystemPrompt { get; set; }

        /// <summary>Replaces the entire default system prompt, via --system-prompt.</summary>
        public string? SystemPrompt { get; set; }

        /// <summary>Paths to MCP server config JSON files, via --mcp-config.</summary>
        public IReadOnlyList<string>? McpConfigPaths { get; set; }

        /// <summary>Only use MCP servers from <see cref="McpConfigPaths"/>, via --strict-mcp-config.</summary>
        public bool StrictMcpConfig { get; set; }
    }

    /// <summary>
    /// Hosts a single `claude -p --input-format stream-json --output-format stream-json
    /// --include-partial-messages --verbose` process and exposes its NDJSON protocol as
    /// typed .NET events. All events are raised on a background thread - subscribers must
    /// marshal to the UI thread themselves.
    /// </summary>
    public sealed class ClaudeCodeSession : IDisposable
    {
        private Process? _process;
        private StreamWriter? _stdin;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public event EventHandler<InitMessage>? SessionInitialized;
        public event EventHandler<StatusMessage>? StatusChanged;
        public event EventHandler<MessageStartEvent>? MessageStarted;
        public event EventHandler<ContentBlockStartEvent>? BlockStarted;
        public event EventHandler<TextDeltaEvent>? TextDelta;
        public event EventHandler<ThinkingDeltaEvent>? ThinkingDelta;
        public event EventHandler<ContentBlockStopEvent>? BlockStopped;
        public event EventHandler<AssistantSnapshotEvent>? AssistantSnapshot;
        public event EventHandler<ToolResultEvent>? ToolResult;
        public event EventHandler<ResultMessage>? TurnCompleted;
        public event EventHandler<PermissionRequestEvent>? PermissionRequested;
        public event EventHandler<AskUserQuestionEvent>? AskUserQuestionRequested;
        public event EventHandler<ControlResponseEvent>? ControlResponseReceived;
        public event EventHandler<string>? RawLineReceived;
        public event EventHandler<string>? ErrorReceived;
        public event EventHandler? ProcessExited;

        // Correlates a client-originated control_request (e.g. interrupt) with its eventual
        // control_response, keyed by request_id. Written from SendInterruptAsync (any thread),
        // completed from HandleLine (the stdout read-loop thread) - lock-protected.
        private readonly Dictionary<string, TaskCompletionSource<ControlResponseEvent>> _pendingControlResponses =
            new Dictionary<string, TaskCompletionSource<ControlResponseEvent>>();

        /// <summary>The session id reported by the most recent `init`/`result` message, for `--resume`.</summary>
        public string? LastSessionId { get; private set; }

        public bool IsRunning => _process != null && !_process.HasExited;

        /// <summary>Starts the underlying `claude` process. Output is consumed on background tasks.</summary>
        public void Start(string claudePath, string workingDirectory, string? model, string? permissionMode,
            string? resumeSessionId = null, string? effortArg = null, ClaudeSessionStartOptions? options = null,
            int? ideServerPort = null)
        {
            if (_process != null)
                throw new InvalidOperationException("Session already started.");

            string fileName = claudePath;
            var args = new List<string>();

            string ext = Path.GetExtension(claudePath);
            if (string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                // .cmd/.bat shims (typical for npm global installs) can't be launched
                // directly with UseShellExecute=false; run them through cmd.exe.
                fileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                args.Add("/c");
                args.Add(claudePath);
            }

            args.Add("-p");
            args.Add("--input-format");
            args.Add("stream-json");
            args.Add("--output-format");
            args.Add("stream-json");
            args.Add("--include-partial-messages");
            args.Add("--verbose");

            if (!string.IsNullOrWhiteSpace(permissionMode))
            {
                args.Add("--permission-mode");
                args.Add(permissionMode!);
            }

            if (!string.IsNullOrWhiteSpace(model))
            {
                args.Add("--model");
                args.Add(model!);
            }

            if (!string.IsNullOrWhiteSpace(resumeSessionId))
            {
                args.Add("--resume");
                args.Add(resumeSessionId!);
            }

            if (!string.IsNullOrWhiteSpace(effortArg))
            {
                args.Add("--effort");
                args.Add(effortArg!);
            }

            if (options?.AdditionalDirectories?.Count > 0)
            {
                args.Add("--add-dir");
                args.AddRange(options.AdditionalDirectories);
            }

            if (options?.AllowedTools?.Count > 0)
            {
                args.Add("--allowedTools");
                args.AddRange(options.AllowedTools);
            }

            if (options?.DisallowedTools?.Count > 0)
            {
                args.Add("--disallowedTools");
                args.AddRange(options.DisallowedTools);
            }

            if (!string.IsNullOrWhiteSpace(options?.AppendSystemPrompt))
            {
                args.Add("--append-system-prompt");
                args.Add(options!.AppendSystemPrompt!);
            }

            if (!string.IsNullOrWhiteSpace(options?.SystemPrompt))
            {
                args.Add("--system-prompt");
                args.Add(options!.SystemPrompt!);
            }

            if (options?.McpConfigPaths?.Count > 0)
            {
                args.Add("--mcp-config");
                args.AddRange(options.McpConfigPaths);
            }

            if (options?.StrictMcpConfig == true)
            {
                args.Add("--strict-mcp-config");
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = BuildArguments(args),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            // Discovery mechanism for the IDE companion server (confirmed live against the real
            // official VS Code extension's own source): the CLI reads this env var to find which
            // port to connect to, then reads that port's ~/.claude/ide/<port>.lock for the auth
            // token - no lockfile-scanning ambiguity needed since we spawn the process ourselves.
            if (ideServerPort.HasValue)
                psi.EnvironmentVariables["CLAUDE_CODE_SSE_PORT"] = ideServerPort.Value.ToString();

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += (s, e) => ProcessExited?.Invoke(this, EventArgs.Empty);
            _process.Start();

            // Write NDJSON directly to the underlying stream with UTF-8 *without* a BOM -
            // claude's JSON parser rejects a leading BOM on the first input line.
            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = false };

            _ = Task.Run(() => ReadLoopAsync(_process.StandardOutput, isError: false));
            _ = Task.Run(() => ReadLoopAsync(_process.StandardError, isError: true));
        }

        /// <summary>
        /// Joins arguments into a single command-line string using the same quoting rules as
        /// ProcessStartInfo.ArgumentList (CommandLineToArgvW-compatible). This project targets
        /// net481, where ArgumentList itself is unavailable.
        /// </summary>
        private static string BuildArguments(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                if (sb.Length != 0)
                    sb.Append(' ');

                if (arg.Length != 0 && IndexOfWhitespaceOrQuote(arg) < 0)
                {
                    sb.Append(arg);
                    continue;
                }

                sb.Append('"');
                int idx = 0;
                while (idx < arg.Length)
                {
                    char c = arg[idx++];
                    if (c == '\\')
                    {
                        int backslashes = 1;
                        while (idx < arg.Length && arg[idx] == '\\')
                        {
                            idx++;
                            backslashes++;
                        }

                        if (idx == arg.Length)
                            sb.Append('\\', backslashes * 2);
                        else if (arg[idx] == '"')
                        {
                            sb.Append('\\', backslashes * 2 + 1);
                            sb.Append(arg[idx++]);
                        }
                        else
                            sb.Append('\\', backslashes);
                    }
                    else if (c == '"')
                    {
                        sb.Append('\\').Append(c);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                sb.Append('"');
            }
            return sb.ToString();
        }

        private static int IndexOfWhitespaceOrQuote(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsWhiteSpace(s[i]) || s[i] == '"')
                    return i;
            }
            return -1;
        }

        /// <summary>Sends a plain-text user turn.</summary>
        public Task SendUserMessageAsync(string text)
        {
            var payload = new JObject
            {
                ["type"] = "user",
                ["message"] = new JObject
                {
                    ["role"] = "user",
                    ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } }
                }
            };
            return WriteLineAsync(payload);
        }

        /// <summary>Answers an `ask_user_question` control request with the user's selections.</summary>
        public Task RespondToAskUserQuestionAsync(string requestId, System.Collections.Generic.Dictionary<string, string> answers)
        {
            var answersObj = new JObject();
            foreach (var kv in answers)
                answersObj[kv.Key] = kv.Value;

            var payload = new JObject
            {
                ["type"] = "control_response",
                ["response"] = new JObject
                {
                    ["subtype"] = "success",
                    ["request_id"] = requestId,
                    ["response"] = new JObject { ["answers"] = answersObj }
                }
            };
            return WriteLineAsync(payload);
        }

        /// <summary>Answers a `can_use_tool` control request.</summary>
        public Task RespondToPermissionAsync(string requestId, bool allow, JObject? updatedInput = null, string? denyMessage = null)
        {
            JObject response = allow
                ? new JObject { ["behavior"] = "allow" }
                : new JObject { ["behavior"] = "deny", ["message"] = denyMessage ?? "User declined the request." };

            if (allow && updatedInput != null)
                response["updatedInput"] = updatedInput;

            var payload = new JObject
            {
                ["type"] = "control_response",
                ["response"] = new JObject
                {
                    ["subtype"] = "success",
                    ["request_id"] = requestId,
                    ["response"] = response
                }
            };
            return WriteLineAsync(payload);
        }

        /// <summary>
        /// Sends a client-originated interrupt control_request (confirmed live against the real
        /// CLI in this exact -p/stream-json invocation mode: the process stays alive, aborts the
        /// in-flight turn, and accepts a normal follow-up turn afterward with no --resume needed).
        /// Returns the correlated control_response, or null if none arrives within <paramref name="timeoutMs"/>.
        /// </summary>
        public async Task<ControlResponseEvent?> SendInterruptAsync(bool cancelQueued = false, int timeoutMs = 5000)
        {
            string requestId = Guid.NewGuid().ToString();
            var tcs = new TaskCompletionSource<ControlResponseEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingControlResponses)
            {
                _pendingControlResponses[requestId] = tcs;
            }

            var request = new JObject { ["subtype"] = "interrupt" };
            if (cancelQueued)
                request["cancel_queued"] = true;

            var payload = new JObject
            {
                ["type"] = "control_request",
                ["request_id"] = requestId,
                ["request"] = request
            };

            await WriteLineAsync(payload).ConfigureAwait(false);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);

            lock (_pendingControlResponses)
            {
                _pendingControlResponses.Remove(requestId);
            }

            return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }

        private async Task WriteLineAsync(JObject payload)
        {
            if (_stdin == null)
                return;

            string json = payload.ToString(Newtonsoft.Json.Formatting.None);

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stdin.WriteAsync(json).ConfigureAwait(false);
                await _stdin.WriteAsync("\n").ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke(this, $"[stdin write error] {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync(StreamReader reader, bool isError)
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (isError)
                    {
                        if (line.Length > 0)
                            ErrorReceived?.Invoke(this, line);
                        continue;
                    }

                    HandleLine(line);
                }
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke(this, $"[{(isError ? "stderr" : "stdout")} read error] {ex.Message}");
            }
        }

        private void HandleLine(string line)
        {
            if (line.Length == 0)
                return;

            RawLineReceived?.Invoke(this, line);

            ClaudeMessage? msg;
            try
            {
                msg = ClaudeMessage.Parse(line);
            }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke(this, $"[parse error] {ex.Message}");
                return;
            }

            switch (msg)
            {
                case InitMessage init:
                    LastSessionId = init.SessionId;
                    SessionInitialized?.Invoke(this, init);
                    break;

                case StatusMessage status:
                    StatusChanged?.Invoke(this, status);
                    break;

                case MessageStartEvent start:
                    MessageStarted?.Invoke(this, start);
                    break;

                case MessageStopEvent:
                    // Intentional no-op: same precedent as ContentBlockStopEvent/BlockStopped below
                    // (raised but never subscribed to) - nothing downstream needs a finalization
                    // signal, since text/thinking blocks re-render live off deltas and there's no
                    // IsStreaming/IsComplete concept anywhere in the view models.
                    break;

                case ContentBlockStartEvent blockStart:
                    BlockStarted?.Invoke(this, blockStart);
                    break;

                case TextDeltaEvent text:
                    TextDelta?.Invoke(this, text);
                    break;

                case ThinkingDeltaEvent thinking:
                    ThinkingDelta?.Invoke(this, thinking);
                    break;

                case ContentBlockStopEvent blockStop:
                    BlockStopped?.Invoke(this, blockStop);
                    break;

                case AssistantSnapshotEvent snapshot:
                    AssistantSnapshot?.Invoke(this, snapshot);
                    break;

                case ToolResultEvent toolResult:
                    ToolResult?.Invoke(this, toolResult);
                    break;

                case ResultMessage result:
                    if (!string.IsNullOrEmpty(result.SessionId))
                        LastSessionId = result.SessionId;
                    TurnCompleted?.Invoke(this, result);
                    break;

                case PermissionRequestEvent permission:
                    ErrorReceived?.Invoke(this, $"[permission] control_request parsed: {permission.ToolName} subtype={permission.Subtype}");
                    PermissionRequested?.Invoke(this, permission);
                    break;

                case AskUserQuestionEvent askQuestion:
                    AskUserQuestionRequested?.Invoke(this, askQuestion);
                    break;

                case ControlResponseEvent controlResponse:
                    TaskCompletionSource<ControlResponseEvent>? pending;
                    lock (_pendingControlResponses)
                    {
                        _pendingControlResponses.TryGetValue(controlResponse.RequestId, out pending);
                    }
                    pending?.TrySetResult(controlResponse);
                    ControlResponseReceived?.Invoke(this, controlResponse);
                    break;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _stdin?.Dispose(); } catch { }

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
            }
            catch { }
            finally
            {
                _process?.Dispose();
                _process = null;
            }

            _writeLock.Dispose();
        }
    }
}
