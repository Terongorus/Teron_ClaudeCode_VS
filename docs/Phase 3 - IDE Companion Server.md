# Phase 3 - IDE Companion Server

**Date:** 2026-08-26

Third implementation pass of the [Full CLI Parity Roadmap](Phase%200%20-%20Full%20CLI%20Parity%20Roadmap.md),
tackling the single biggest "native" gap identified in Phase 0: a VS-native equivalent of the
official VS Code extension's hidden local `ide` MCP server, which gives the CLI live diagnostics,
editor/selection awareness, and a real inline diff review flow instead of chat-only diffs.

## Protocol research

Before writing any server code, the real protocol was reverse-engineered live against a
currently-running instance of the official `anthropic.claude-code-2.1.246` VS Code extension on
this machine (not guessed, not research-only):

- Found a real lockfile at `~/.claude/ide/49461.lock` and connected to it directly as a WebSocket
  client (`ws://127.0.0.1:49461`, header `X-Claude-Code-Ide-Authorization: <token>`), driving the
  real MCP `initialize` → `notifications/initialized` → `tools/list` → `tools/call` handshake.
- Confirmed the real tool surface is **12 tools, not the 2 the public docs mention** (those 2 are
  just what's exposed *to the model*): `openDiff`, `getDiagnostics`, `close_tab`,
  `closeAllDiffTabs`, `openFile`, `getOpenEditors`, `getWorkspaceFolders`, `getCurrentSelection`,
  `checkDocumentDirty`, `saveDocument`, `getLatestSelection`, `executeCode` (Jupyter-only, skipped
  - no VS equivalent).
- Read the extension's own `extension.js` source directly (JS, but not obfuscated beyond
  minification) to find: the discovery mechanism (`CLAUDE_CODE_SSE_PORT` env var set on the CLI
  child process, not lockfile-scanning), the exact `openDiff` accept/reject behavior (native VS
  Code diff view, races an explicit-accept/tab-close/manual-save outcome, returns
  `["FILE_SAVED", <content>]` or `["DIFF_REJECTED", tabName]`), and confirmed only one WebSocket
  client is allowed at a time.
- Full trace recorded in the approved plan
  (`C:\Users\kkole\.claude\plans\precious-zooming-spring.md`, "Phase 2 - IDE Companion Server"
  section) - not reproduced in full here.

## Design and implementation

Split into a VS-SDK-free transport layer (independently testable) and a VS SDK-backed handlers
implementation:

- **`Core/IIdeToolHandlers.cs`** - interface for the 11 VS-relevant tools, so the transport can be
  exercised with a fake implementation.
- **`Core/ToolSchemas.cs`** - the `tools/list` response, schemas matching what was confirmed live.
- **`Core/IdeCompanionServer.cs`** - `HttpListener`+`AcceptWebSocketAsync` (plain net481 BCL, no
  new package), lockfile write/delete (exact schema confirmed live, `ideName: "Visual Studio"`
  rather than spoofing "Visual Studio Code"), JSON-RPC dispatch, one-client-at-a-time behavior.
- **`Core/VsIdeToolHandlers.cs`** - the real implementation:
  - Workspace root / open editors / active selection / dirty-state / save reuse the exact VS SDK
    patterns already established in `ClaudeCodeChatControl.xaml.cs` (`VS.Solutions`, `VS.Documents`,
    `VS.Windows`).
  - **Diagnostics** via `EnvDTE`/`EnvDTE80`'s `DTE.ToolWindows.ErrorList.ErrorItems` (already
    transitively available, no new package) - the toolkit has no Error List helper at all
    (confirmed by reflecting its full public type list), and the modern Table Data Source API's
    NuGet package couldn't be found anywhere in this machine's local cache.
  - **Diff view** via raw `IVsDifferenceService.OpenComparisonWindow2` (assembly already
    transitively resolved) writing old/new content to temp files. Confirmed via research this API
    is read-only browsing UI with no built-in Accept/Reject - built that ourselves with an
    `InfoBar` (Accept/Reject hyperlinks), reusing the exact pattern already shipped in
    `ExtensionUpdateCheck.cs`. On Accept, writes the real file to disk and returns `FILE_SAVED`; on
    Reject, `DIFF_REJECTED`. The existing in-chat `Controls/DiffViewer.xaml.cs` is not reusable
    here (pure WPF-visual-tree string parser, no shared data model) - left untouched.
- **`Core/ClaudeCodePackage.cs`** - owns one shared `IdeCompanionServer` per VS instance
  (`GetOrStartIdeServer()`), lazily started/stopped based on the new Options-page toggle.
- **`Core/ClaudeCodeSession.cs`** - `Start` gained an `ideServerPort` parameter; when set, adds
  `CLAUDE_CODE_SSE_PORT` to the subprocess's environment - the exact discovery mechanism confirmed
  in the real extension's source.
- **`Core/ClaudeCodeOptionsPage.cs`** - new `Enable IDE Companion Server` setting, **default
  `true`** (user-confirmed) - loopback-only with per-session random port + token auth, same
  security posture as the official extension.

## API discovery during implementation (not knowable from static research alone)

Several exact VS SDK signatures were unknown going in and had to be resolved via reflection on the
already-referenced NuGet packages (not guessed, not left as TODOs):
- `Community.VisualStudio.Toolkit.FrameCloseOption` enum: `NoSave` / `SaveIfDirty` / `PromptSave`.
- `Community.VisualStudio.Toolkit.InfoBar` has **no `Closed` event** - only `ActionItemClicked`,
  `Close()`, `IsVisible`. The diff InfoBar therefore only resolves via explicit Accept/Reject
  clicks, not by detecting the user dismissing it via the X button - documented simplification.
- `Microsoft.VisualStudio.Shell.InfoBarModel`'s real constructor overloads, matching
  `ExtensionUpdateCheck.cs`'s existing usage exactly (mixed `IVsInfoBarTextSpan[]` array).
- `WindowFrame.CloseFrameAsync` requires a `FrameCloseOption` argument (not parameterless).
- `ITextSelection` lives in `Microsoft.VisualStudio.Text.Editor`, not `Microsoft.VisualStudio.Text`.

## Verification

`dotnet build TeronClaudeCodeVS.csproj` - 0 warnings, 0 errors.

**Transport layer - fully live-verified**, independent of a running VS host (by design, per the
`IIdeToolHandlers` split): loaded the actual built `TeronClaudeCodeVS.dll` via PowerShell, compiled
a fake `IIdeToolHandlers` implementation, started a real `IdeCompanionServer`, and drove it with a
real WebSocket client (same technique used to probe the official extension). Confirmed: lockfile
written with the exact real schema on start and deleted on stop; full MCP handshake
(`initialize`/`notifications/initialized`/`tools/list`) works; all 11 tool schemas match exactly;
`tools/call` for `getWorkspaceFolders` (object payload), `getDiagnostics` (array payload - the
interface deliberately returns `JArray` here, not `JObject`, to match the real server's top-level-
array shape), and `openDiff` (two-element `["FILE_SAVED", content]` content array, exact real
shape) all round-trip correctly; an unknown tool name returns a proper JSON-RPC error.

**VS-backed handlers (`VsIdeToolHandlers`) - NOT yet live-verified.** This requires a running VS
experimental instance (F5), which isn't something achievable from this session alone. Specific
open items for that pass, per the plan's own verification checklist:
1. Confirm the lockfile appears/disappears correctly when a real chat session starts/stops.
2. Confirm the real `claude.exe` subprocess actually connects and calls tools (e.g. ask it "what's
   my workspace root" and see if it uses `getWorkspaceFolders` rather than guessing).
3. Confirm `EnvDTE.ToolWindows.ErrorList` actually returns real diagnostics - it reflects poorly
   outside VS's own hosting (COM PIA quirk hit during research), so this needs a real compile
   error in a test file and a real F5 run to trust, not just "it compiled."
4. Confirm `openDiff` end-to-end: real diff window opens, Accept writes the file for real, Reject
   leaves it untouched.
