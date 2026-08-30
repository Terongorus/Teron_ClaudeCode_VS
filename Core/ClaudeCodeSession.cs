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

        /// <summary>
        /// FEAT-7. Model, or comma-separated chain of models, to fall back to when the selected one
        /// is overloaded or unavailable - via --fallback-model. Null or blank leaves the flag off.
        /// The CLI's own help is explicit that this flag "only works with --print", which is the
        /// mode this session always runs in.
        /// </summary>
        public string? FallbackModel { get; set; }
    }

    /// <summary>A dropped text/code file (raw text content) or PDF (base64) attached to an outgoing user message.</summary>
    public readonly struct PendingFileContent
    {
        public string Title { get; }

        /// <summary>True for a PDF (Content is base64 bytes); false for text/code (Content is raw text).</summary>
        public bool IsPdf { get; }

        public string Content { get; }

        public PendingFileContent(string title, bool isPdf, string content)
        {
            Title = title;
            IsPdf = isPdf;
            Content = content;
        }
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
        public event EventHandler<CompactBoundaryEvent>? CompactBoundary;
        public event EventHandler<ModelFallbackEvent>? ModelFallback;
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
        public event EventHandler<RateLimitEvent>? RateLimitUpdated;
        public event EventHandler<string>? RawLineReceived;
        public event EventHandler<string>? ErrorReceived;
        public event EventHandler? ProcessExited;

        // Correlates a client-originated control_request (e.g. interrupt) with its eventual
        // control_response, keyed by request_id. Written from SendInterruptAsync (any thread),
        // completed from HandleLine (the stdout read-loop thread) - lock-protected.
        private readonly Dictionary<string, TaskCompletionSource<ControlResponseEvent>> _pendingControlResponses =
            [];

        /// <summary>The session id reported by the most recent `init`/`result` message, for `--resume`.</summary>
        public string? LastSessionId { get; private set; }

        public bool IsRunning => _process != null && !_process.HasExited;

        /// <summary>Starts the underlying `claude` process. Output is consumed on background tasks.</summary>
        public void Start(string claudePath, string workingDirectory, string? model, string? permissionMode,
            string? resumeSessionId = null, string? effortArg = null, ClaudeSessionStartOptions? options = null,
            (int Port, string AuthToken)? ideServer = null)
        {
            if (_process != null)
                throw new InvalidOperationException("Session already started.");

            string fileName = claudePath;
            List<string> args = new List<string>();

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

            // Without this, built-in tool permission requests (Edit/Write/Bash/...) never reach
            // the control_request/can_use_tool flow at all in -p/headless mode - confirmed live
            // (2026-08-26): a synthetic harness reproduced a synchronous
            // {"type":"system","subtype":"permission_denied"} for Edit even with zero IDE
            // integration, --allowedTools, or --mcp-config involved, and confirming the official
            // VS Code extension's own real claude.exe invocation (captured live via
            // Get-CimInstance Win32_Process) always passes this flag. This is what makes the
            // stdin/stdout control_response protocol this extension already implements
            // (RespondToPermissionAsync) the thing the CLI actually calls into.
            args.Add("--permission-prompt-tool");
            args.Add("stdio");

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

            if (!string.IsNullOrWhiteSpace(options?.FallbackModel))
            {
                args.Add("--fallback-model");
                args.Add(options!.FallbackModel!);
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

            // mcp__ide__getDiagnostics needs to be pre-authorized here, not approved live.
            // Root-caused live (2026-08-26): MCP-server-sourced tools don't go through the normal
            // can_use_tool control_request flow at all - an unauthorized call comes back as a
            // synchronous {"type":"system","subtype":"permission_denied"} event with no
            // opportunity for any UI prompt to exist, confirmed by reproducing it against the real
            // CLI and then confirming --allowedTools eliminates it entirely (permission_denials
            // goes from non-empty to []). Only getDiagnostics is exposed as a model-callable tool
            // (the rest of the 11-tool surface is used internally by the CLI's own UI-driven flows
            // like openDiff, which already goes through the existing can_use_tool approval this
            // extension already handles for Edit/Write) - see docs/Phase 3 for the full trace.
            List<string> allowedTools = new List<string>();
            if (ideServer.HasValue)
                allowedTools.Add("mcp__ide__getDiagnostics");
            if (options?.AllowedTools?.Count > 0)
                allowedTools.AddRange(options.AllowedTools);

            if (allowedTools.Count > 0)
            {
                args.Add("--allowedTools");
                args.AddRange(allowedTools);
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

            // Registering the IDE companion server as an explicit --mcp-config entry (bundled into
            // the same flag invocation as any user-configured McpConfigPaths, since the CLI treats
            // --strict-mcp-config as "only servers from --mcp-config" and this needs to survive
            // that). Root-caused live (2026-08-26): --ide + CLAUDE_CODE_SSE_PORT (this method's
            // original design, based on reading the official extension's source) never attempts a
            // connection at all in -p/headless mode - confirmed via the CLI's own debug log
            // showing zero IDE-related activity. An explicit ws-transport --mcp-config entry does
            // work (confirmed end-to-end: mcp_servers:[{"name":"ide","status":"connected"}] in a
            // real init message) once the server also echoes back the "mcp" subprotocol the CLI's
            // WebSocket client requests (see IdeCompanionServer.HandleConnectionAsync).
            List<string> mcpConfigValues = new List<string>();
            if (ideServer.HasValue)
            {
                JObject ideServerConfig = new JObject
                {
                    ["mcpServers"] = new JObject
                    {
                        ["ide"] = new JObject
                        {
                            ["type"] = "ws",
                            ["url"] = $"ws://127.0.0.1:{ideServer.Value.Port}",
                            ["headers"] = new JObject
                            {
                                ["X-Claude-Code-Ide-Authorization"] = ideServer.Value.AuthToken
                            }
                        }
                    }
                };
                mcpConfigValues.Add(ideServerConfig.ToString(Newtonsoft.Json.Formatting.None));
            }
            if (options?.McpConfigPaths?.Count > 0)
                mcpConfigValues.AddRange(options.McpConfigPaths);

            if (mcpConfigValues.Count > 0)
            {
                args.Add("--mcp-config");
                args.AddRange(mcpConfigValues);
            }

            if (options?.StrictMcpConfig == true)
            {
                args.Add("--strict-mcp-config");
            }

            ProcessStartInfo psi = new ProcessStartInfo
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
            StringBuilder sb = new StringBuilder();
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

        /// <summary>
        /// Sends a user turn, optionally with one or more pasted screenshots and/or dropped files
        /// attached first - real Anthropic Messages API content-block shapes confirmed by reading
        /// the official VS Code extension's own webview bundle (2026-08-27), not guessed:
        /// image: {"type":"image","source":{"type":"base64","media_type":"image/png","data":"..."}}
        /// text doc: {"type":"document","source":{"type":"text","media_type":"text/plain","data":"&lt;raw text, not base64&gt;"},"title":"..."}
        /// pdf doc: {"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"..."},"title":"..."}
        /// </summary>
        public Task SendUserMessageAsync(string text, System.Collections.Generic.IReadOnlyList<string>? imagesBase64Png = null,
            System.Collections.Generic.IReadOnlyList<PendingFileContent>? files = null)
        {
            JArray content = new JArray();

            if (imagesBase64Png != null)
            {
                foreach (string base64Png in imagesBase64Png)
                {
                    content.Add(new JObject
                    {
                        ["type"] = "image",
                        ["source"] = new JObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "image/png",
                            ["data"] = base64Png
                        }
                    });
                }
            }

            if (files != null)
            {
                foreach (PendingFileContent file in files)
                {
                    content.Add(new JObject
                    {
                        ["type"] = "document",
                        ["source"] = file.IsPdf
                            ? new JObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = file.Content }
                            : new JObject { ["type"] = "text", ["media_type"] = "text/plain", ["data"] = file.Content },
                        ["title"] = file.Title
                    });
                }
            }

            if (!string.IsNullOrEmpty(text))
                content.Add(new JObject { ["type"] = "text", ["text"] = text });

            JObject payload = new JObject
            {
                ["type"] = "user",
                ["message"] = new JObject
                {
                    ["role"] = "user",
                    ["content"] = content
                }
            };
            return WriteLineAsync(payload);
        }

        /// <summary>Answers an `ask_user_question` control request with the user's selections.</summary>
        public Task RespondToAskUserQuestionAsync(string requestId, System.Collections.Generic.Dictionary<string, string> answers)
        {
            JObject answersObj = new JObject();
            foreach (var kv in answers)
                answersObj[kv.Key] = kv.Value;

            JObject payload = new JObject
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

            JObject payload = new JObject
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
        public Task<ControlResponseEvent?> SendInterruptAsync(bool cancelQueued = false, int timeoutMs = 5000)
        {
            JObject request = new JObject { ["subtype"] = "interrupt" };
            if (cancelQueued)
                request["cancel_queued"] = true;

            return SendControlRequestAsync(request, timeoutMs);
        }

        /// <summary>
        /// Sends an arbitrary client-originated control_request and waits for the correlated
        /// control_response. Returns null if none arrives within <paramref name="timeoutMs"/>.
        ///
        /// This is the same channel `interrupt` and the permission responses already ride on; it
        /// was generalised for GAP-3, whose three commands (`side_question`, `submit_feedback`,
        /// `remote_control`) all turned out to be real control-request subtypes handled by the
        /// CLI itself - verified against the shipped binary (v2.1.251), not inferred from the
        /// official extension's SDK wrapper.
        /// </summary>
        public async Task<ControlResponseEvent?> SendControlRequestAsync(JObject request, int timeoutMs)
        {
            string requestId = Guid.NewGuid().ToString();
            TaskCompletionSource<ControlResponseEvent> tcs = new TaskCompletionSource<ControlResponseEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingControlResponses)
            {
                _pendingControlResponses[requestId] = tcs;
            }

            JObject payload = new JObject
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

        /// <summary>
        /// GAP-3 `/btw`. Asks a one-off question that sees the session's context but is not added
        /// to its transcript. Generous timeout: this is a real model call, not a local toggle.
        /// </summary>
        public Task<ControlResponseEvent?> SendSideQuestionAsync(string question, int timeoutMs = 300000)
        {
            JObject request = new JObject
            {
                ["subtype"] = "side_question",
                ["question"] = question
            };
            return SendControlRequestAsync(request, timeoutMs);
        }

        /// <summary>
        /// GAP-3 `/feedback`. Uploads the description together with the session transcript to
        /// Anthropic. Outward-facing, so callers must confirm before calling this.
        /// </summary>
        public Task<ControlResponseEvent?> SubmitFeedbackAsync(string description, int timeoutMs = 60000)
        {
            JObject request = new JObject
            {
                ["subtype"] = "submit_feedback",
                ["description"] = description,
                // Baseline's SDK path sends no surface and the CLI defaults it to "sdk"; being
                // explicit keeps our reports distinguishable from the VS Code extension's.
                ["surface"] = "sdk"
            };
            return SendControlRequestAsync(request, timeoutMs);
        }

        /// <summary>
        /// GAP-3 `/remote-control`. Enables or disables the bridge that makes this session
        /// visible and drivable from claude.ai/code. Outward-facing; callers must confirm.
        /// </summary>
        public Task<ControlResponseEvent?> SetRemoteControlAsync(bool enabled, int timeoutMs = 60000)
        {
            JObject request = new JObject
            {
                ["subtype"] = "remote_control",
                ["enabled"] = enabled
            };
            return SendControlRequestAsync(request, timeoutMs);
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

                case CompactBoundaryEvent compact:
                    CompactBoundary?.Invoke(this, compact);
                    break;

                case ModelFallbackEvent fallback:
                    ModelFallback?.Invoke(this, fallback);
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

                case RateLimitEvent rateLimit:
                    RateLimitUpdated?.Invoke(this, rateLimit);
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
