using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>
    /// A VS-native equivalent of the official VS Code extension's hidden local "ide" MCP server:
    /// a loopback-only WebSocket JSON-RPC server the `claude` CLI subprocess connects to for live
    /// diagnostics, editor/selection awareness, and inline diff review. Protocol confirmed live
    /// (2026-08-26) against a real, currently-running instance of the official extension - see
    /// `docs/Phase 3 - IDE Companion Server.md` for the full trace. Transport/JSON-RPC framing
    /// lives here; actual VS SDK calls are delegated to an <see cref="IIdeToolHandlers"/> so this
    /// class can be exercised independent of a running VS host.
    /// </summary>
    public sealed class IdeCompanionServer(IIdeToolHandlers handlers, Func<IReadOnlyList<string>> getWorkspaceFolders) : IDisposable
    {
        private readonly IIdeToolHandlers _handlers = handlers;
        private readonly Func<IReadOnlyList<string>> _getWorkspaceFolders = getWorkspaceFolders;

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoopTask;
        private WebSocket? _activeSocket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _disposed;

        public int Port { get; private set; }
        public bool IsRunning => _listener != null;

        /// <summary>
        /// The per-start auth token required in the <c>X-Claude-Code-Ide-Authorization</c> header.
        /// Exposed so the spawning session can register this server via an explicit
        /// <c>--mcp-config</c> entry (see <see cref="ClaudeCodeSession"/>) - confirmed live
        /// (2026-08-26) to be the mechanism that actually works for a self-spawned `-p`/headless
        /// process. <c>CLAUDE_CODE_SSE_PORT</c> + <c>--ide</c> was the original design based on
        /// reading the official extension's source, but empirical testing against the real CLI
        /// binary showed it never even attempts a connection in `-p` mode - only the lockfile is
        /// still useful for that path (e.g. a user manually running `claude --ide` in a terminal).
        /// </summary>
        public string AuthToken => _authToken;

        private const string AuthHeaderName = "X-Claude-Code-Ide-Authorization";
        private string _authToken = "";
        private string? _lockFilePath;

        /// <summary>Starts the server on an OS-assigned loopback port and writes the lockfile. Idempotent while already running.</summary>
        public void Start()
        {
            if (_listener != null)
                return;

            Port = GetAvailablePort();
            _authToken = Guid.NewGuid().ToString();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();

            WriteLockFile();

            _cts = new CancellationTokenSource();
            _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            if (_listener == null)
                return;

            try { _cts?.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            _listener = null;

            try { _activeSocket?.Abort(); } catch { }
            _activeSocket = null;

            DeleteLockFile();
        }

        private static int GetAvailablePort()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        // ─── Lockfile lifecycle ─────────────────────────────────────────────────

        private static string LockFileDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ide");

        private void WriteLockFile()
        {
            Directory.CreateDirectory(LockFileDirectory);
            CleanUpStaleLockFiles();
            _lockFilePath = Path.Combine(LockFileDirectory, $"{Port}.lock");

            JObject json = new()
            {
                ["pid"] = Process.GetCurrentProcess().Id,
                ["workspaceFolders"] = new JArray(_getWorkspaceFolders()),
                ["ideName"] = "Visual Studio",
                ["transport"] = "ws",
                ["runningInWindows"] = true,
                ["authToken"] = _authToken
            };

            File.WriteAllText(_lockFilePath, json.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
        }

        /// <summary>
        /// Deletes any lockfile in <see cref="LockFileDirectory"/> whose recorded pid no longer
        /// exists. Confirmed live (2026-08-26) that a previous session which didn't shut down
        /// cleanly (e.g. the experimental instance force-closed rather than disposed) can leave a
        /// dead lockfile behind indefinitely, and the CLI's own `--ide` auto-connect only proceeds
        /// when exactly one *valid* lockfile is present - a stale one silently breaks every future
        /// connection attempt, ours or another IDE's, until manually deleted. Only ever removes
        /// files whose pid is confirmed gone; never touches another IDE's genuinely live lockfile.
        /// </summary>
        private static void CleanUpStaleLockFiles()
        {
            string[] files;
            try { files = Directory.GetFiles(LockFileDirectory, "*.lock"); }
            catch { return; }

            foreach (string file in files)
            {
                try
                {
                    JObject json = JObject.Parse(File.ReadAllText(file));
                    int pid = json.Value<int>("pid");
                    Process.GetProcessById(pid); // throws ArgumentException if no such process
                }
                catch (ArgumentException)
                {
                    try { File.Delete(file); } catch { }
                }
                catch { }
            }
        }

        /// <summary>Called when the workspace folder set changes while the server is running (e.g. a different solution is opened).</summary>
        public void RefreshWorkspaceFolders()
        {
            if (_lockFilePath != null)
                WriteLockFile();
        }

        private void DeleteLockFile()
        {
            if (_lockFilePath == null) return;
            try { File.Delete(_lockFilePath); } catch { }
            _lockFilePath = null;
        }

        // ─── Accept loop ────────────────────────────────────────────────────────

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            var listener = _listener;
            if (listener == null) return;

            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Listener stopped/disposed - exit the loop.
                    return;
                }

                _ = HandleConnectionAsync(context, ct);
            }
        }

        private async Task HandleConnectionAsync(HttpListenerContext context, CancellationToken ct)
        {
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            string? providedToken = context.Request.Headers[AuthHeaderName];
            if (!string.Equals(providedToken, _authToken, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                return;
            }

            HttpListenerWebSocketContext wsContext;
            try
            {
                // Root-caused live (2026-08-26): the real CLI's WebSocket client sends
                // "Sec-WebSocket-Protocol: mcp" in its handshake request (confirmed via a raw
                // header dump). Accepting with subProtocol: null completes the HTTP-level
                // handshake but never confirms that subprotocol back - per RFC 6455 the client is
                // then supposed to treat the handshake as invalid, which matched the observed
                // symptom exactly: connects, then the client never proceeds until its own 30s
                // connect timeout fires. Echoing "mcp" back is what a compliant server must do.
                wsContext = await context.AcceptWebSocketAsync(subProtocol: "mcp").ConfigureAwait(false);
            }
            catch
            {
                try { context.Response.Close(); } catch { }
                return;
            }

            var socket = wsContext.WebSocket;

            // Only one client at a time, matching the real server's behavior.
            var previous = Interlocked.Exchange(ref _activeSocket, socket);
            if (previous != null && previous.State == WebSocketState.Open)
            {
                try { await previous.CloseAsync(WebSocketCloseStatus.NormalClosure, "Superseded by new connection", ct).ConfigureAwait(false); }
                catch { }
            }

            await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);

            if (ReferenceEquals(_activeSocket, socket))
                _activeSocket = null;
        }

        private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using MemoryStream messageStream = new();

            try
            {
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    messageStream.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false); } catch { }
                            return;
                        }
                        // MemoryStream.Write is genuinely non-blocking (in-memory only) - the
                        // analyzer can't distinguish it from a real I/O stream's Write.
#pragma warning disable VSTHRD103
                        messageStream.Write(buffer, 0, result.Count);
#pragma warning restore VSTHRD103
                    }
                    while (!result.EndOfMessage);

                    string json = Encoding.UTF8.GetString(messageStream.ToArray());
                    if (json.Length == 0) continue;

                    _ = HandleMessageAsync(json, socket, ct);
                }
            }
            catch
            {
                // Connection dropped - fall through, the accept loop will pick up the next client.
            }
        }

        // ─── JSON-RPC dispatch ──────────────────────────────────────────────────

        private async Task HandleMessageAsync(string json, WebSocket socket, CancellationToken ct)
        {
            JObject root;
            try { root = JObject.Parse(json); }
            catch { return; }

            string? method = root.Value<string>("method");
            JToken? id = root["id"];

            try
            {
                switch (method)
                {
                    case "initialize":
                        await SendResultAsync(socket, id, BuildInitializeResult(), ct).ConfigureAwait(false);
                        break;

                    case "notifications/initialized":
                        // Notification - no response expected.
                        break;

                    case "tools/list":
                        await SendResultAsync(socket, id, new JObject { ["tools"] = ToolSchemas.BuildToolList() }, ct).ConfigureAwait(false);
                        break;

                    case "tools/call":
                        await HandleToolCallAsync(root["params"] as JObject, socket, id, ct).ConfigureAwait(false);
                        break;

                    default:
                        if (id != null)
                            await SendErrorAsync(socket, id, -32601, $"Method not found: {method}", ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                if (id != null)
                    await SendErrorAsync(socket, id, -32603, ex.Message, ct).ConfigureAwait(false);
            }
        }

        private static JObject BuildInitializeResult() => new()
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = true } },
            ["serverInfo"] = new JObject { ["name"] = "Claude Code Visual Studio MCP", ["version"] = "1.0" }
        };

        private async Task HandleToolCallAsync(JObject? callParams, WebSocket socket, JToken? id, CancellationToken ct)
        {
            string name = callParams?.Value<string>("name") ?? "";
            var args = callParams?["arguments"] as JObject ?? [];

            if (name == "openDiff")
            {
                var (status, detail) = await _handlers.OpenDiffAsync(
                    args.Value<string>("old_file_path") ?? "",
                    args.Value<string>("new_file_path") ?? "",
                    args.Value<string>("new_file_contents") ?? "",
                    args.Value<string>("tab_name") ?? "").ConfigureAwait(false);

                JArray content =
                [
                    new JObject { ["type"] = "text", ["text"] = status },
                    new JObject { ["type"] = "text", ["text"] = detail }
                ];
                await SendResultAsync(socket, id, new JObject { ["content"] = content }, ct).ConfigureAwait(false);
                return;
            }

            JToken payload = name switch
            {
                "getWorkspaceFolders" => await _handlers.GetWorkspaceFoldersAsync().ConfigureAwait(false),
                "getOpenEditors" => await _handlers.GetOpenEditorsAsync().ConfigureAwait(false),
                "getCurrentSelection" => await _handlers.GetCurrentSelectionAsync().ConfigureAwait(false),
                "getLatestSelection" => await _handlers.GetLatestSelectionAsync().ConfigureAwait(false),
                "checkDocumentDirty" => await _handlers.CheckDocumentDirtyAsync(args.Value<string>("filePath") ?? "").ConfigureAwait(false),
                "saveDocument" => await _handlers.SaveDocumentAsync(args.Value<string>("filePath") ?? "").ConfigureAwait(false),
                "openFile" => await _handlers.OpenFileAsync(
                    args.Value<string>("filePath") ?? "",
                    args.Value<bool?>("preview") ?? false,
                    args.Value<string>("startText"),
                    args.Value<string>("endText"),
                    args.Value<bool?>("selectToEndOfLine") ?? false,
                    args.Value<bool?>("makeFrontmost") ?? true).ConfigureAwait(false),
                "close_tab" => await _handlers.CloseTabAsync(args.Value<string>("tab_name") ?? "").ConfigureAwait(false),
                "closeAllDiffTabs" => await _handlers.CloseAllDiffTabsAsync().ConfigureAwait(false),
                "getDiagnostics" => await _handlers.GetDiagnosticsAsync(args.Value<string>("uri")).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unknown tool: {name}")
            };

            JArray textContent = [new JObject { ["type"] = "text", ["text"] = payload.ToString(Newtonsoft.Json.Formatting.Indented) }];
            await SendResultAsync(socket, id, new JObject { ["content"] = textContent }, ct).ConfigureAwait(false);
        }

        private Task SendResultAsync(WebSocket socket, JToken? id, JObject result, CancellationToken ct)
        {
            JObject payload = new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            return SendAsync(socket, payload, ct);
        }

        private Task SendErrorAsync(WebSocket socket, JToken? id, int code, string message, CancellationToken ct)
        {
            JObject payload = new()
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JObject { ["code"] = code, ["message"] = message }
            };
            return SendAsync(socket, payload, ct);
        }

        private async Task SendAsync(WebSocket socket, JObject payload, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None));

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch
            {
                // Socket closed mid-send - ignore, the receive loop will notice and clean up.
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _sendLock.Dispose();
        }
    }
}
