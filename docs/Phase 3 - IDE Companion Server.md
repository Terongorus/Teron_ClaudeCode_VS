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

**VS-backed handlers (`VsIdeToolHandlers`) - first real F5 pass done by the user (2026-08-26),
found 2 real bugs, both fixed:**

1. **The CLI never actually attempted an IDE connection at all** (`mcp_servers: []` in the real
   `init` message, confirmed from the raw output panel of a genuine F5 session). Root cause: the
   original design (this doc's own "How the real protocol works" section above) assumed setting
   `CLAUDE_CODE_SSE_PORT` alone was sufficient, based on reading the official extension's
   `extension.js`. Checking the real binary's own `claude --help` directly revealed the actual
   gate: **`--ide` - "Automatically connect to IDE on startup if exactly one valid IDE is
   available"** - an explicit opt-in flag. The env var only supplies *which* port to use once the
   CLI decides to connect; without `--ide` it's never read. Fixed in `ClaudeCodeSession.Start`
   (`Core/ClaudeCodeSession.cs`): now adds `--ide` to the argument list whenever `ideServerPort`
   is set, alongside the existing `CLAUDE_CODE_SSE_PORT` env var. (A real ordering bug was caught
   in the same pass while fixing this - the first attempt appended `--ide` to the `args` list
   *after* `ProcessStartInfo.Arguments` had already been built from it, making the addition dead
   code; corrected by moving the `--ide` append above the `psi` construction.)
2. **Wrong working directory** - the real `init` message's `cwd` was
   `C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE` (devenv.exe's own install
   folder) instead of the open solution's folder, even though a real `.sln` with 3 projects was
   genuinely loaded in Solution Explorer at the time. Root cause: `GetWorkingDirectoryAsync()`
   (duplicated in both `Core/VsIdeToolHandlers.cs` and `Core/ClaudeCodeChatControl.xaml.cs`) only
   ever tried `VS.Solutions.GetCurrentSolutionAsync()`, silently swallowed any exception, and fell
   back straight to `Environment.CurrentDirectory` - which for an in-process VS extension is
   devenv.exe's own cwd, not any project folder. `VS.Solutions.GetCurrentSolutionAsync()` coming
   back empty in this real hosting timing was not something either research pass predicted. Fixed
   by adding a second fallback via `EnvDTE`'s `DTE.Solution.FullName` (the same API this class
   already relies on for `GetDiagnosticsAsync`, proven reliable) before giving up to CWD, and by
   de-duplicating the two copies down to the one in `VsIdeToolHandlers.cs` so both call sites
   (`ClaudeCodeChatControl.OnLoaded` and `ClaudeCodePackage.GetWorkspaceFoldersSync`) share the fix.

`dotnet build` clean after both fixes (0 warnings/errors).

**Third live F5 pass (2026-08-26), with real diagnostic instrumentation added instead of guessing
again** (`[cwd-diag]`/`[ide-server-diag]` lines now written directly to the chat's Raw CLI Output
panel from `VsIdeToolHandlers.GetWorkingDirectoryAsync`, `ChatSessionViewModel.StartSession`, and
`ClaudeCodePackage.GetOrStartIdeServer`) - this pass definitively separated two previously-conflated
symptoms and confirmed the `--ide` fix from the prior pass genuinely works:

- **The companion server now starts successfully** (`[ide-server-diag] running, port=60611`) -
  confirms the `--ide`/env var fix from the previous pass is real, not another dead end.
- **`mcp_servers` was still empty anyway - root cause found, not code, environment.** Checked
  `~/.claude/ide/` directly: 3 lockfiles existed simultaneously - a genuine live VS Code window
  with its own real Claude Code IDE connection (`49461.lock`), this test's own server
  (`60611.lock`), and a **stale lockfile from an earlier session that never cleaned up**
  (`61448.lock` - pid confirmed dead via `tasklist`). `--ide`'s own documented behavior is
  "connect if exactly one valid IDE is available" - with 2+ genuinely live IDEs plus a corpse, the
  ambiguity check correctly refuses to guess. Fixed the robustness half of this: `IdeCompanionServer.WriteLockFile`
  now calls a new `CleanUpStaleLockFiles()` that deletes any lockfile whose pid no longer exists
  before writing its own (never touches another IDE's genuinely live lockfile). The "another real
  IDE is legitimately open at the same time" half isn't a bug to fix - close VS Code (or whatever
  else is holding a live lockfile) for a clean single-IDE test.
- **cwd race root-caused precisely, not just theorized**: the diagnostic lines showed
  `VS.Solutions.GetCurrentSolutionAsync()` returning a literal `null` solution *and* `EnvDTE`'s
  `dte.Solution.FullName` returning an empty string at the exact same moment - both APIs agreeing
  "no solution attached yet," confirming this is a startup timing race (`OnLoaded` firing before
  VS finishes attaching the solution), not an API reliability gap the EnvDTE fallback could ever
  have fixed alone. Fixed by retrying both attempts (`TryGetSolutionDirectoryAsync`) up to 10 times,
  300ms apart (~3s total), before falling back to CWD.

**Not yet re-verified live** - needs a fourth F5 pass, this time with only one IDE's lockfile live,
to confirm `mcp_servers` is finally non-empty and `cwd` resolves correctly. Remaining open items
from the plan's verification checklist, still unconfirmed:

1. ~~Confirm the lockfile appears/disappears correctly when a real chat session starts/stops.~~
   Not directly observed yet, but plausible given the transport-layer tests already cover lockfile
   read/write in isolation - worth a direct check next pass regardless.
2. Re-confirm the real `claude.exe` subprocess actually connects and calls tools now that `--ide`
   is passed (e.g. ask it "what's my workspace root" and see if it uses `getWorkspaceFolders`
   rather than guessing) - this is the exact test that surfaced bug 1 above, re-run it.
3. Confirm `EnvDTE.ToolWindows.ErrorList` actually returns real diagnostics - the same F5 pass had
   a real solution with 7 compiler errors showing correctly in VS's own Error List (visible in the
   screenshot), but this wasn't yet confirmed to flow through `getDiagnostics` specifically, since
   the CLI never got as far as calling any tool.
4. Confirm `openDiff` end-to-end: real diff window opens, Accept writes the file for real, Reject
   leaves it untouched.

## Discovery mechanism was wrong - real mechanism found and fixed (2026-08-26)

The "close VS Code, it's just an ambiguous-IDE-count problem" diagnosis above turned out to be
premature - the user asked a sharp question (real VS Code supports multiple simultaneous windows
with independent chat sessions and zero conflicts, so whatever mechanism it uses for its own
self-spawned child CLI can't depend on "exactly one lockfile visible" at all) that didn't fit that
theory, and it was right not to.

**Root cause, confirmed empirically against the real `claude.exe` binary, not guessed:** `--ide` +
`CLAUDE_CODE_SSE_PORT` (this doc's entire original design, based on reading the official VS Code
extension's `extension.js`) **never attempts an IDE connection at all in `-p`/headless mode** -
confirmed via `claude --debug`'s own per-session debug log (`~/.claude/debug/<session-id>.txt`)
showing zero mention of IDE/SSE_PORT/lockfile activity anywhere in the startup sequence, across
multiple isolated test runs (including with all ambient `CLAUDE_CODE_*`/`CLAUDECODE` env vars
stripped, to rule out this session's own shell contaminating the child process). The ambiguity
check in `--ide`'s own help text never even gets the chance to matter, because the connection is
never attempted in the first place - the earlier fix (adding `--ide` to the args) was real code
that compiled and ran, but was operating on a mechanism that doesn't function in this mode.

**The real mechanism**: an explicit `--mcp-config` entry registering the server directly, e.g.

```json
{"mcpServers":{"ide":{"type":"ws","url":"ws://127.0.0.1:<port>","headers":{"X-Claude-Code-Ide-Authorization":"<token>"}}}}
```

Confirmed via the debug log that this genuinely triggers `MCP server "ide": Initializing WebSocket
transport to ws://127.0.0.1:<port>` and a real connection attempt. This is presumably the actual
mechanism the official extension's self-spawned child process uses too (its `extension.js` source
reading was correct about the lockfile schema and lockfile-scanning discovery mode for *externally*
launched CLIs, but likely incomplete about how the extension's *own* spawned child connects).

**Second bug found in the same investigation**: even with the right `--mcp-config` entry, the
connection attempt hung for the CLI's own 30-second timeout and then failed
(`CONNECT_TIMEOUT`). A connection-level trace (temporarily added to `IdeCompanionServer`, since
removed) showed the HTTP-level WebSocket handshake actually completing
(`AcceptWebSocketAsync succeeded`) - the request never even reached `HandleToolCallAsync`. The
real CLI's WebSocket client sends `Sec-WebSocket-Protocol: mcp` in its handshake request (confirmed
via a raw header dump); `IdeCompanionServer` was calling `context.AcceptWebSocketAsync(subProtocol:
null)`, never confirming that subprotocol back. Per RFC 6455, a client that requests a subprotocol
and gets a response that doesn't confirm one is supposed to treat the handshake as invalid - which
matches the observed symptom exactly (connects, then never proceeds). Fixed by accepting with
`subProtocol: "mcp"` instead.

**Fully confirmed end-to-end** against the real binary (not the VS extension yet, but the exact
same `ClaudeCodeSession.Start`/`IdeCompanionServer` code paths, driven directly): a real `init`
message came back with `"mcp_servers":[{"name":"ide","status":"connected"}]` and
`"tools":[...,"mcp__ide__getDiagnostics"]`. Also confirmed only `getDiagnostics` (and presumably
`executeCode`, Jupyter-only) get injected as top-level `mcp__ide__*` tools the model can call
directly - the other 10 tools in the surface are used internally by the CLI's own UI-driven flows
(e.g. `openDiff` when the model proposes an edit), not exposed as directly-callable tools, matching
the "2 tools exposed to the model" the public docs mention (this doc's earlier "12 tools, not 2"
framing was about the total RPC surface, which is still accurate - only the exposure point was
misunderstood).

**Code changes**: `Core/ClaudeCodeSession.cs` - `Start`'s last parameter changed from `int?
ideServerPort` to `(int Port, string AuthToken)? ideServer`; builds the inline `--mcp-config` JSON
above (bundled into the same `--mcp-config` invocation as any user-configured `McpConfigPaths`, so
`--strict-mcp-config` doesn't exclude it); the non-functional `--ide` arg and
`CLAUDE_CODE_SSE_PORT` env var are removed entirely. `Core/IdeCompanionServer.cs` - exposes
`AuthToken`; `AcceptWebSocketAsync(subProtocol: "mcp")`. `ViewModels/ChatSessionViewModel.cs` -
`StartSession()` builds the tuple from `GetOrStartIdeServer()`'s `Port`/`AuthToken` instead of just
reading `Port`. The lockfile-writing infrastructure (`WriteLockFile`/`CleanUpStaleLockFiles`/
`DeleteLockFile`) is left in place - it's no longer load-bearing for our own self-spawned session,
but still has a legitimate secondary purpose if a user manually runs `claude --ide` in a terminal
opened from within VS.

`dotnet build` clean (0 warnings/errors) after these changes.

**Real F5 pass (2026-08-26) confirmed the connection fix live**: the actual VS experimental
instance's `init` message came back with `"mcp_servers":[{"name":"ide","status":"connected"}]` and
`mcp__ide__getDiagnostics` in the tool list - the mcp-config + subprotocol fixes above are real,
not just synthetic-test-verified.

## Third bug found in the same pass: mcp__ide__getDiagnostics needs static pre-authorization

The user asked it to use `getDiagnostics` explicitly; the model correctly attempted the tool call,
but it came back denied: *"Claude requested permissions to use mcp__ide__getDiagnostics, but you
haven't granted it yet."* No permission card ever appeared in the chat UI for the user to approve.

**Root-caused live, not guessed**: reproduced the exact denial against the real CLI via the same
`--mcp-config` harness, and the raw wire output showed the actual message type -
`{"type":"system","subtype":"permission_denied",...}` - a *synchronous system event*, not a
`control_request`/`can_use_tool`. This extension's entire permission-card UI
(`ChatSessionViewModel.OnPermissionRequested`, `PermissionRequestViewModel`) only ever handles
`can_use_tool` control requests; there was never a bug in that code because there was never a
request routed to it to handle - MCP-server-sourced tools apparently don't go through the
interactive approval flow at all, only a static allowlist check made before the session even
starts. Confirmed the fix directly: adding `--allowedTools mcp__ide__getDiagnostics` to the same
test harness made `permission_denials` go from non-empty to `[]` and the tool call succeeded,
returning real data.

**Fix**: `Core/ClaudeCodeSession.cs` - whenever `ideServer.HasValue`, `mcp__ide__getDiagnostics` is
now unconditionally added to `--allowedTools` (merged with any user-configured `AllowedTools`),
alongside the `--mcp-config` entry. Only `getDiagnostics` needs this - the other 10 tools in the
server's surface are invoked internally by the CLI's own UI-driven flows (e.g. `openDiff`), which
already route through the normal `can_use_tool` flow this extension already handles correctly for
Edit/Write.

`dotnet build` clean (0 warnings/errors) after this fix too.

**Verification status**: the mcp-config connection mechanism and subprotocol fix are confirmed live
inside the real VS experimental instance. Checklist items 1-3 (connection, cwd, real `EnvDTE`
diagnostics via `getDiagnostics`, not fake data) are now confirmed live in the real VS experimental
instance too - a real F5 pass returned 7 genuine `CS____` compiler errors for a real broken test
file. Item 4 (`openDiff` end-to-end) is next.

## Fourth bug found: built-in tool permissions never reached the CLI's interactive approval flow

Applies to Edit/Write/etc. - unrelated to the IDE server, present since day one.

Testing item 4 (`openDiff`) surfaced a much bigger problem: asking Claude to edit a file (to show
the fix as a diff for accept/reject) produced no diff window, no permission card - the model just
claimed it had "proposed an edit" that the user should "accept or reject in the editor," when
nothing had actually appeared. This happened twice, with two different prompts, both times.

**Root-caused live, not guessed** (per this project's own hard-learned rule after an earlier
guessing spree on this exact IDE Companion Server work went badly): reproduced directly against the
real `claude.exe` binary via a synchronous-I/O synthetic harness (PowerShell's async event
registration - `Register-ObjectEvent`/`BeginOutputReadLine` - proved unreliable in this environment,
silently producing zero captured output across several attempts for reasons unrelated to the CLI
itself; switching to a plain blocking `ReadLine()`/`WriteLine()` loop fixed the harness and gave
clean, immediate results every time).

The `Edit` tool call came back denied via the exact same shape as the earlier `getDiagnostics` bug:
a **synchronous** `{"type":"system","subtype":"permission_denied","tool_name":"Edit",...}` event -
never a `control_request`/`can_use_tool`. Three isolating tests proved this had nothing to do with
the IDE Companion Server at all:

1. `--permission-mode manual` + IDE `--mcp-config` + `--allowedTools mcp__ide__getDiagnostics`
   (today's actual production args) - synchronous denial.
2. Same, minus `--allowedTools` entirely - still a synchronous denial (rules out the
   `--allowedTools` mechanism as the cause).
3. Same, minus the IDE `--mcp-config` entirely too (`mcp_servers:[]`, zero IDE integration
   whatsoever) - **still** a synchronous denial.

Test 3 proved this is a pre-existing, general bug in how this extension invokes the CLI, present
since before any IDE Companion Server work started - not an IDE-integration regression.

**The actual cause**: comparing our own invocation's argument list against the real, currently-
running official VS Code extension's own `claude.exe` process (captured live via
`Get-CimInstance Win32_Process -Filter "Name='claude.exe'"`, which happened to include this very
session's own real CLI process) showed one flag our extension has never passed:
`--permission-prompt-tool stdio`. Adding it and rerunning the same synthetic harness (now with the
CLI's `control_request`s answered inline) confirmed it directly: the identical `Edit` request that
was previously auto-denied instead came back as a proper
`{"type":"control_request","request":{"subtype":"can_use_tool","tool_name":"Edit",...}}` - the
exact protocol `ClaudeCodeSession.RespondToPermissionAsync`/`ChatSessionViewModel.OnPermissionRequested`
already implement correctly - and answering it with `allow` let the edit go through for real
(`permission_denials:[]`, file content genuinely changed on disk to `LINE ONE EDITED`).

Without `--permission-prompt-tool stdio`, the CLI's headless `-p` mode apparently never routes
built-in tool permission decisions through the stdin/stdout control-request channel at all - it
just auto-denies anything not already covered by `--permission-mode`/`--allowedTools`. This means
the extension's entire permission-card UI for built-in tools (Edit, Write, Bash, etc.) has likely
never actually been exercised in real usage until now - it was silent, correct code with no live
input reaching it, not a hidden bug in the card UI itself.

**Fix**: `Core/ClaudeCodeSession.cs` - `--permission-prompt-tool stdio` is now added unconditionally,
alongside `--include-partial-messages`/`--verbose`, for every session regardless of IDE server
state.

`dotnet build` clean (0 warnings/errors) after this fix.

**Verification status**: confirmed against the real `claude.exe` binary via the synchronous-I/O
harness (both a raw process invocation and, separately, via `ClaudeCodeSession.Start` reflected
directly). Not yet re-confirmed inside a real F5 pass with the actual chat UI's permission cards -
that's the next thing to test, and it should unblock re-testing `openDiff` (item 4) at the same time
since that's gated on the model successfully calling `Edit` in the first place.

## Fifth and sixth bugs: the built-in AskUserQuestion tool and missing multi-select UI

Once `--permission-prompt-tool stdio` was fixed, user testing surfaced a related but distinct bug:
asking Claude something ambiguous enough to trigger a clarifying question showed a generic "Allow
Question?" permission card (bullet-listing the options as plain text, with Allow/Allow for
Session/Deny buttons) instead of an actual selectable question. Clicking Allow just let the tool
"execute" with no real answer, and the model asked the same thing again in plain text.

**Root-caused live**: the built-in `AskUserQuestion` tool (present in the CLI's own `tools` list,
distinct from the already-working `ask_user_question` *control_request subtype* this extension
already handles via `AskUserQuestionRequested`/`AskUserQuestionViewModel`) sends a normal
`can_use_tool` control_request, but tagged with an extra field neither this extension nor the
generic permission-card path ever looked at:

```json
{"type":"control_request","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion",
 "input":{"questions":[...]},"tool_use_id":"...","requires_user_interaction":true}}
```

Confirmed live that answering it with a bare `{"behavior":"allow"}` (no answer data) makes the tool
report `"The user did not answer the questions."` back to the model - two live-tested guesses at
how to supply the actual answer both failed the same way:

1. Embedding an `"answer"` field into each question object inside `updatedInput` - ignored.
2. `{"behavior":"allow","answers":{"<header>":"<label>"}}` as a sibling of `behavior` (this is the
   correct shape for the *separate* `ask_user_question` subtype's response, confirmed via
   `RespondToAskUserQuestionAsync`, but wrong here) - ignored.

Rather than keep guessing against the real API (each attempt costs real usage), the correct shape
was found via the official Agent SDK docs and Python SDK source (`code.claude.com/docs/en/agent-sdk/user-input.md`,
`claude-agent-sdk-python`'s `PermissionResultAllow.updated_input`) and then confirmed live: the
answers belong **inside `updatedInput`, alongside the original unchanged `questions` array**, keyed
by each question's **`question` text** (not `header`):

```json
{"behavior":"allow","updatedInput":{"questions":[...unchanged...],
 "answers":{"Which file should I add text to?":"Class1.cs"}}}
```

Confirmed live: the model correctly received `"Got it — Class1.cs. What text would you like me to
add?"` and moved on, instead of re-asking from scratch.

**Sixth bug, found while implementing the fix**: even the already-working `ask_user_question`
control_request subtype path parses each question's `multiSelect` flag off the wire
(`AskQuestion.IsMultiSelect`) but never actually used it anywhere - `QuestionAnswerViewModel` only
ever exposed a single `SelectedIndex`, and the XAML hard-coded a single-select radio-style
`ListBox` (the comment literally said "Option list (single-select)"). Multi-select questions have
apparently never rendered as checkboxes in this extension.

**UX decision**: per user preference, the AskUserQuestion tool's request skips the Allow/Deny gate
entirely and goes straight to the interactive answer UI - asking a question has no side effects, so
gating it behind an approval click was pure friction, and it doesn't match how the dedicated
`ask_user_question` subtype already behaves (no gate either).

**Fix**: `Protocol/ClaudeStreamEvents.cs` - `PermissionRequestEvent` gains `RequiresUserInteraction`;
the questions-array parser used by the `ask_user_question` subtype is factored out into a shared
`ClaudeMessage.ParseQuestions(JArray?)` reused by both paths. `ViewModels/ChatSessionViewModel.cs` -
`OnPermissionRequested` now checks `RequiresUserInteraction && ToolName == "AskUserQuestion"` first
and routes to a new `OnAskUserQuestionToolRequested`, which builds the same `AskUserQuestionViewModel`
UI and answers via a new `RespondToAskUserQuestionToolAsync` that clones the original `input`,
merges in the `answers` object keyed by question text, and sends it as `updatedInput` on a normal
`allow` `RespondToPermissionAsync` call (an empty answers dict, e.g. from Skip, sends `deny`
instead). `ViewModels/ContentBlocks.cs` - new `SelectableOptionViewModel` (one bindable `IsSelected`
per option); `QuestionAnswerViewModel` gains an `Options` collection built from it, `IsMultiSelect`/
`IsSingleSelectWithOptions` flags, and `GetAnswer()` now returns a comma-joined list of every
checked option's value when `IsMultiSelect` is true. `Core/ClaudeCodeChatControl.xaml` - the
existing radio-style `ListBox` is now gated on `IsSingleSelectWithOptions`; a new `CheckBox`-per-
option `ItemsControl` renders when `IsMultiSelect` is true.

`dotnet build` clean (0 warnings/errors) after this fix.

**Verification status**: the `updatedInput.answers` wire shape is confirmed live against the real
`claude.exe` binary (raw process invocation, not yet through the production `ChatSessionViewModel`
code path in a real F5 pass) - the JObject this extension's new code builds was checked field-by-
field against that confirmed-working shape and matches exactly, but hasn't been exercised through
the actual chat UI yet. The multi-select checkbox UI has not been live-tested at all (no real prompt
has triggered a `multiSelect:true` question yet in either the tool-based or the dedicated
control_request path) - next F5 pass should cover both a single-select and a multi-select question.

---

## Seventh bug: sending anything while the CLI is busy was silently dropped, and /compact had no UI at all

Reported by the user with two live examples from the real official VS Code extension hosting the
session that reported this: (1) sending `/compact` while a turn is running does nothing visible
until Enter is pressed again after the turn finishes; (2) the real extension's input box stays
enabled while busy ("Queue another message...") and shows a distinct "Compacting..." status
followed by a "Compacted chat · manual · 511k tokens freed" line - our extension showed neither.

**Root cause, confirmed live (2026-08-26) via raw stdin/stdout capture against the real
`claude.exe`, this exact `-p --input-format stream-json --output-format stream-json
--include-partial-messages --verbose --permission-prompt-tool stdio` invocation:**

1. **Message queuing is a real, documented CLI feature** (confirmed both from the Agent SDK docs
   via `claude-code-guide` and by writing two `{"type":"user",...}` NDJSON lines back-to-back with
   no wait between them): the CLI buffers additional `user` lines written to stdin while a turn is
   in flight and processes them sequentially with no extra wire ceremony - no special "queue"
   subtype needed, just write the next line. The client-originated `interrupt` control_request's
   `"cancel_queued": true` field (already implemented for Stop) exists specifically to clear this
   queue. Each `result` message carries `"queued_turn_count"`, telling the client how many more
   turns are already queued behind the one that just finished.

   But this extension's `CanSend` was `!IsBusy && ClaudeNotFoundMessage == null`, and
   `SendCurrentInputAsync` bailed out immediately when `!CanSend` - so typing anything (a command
   or plain text) while a turn was running did **nothing at all**, silently. The text just sat in
   the box. Only after the turn finished (and `CanSend` became true again) did pressing Enter a
   second time actually send it - which explains why it looked like "the command gets sent as a
   text message, not as a command" once it did go through: by then it genuinely was just an
   ordinary un-queued turn, not a queued one.

2. **`/compact`'s wire shape is real but doesn't match the Agent SDK docs' description**
   (`SDKCompactBoundaryMessage`/"compact_boundary" is real, but the docs don't show the actual
   NDJSON for this transport). Captured live, twice - once a failure (too little history) and once
   a real success:
   ```json
   {"type":"system","subtype":"status","status":"compacting", ...}
   {"type":"system","subtype":"status","status":null,"compact_result":"failed","compact_error":"Not enough messages to compact.", ...}
   ```
   ```json
   {"type":"system","subtype":"status","status":"compacting", ...}
   {"type":"system","subtype":"status","status":null,"compact_result":"success", ...}
   {"type":"system","subtype":"init", ...}
   {"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"manual","pre_tokens":33547,"post_tokens":885,"cumulative_dropped_tokens":32662,"duration_ms":12884, ...}, ...}
   ```
   `cumulative_dropped_tokens` is exactly the "511k tokens freed" figure the real extension
   displays. On success, the CLI also injects a synthetic continuation-summary `user`-role message
   (`isSynthetic:true`) plus a `<local-command-stdout>Compacted </local-command-stdout>`
   (`isReplay:true`) message - both harmless to this extension, since `ParseUserMessage` already
   only recognizes `tool_result` content items and returns null for anything else (these have a
   plain string `content`, not an array), so they were already silently ignored rather than
   rendered as garbage chat bubbles.

**Fix:**
- `Protocol/ClaudeStreamEvents.cs` - `StatusMessage` gains `CompactResult`/`CompactError`; new
  `CompactBoundaryEvent` (`Trigger`, `PreTokens`, `PostTokens`, `TokensFreed`) parsed from the
  `compact_boundary` subtype; `ResultMessage` gains `QueuedTurnCount` from `queued_turn_count`.
- `Core/ClaudeCodeSession.cs` - new `CompactBoundary` event raised from `HandleLine`.
- `ViewModels/ChatMessageViewModel.cs` - `ChatRole` gains a `System` value for unbubbled notices.
- `ViewModels/ChatSessionViewModel.cs`:
  - `CanSend` no longer depends on `IsBusy` - only on the CLI actually being found. Sending while
    busy now just writes the queued turn to the wire immediately, same as the real extension.
  - `SendMessageAsync` no longer calls `ResetTurnState()` (that would corrupt an in-flight turn's
    `_currentAssistantMessage`/`_blocksByIndex` if called while queuing a second message) - the
    next turn's state is set up naturally when its own `message_start` arrives via the existing
    `OnMessageStarted`/`EnsureAssistantMessage` path.
  - `OnTurnCompleted` only clears `IsBusy`/resets `StatusText` to "Ready" when
    `result.QueuedTurnCount == 0`, so the UI stays "Working…" across a queued sequence instead of
    flickering to "Ready" between turns.
  - `OnStatusChanged` maps `"requesting"`→"Working…", `"compacting"`→"Compacting…", and a
    `CompactResult == "failed"` status line to a new system-notice chat entry.
  - New `OnCompactBoundary` appends a system-notice chat entry: `"Compacted chat · {trigger} ·
    {formatted tokens} tokens freed"`, reusing the existing `ResultFooterViewModel` (already has
    `Text`/`IsError` with matching visual styling) as the notice's sole content block.
- `Controls/TemplateSelectors.cs` / `Core/ClaudeCodeChatControl.xaml` - `ChatMessageTemplateSelector`
  gains a `SystemTemplate`; new `SystemMessageTemplate` renders a `ChatRole.System` message's
  `Blocks` centered and unbubbled (matching the real extension's plain centered notice line).

`dotnet build` clean (0 warnings/errors) after this fix.

**Verification status**: the queuing mechanism and both compact wire shapes (success and failure)
are confirmed live via raw process capture. The production code path - sending a queued message
from the actual chat UI while busy, and seeing the new "Compacted chat..."/"Compacting…" UI render
in a real F5 session - has not been exercised yet; next F5 pass should cover both.

---

## Eighth bug: no recovery path when a turn fails or the process dies mid-turn

Reported with a real screenshot of the official VS Code extension hitting its own session limit:
it shows an explicit "You've hit your session limit · resets HH:MM" banner and a "Try again"
button, and the user noted Claude Code Desktop/VS Code both retain the in-flight message so a
follow-up "Continue" isn't ambiguous. Our extension's `OnProcessExited` just set a quiet
status-strip line ("Claude Code exited unexpectedly") with no chat-visible notice and no way to
recover the message except retyping it from memory.

**Live-tested first, cheaply, without needing to actually exhaust a real quota:** started a real
session, sent a message, then killed the `claude.exe` process mid-turn (after streaming started,
before any `result`) to simulate a crash - then reconnected with a fresh process and `--resume
<same-session-id>` and sent "Continue". Result: the model correctly referenced the abandoned
question unprompted ("If you meant the strawberry/volcano question... let me know"), confirming
**the CLI's own on-disk session log already retains an interrupted turn** even after a hard kill -
nothing is silently dropped server-side in this failure mode. But the model still couldn't just
answer directly from a bare "Continue" - it needed the actual content restated, exactly the
ambiguity the user was flagging.

**Fix - a verbatim "Try again", not a rewritten "Continue":** rather than trust CLI/server-side
resume fidelity across every possible failure mode (a genuine quota rejection may never even reach
the API to be logged in the first place, unlike a mid-stream kill), the extension now tracks
`_lastSentText` (the most recently sent turn) in `ChatSessionViewModel`, cleared only on a
successful (`!IsError`) turn completion. On a failed turn (`OnTurnCompleted` with `IsError`) or an
unexpected process exit while `IsBusy`, a new `AddRetryNotice` appends a `RetryNoticeViewModel`
block whose command resends `_lastSentText` verbatim through the normal `SendMessageAsync` path -
sidestepping the ambiguity entirely instead of hoping the model infers intent from "Continue".

The notice text itself tries to distinguish a genuine usage-limit hit from any other failure via a
`ContainsRateLimitHint` keyword check ("rate limit"/"usage limit"/"session limit"/"quota"/"out of
credits") against the result's error text (turn-failure case) or the tail of captured stderr
(process-exit case). When it matches and live rate-limit data is already available (from the
existing `RateLimitEvent`/`AccountUsageViewModel` wiring), the notice reuses the already-verified
`SessionResetLabel` to show "You've hit your usage limit · resets HH:MM", matching the real
extension's banner; otherwise it falls back to a generic "your message is still here" notice.

- `ViewModels/ContentBlocks.cs` - new `RetryNoticeViewModel` (`Text`, `RetryCommand`).
- `Controls/TemplateSelectors.cs` / `Core/ClaudeCodeChatControl.xaml` - new `RetryTemplate`
  (message + full-width "Try again" button).
- `ViewModels/ChatSessionViewModel.cs` - `_lastSentText` field; `AddRetryNotice`/
  `ContainsRateLimitHint` helpers; wired from both `OnTurnCompleted`'s error branch and
  `OnProcessExited`.

`dotnet build` clean (0 warnings/errors) after this fix.

**Verification status**: the underlying resume-preserves-an-interrupted-turn behavior is confirmed
live via a real simulated mid-turn kill. The actual quota-exhaustion keyword detection heuristic
(`ContainsRateLimitHint`) is unverified against a real 429/out-of-credits wire response - deliberately
not live-tested by exhausting real quota to reproduce it. The retry notice UI itself (rendering,
button, resend) has not been exercised through a real F5 pass yet.
