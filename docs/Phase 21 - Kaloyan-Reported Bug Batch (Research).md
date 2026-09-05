# Phase 21 - Kaloyan-Reported Bug Batch (Research)

**Status: research only. No code changed in this phase.** Kaloyan filed seven issues from his own
use of the extension (logged verbatim in `Teron_Extensions To-dos` in the top-level
`GLOBAL_TODO.md`, items 1-7). This phase root-causes all seven against the actual source, with
file:line citations, so a future implementation phase can fix them without re-deriving the
mechanism. Three parallel research passes plus direct inspection were used; every finding below
was confirmed by reading the full code path end-to-end, not guessed from symptoms, except where
explicitly marked unverified.

## 1. Session history is not scoped per-workspace

**Report:** the extension lists every session ever recorded on the machine in its History panel,
regardless of which VS solution is currently open — unlike the official VS Code extension, which
only shows sessions for the current workspace.

**Root cause (confirmed):** the store is a single flat, machine-wide JSON file with no workspace
filtering anywhere in the read path.

- `ViewModels/SessionHistoryStore.cs:11-13` — path is fixed at
  `%AppData%\TeronClaudeCodeVS\sessions.json`, one file for the whole machine, unlike the CLI's own
  per-cwd `~/.claude/projects/<encoded-cwd>/` layout.
- `ViewModels/SessionHistoryStore.cs:15-26` (`Load()`) — deserializes the whole file, sorts by
  `LastUsed`, takes the top 100. No cwd/workspace parameter exists.
- `ViewModels/SessionHistoryEntry.cs:22-23` — each entry already carries a `WorkingDirectory`
  field, so the data needed to filter is present in every row.
- `ViewModels/ChatSessionViewModel.cs:531-533` — the constructor calls `Load()` and copies **every**
  returned entry straight into the UI-bound `SessionHistory` collection (`:165`) with zero
  comparison against `_workingDirectory` (the current solution's path, set at `:586`, exposed at
  `:489`).

The data needed to fix this already exists on every entry — it's just never applied as a filter.

**Fix shape:** filter `SessionHistory` to entries whose `WorkingDirectory` matches (or is under)
the current `_workingDirectory` before populating the observable collection at
`ChatSessionViewModel.cs:531-533`. Cheaper than partitioning the store file, since the field
already exists.

## 2. Permission mode requires a restart to take effect

**Report:** changing permission mode from the composer button should apply immediately; it
currently only takes effect after a restart.

**Root cause (confirmed):** `--permission-mode` is a launch-time-only CLI flag, and the extension's
only mechanism to apply a change is killing and relaunching the process — gated on the session
being idle, with no re-check afterward.

- `Core/ClaudeCodeSession.cs:115-161` — `Start()` builds argv once; `permissionMode` is appended as
  `--permission-mode <value>` (`:157-160`) and never revisited.
- `ViewModels/ChatSessionViewModel.cs:372-381` — the `SelectedPermissionMode` setter (bound from the
  composer button, `Core/ClaudeCodeChatControl.xaml:2044,2212,461`) calls `RestartIfIdle()`.
- `ViewModels/ChatSessionViewModel.cs:755-759` — `RestartIfIdle()` only restarts when
  `_session.IsRunning && !IsBusy && Messages.Count > 0`. If a turn is in flight, this is a **silent
  no-op** — the UI shows the new mode selected, but the running process is never told, and nothing
  re-checks once `IsBusy` goes back to false (`:413-439` has no pending-restart logic).
- `ViewModels/ChatSessionViewModel.cs:1501-1508` — a prior-session comment records that a live
  mode-switch was investigated and abandoned: no `updatedPermissions`/mode field was found on
  `can_use_tool` responses.
- `Core/ClaudeCodeSession.cs:504-616` — the extension already has a generic bidirectional
  `control_request` channel (`SendControlRequestAsync`, used for `interrupt`, `rewind_files`,
  `side_question`, `submit_feedback`, `remote_control`) over the same stdin. No `set_permission_mode`
  subtype exists anywhere in this file or `Protocol/ClaudeStreamEvents.cs`.

So the button isn't purely cosmetic — it restarts the process (resuming the same session id) when
idle, which is why it "works" after a restart. When a turn is in progress, the change is dropped
with no feedback and never retried.

**Unverified:** whether the CLI's streaming-input mode accepts a dedicated `set_permission_mode`
control-request subtype (the public Claude Agent SDK's `setPermissionMode` does this). The prior
comment only rules out a field on `can_use_tool` responses, not a standalone control-request — that
avenue may be unexplored rather than confirmed impossible. Worth a live wire-capture check before
assuming a restart is unavoidable.

**Fix shape:** either (a) verify live whether the CLI accepts `set_permission_mode` as a
control_request and route mode changes through `SendControlRequestAsync` instead of a restart, or
(b) if genuinely restart-only, make the change durable — track it as pending and apply the restart
the moment `IsBusy` flips back to `false`, instead of only checking at selection time.

## 3. Switching the docked panel's tab interrupts a running conversation

**Report:** switching to a different tab in the shared docked panel and back kills/interrupts an
in-flight response.

**Root cause (confirmed end-to-end):** `ClaudeCodeChatControl` wires `Unloaded += OnUnloaded` in its
constructor (`Core/ClaudeCodeChatControl.xaml.cs:44`). `OnUnloaded` (`:120-126`) unconditionally
calls `_vm.Dispose()`. `ChatSessionViewModel.Dispose()` (`ViewModels/ChatSessionViewModel.cs:2475-2480`)
calls `StopSessionCore()`, which (`:741-746`) calls `_session.Dispose()` with **no check for
whether a turn is in flight** — unlike the deliberate `StopSessionAsync()` path (`:671-695`), which
tries a graceful `SendInterruptAsync()` control-request first and only kills the process as a
fallback. `ClaudeCodeSession.Dispose()` (`Core/ClaudeCodeSession.cs:768-791`) then calls
`_process.Kill()` (`:779`) on the live `claude` CLI process, destroying the whole subprocess rather
than issuing a soft interrupt.

`ClaudeCodeToolWindow.cs:5-13` is a bare `ToolWindowPane` with no `IVsWindowFrameNotify`/`OnShow`
overrides — there is no VS-level visibility hook here; the whole bug is WPF's own `Unloaded` event.
VS tool-window tabs sharing a docked pane commonly remove the inactive tab's visual content from
the tree (a shared-pane content swap), which fires `Unloaded` on `ClaudeCodeChatControl` even
though the tool window itself isn't closing — just hidden behind a sibling tab. `Loaded` fires
again on tab-back (`:43/52`), re-running `OnLoaded`, which even calls `_vm.StartSession()` again if
`Initialize(...)` returns true (`:109-110`) — consistent with the user seeing the conversation
reset rather than a clean close.

**Unverified inference:** that VS's shared-pane tab switch is specifically what triggers `Unloaded`
here — not traced through VS SDK internals, but this is standard, well-documented WPF/VS
tool-window behavior and matches the reported symptom exactly.

**Fix shape:** `OnUnloaded` should not treat "removed from visual tree" as "tool window closed."
Only real teardown (an actual `IVsWindowFrameNotify.OnClose`/frame-level `Dispose`) should kill the
session; a WPF `Unloaded` from a tab switch should, at most, detach event subscriptions
(`PropertyChanged`, dictation) and leave `_session`/`_vm` alive so the running turn keeps streaming
in the background and is simply re-displayed when `Loaded` fires again.

## 4. Queued messages render out of chronological order

**Report:** messages queued while the agent is responding always render pinned at the bottom,
regardless of when the corresponding response actually arrives — garbling the visible timeline.

**Root cause (confirmed):** there is no separate "pending queue" data structure. `SendMessageAsync`
(`ViewModels/ChatSessionViewModel.cs:697-739`) is not gated by `IsBusy` — `CanSend` only checks
`ClaudeNotFoundMessage == null` (`:462`) — so every send, busy or idle, immediately does
`Messages.Add(userMessage)` (`:715`), appending to the end of the flat
`ObservableCollection<ChatMessageViewModel> Messages` (`:159`). A comment at `:729-734` confirms
this is deliberate: queuing happens CLI-side (it queues additional `user` lines written mid-turn
and runs them sequentially), not in the ViewModel.

On the response side, `EnsureAssistantMessage()` (`:1142-1149`) is also a bare append, triggered the
first time content arrives for a turn (`OnMessageStarted`, `:1136`); `_currentAssistantMessage` is
only cleared in `ResetTurnState()` from `OnTurnCompleted` (`:1608`). `ChatMessageViewModel` has no
timestamp/sequence/turn-correlation field (just `Role` and `Blocks`), and
`Core/ClaudeCodeChatControl.xaml:1284`'s `MessageList` binds `Messages` directly with no
`SortDescriptions` — pure insertion order, nothing corrects it.

Concretely: user sends msg1; while its response is still streaming, sends msg2 and msg3 — the
transcript becomes `[msg1, response1, msg2, msg3]` immediately, both queued bubbles landing
back-to-back at the bottom. When the CLI works through the queue, turn 2's response is appended
only when its `message_start` arrives — *after* msg3 was already rendered — producing
`[msg1, response1, msg2, msg3, response2]`, with response2 visually sitting below msg3, looking
like it answers the wrong message.

**Fix shape:** either (a) give queued messages a turn/sequence correlation and insert each response
immediately after the user message it actually answers, or (b) render messages sent while busy as
visually distinct "queued, not yet running" bubbles that get promoted/repositioned once their turn
actually starts, instead of flat-appending them into the same list as in-flight content.

## 5. "Try again" resends the original text, not a literal retry

**Report:** "try again" doesn't send a literal "try again" — it resends the user's last message.

**Root cause (confirmed):** the only such control is `RetryTemplate`
(`Core/ClaudeCodeChatControl.xaml:948-956`), bound to `RetryCommand` on `RetryNoticeViewModel`
(`ViewModels/ContentBlocks.cs:626-630`). It appears only after a failed/errored turn — added via
`AddRetryNotice` from `OnTurnCompleted` on error (`ChatSessionViewModel.cs:1575-1580`) or
`OnProcessExited` (`:1644-1646`) — never as a generic "regenerate" on a successful response.

`AddRetryNotice` (`:1663-1674`) captures `retryText = _lastSentText` — the user's literal original
text, saved in `SendMessageAsync` at `:720` and cleared only after a successful turn (`:1583`). The
button's action, `() => _ = SendMessageAsync(retryText)` (`:1673`), goes through the **same send
pipeline as typed input**, per Bug 4's trace: it creates a brand-new user `ChatMessageViewModel`
and appends it, then sends it as a genuinely new turn — visibly duplicating the original prompt as
a second turn, rather than sending a short "try again" instruction or silently re-invoking the same
request without a new bubble.

This is deliberate, not accidental: a doc comment at `:1655-1661` records that a killed/errored CLI
process was live-verified (2026-08-26) not to reliably pick up an abandoned prior message on
`--resume`, so resending the exact original text was chosen to sidestep CLI resume fidelity rather
than trusting a bare "continue"/"try again" utterance.

**Fix shape:** either relabel the affordance as "Resend" to match actual behavior, or change it to
not add a second visible user bubble at all — re-issue the same request payload to the CLI directly
without pushing a duplicate `ChatMessageViewModel` into `Messages`.

## 6. Expand button forces scroll-to-bottom

**Report:** expanding a message/tool-call card always yanks the chat scroll to the very bottom.

**Root cause (confirmed end-to-end):** expand/collapse is a plain WPF `Expander` bound to
`IsExpanded` (`Core/ClaudeCodeChatControl.xaml:569,586`), backed by simple bools on
`ThinkingBlockViewModel` (`ViewModels/ContentBlocks.cs:74-78`) and the tool-call view model
(`:205-209`) with no scroll logic of their own. The `Expander` lives inside `ChatScrollViewer`
(`ClaudeCodeChatControl.xaml:1280-1286`), which wires `ScrollChanged="OnChatScrollChanged"`. That
handler (`ClaudeCodeChatControl.xaml.cs:1806-1814`) is a "sticky scroll" implementation: on any
`ExtentHeightChange > 0` it computes `wasAtBottom` from the pre-change extent/offset and, if true,
unconditionally calls `ChatScrollViewer.ScrollToEnd()` (`:1812`).

This fires for *any* extent growth — including a purely local `Expander` toggle, not just a new
message arriving. Toggling `IsExpanded` grows the card's rendered height, which WPF reports as
`ExtentHeightChange`. Since users typically expand a card while reading recent output (already near
the bottom) or in short conversations where `wasAtBottom` is trivially true, the handler yanks the
view to the true end of the transcript, overriding the position of the card the user just expanded.

**Fix shape:** distinguish "a new message/block was appended" from "existing content changed size."
Either scope the sticky-scroll trigger to `Messages.CollectionChanged` (Add) instead of generic
`ScrollChanged`, or set a suppression flag immediately before flipping `IsExpanded` (cleared after
layout settles) so `OnChatScrollChanged`'s bottom-check is skipped for that specific layout pass.

## 7. Visual/accessibility improvements needed

**Report:** "various visual improvements are required for both better visibility AND
accessibility."

This is broader than the prior style-parity work: Phase 8/9 ([[baseline-parity-implementation-2026-08-29]])
already collapsed the measured *visual* gaps against the baseline extension (9 font sizes → 2, 8
corner radii → 2, via `Core/ChatTheme.xaml`). What Phase 8/9 did not address is genuine
**accessibility** (screen-reader/assistive-tech support), which is a different axis from visual
consistency with a reference extension.

**Confirmed by direct inspection (not delegated — read directly this session):**
`Core/ClaudeCodeChatControl.xaml` carries 124 `AutomationProperties.AutomationId` values — added in
Phase 6 purely to let the UIA test harness drive controls — versus only **2**
`AutomationProperties.Name` values in the entire file. Concretely, icon-only buttons have no
accessible name at all:

- `SendButton` (`:2872-2876`): `Content` is a bare `TextBlock Text="➤"`, `AutomationId="SendButton"`,
  no `AutomationProperties.Name`. WPF's default `ButtonAutomationPeer.GetNameCore()` falls back to
  `Content`, not `ToolTip` — so a screen reader announces the glyph character, not "Send."
- `StopButton` (`:2877-2881`): same pattern, `Content="■"`.
- At least six panel-close buttons repeat the identical pattern: `CloseHistoryButton` (`:1352-1354`),
  `CloseAccountUsageButton` (`:2243-2245`), `CloseMcpButton` (`:2406-2409`),
  `ClosePluginsButton` (`:2505-2507`), all `Content="✕"` with no `Name`.
- By contrast, `MicButton` (`:2852-2856`) does it correctly — `AutomationProperties.Name="Dictate"`
  alongside its `AutomationId` — proving the pattern is known in this codebase, just not applied
  consistently.

This means Narrator/NVDA/JAWS users cannot currently identify what most icon-only controls in this
panel do — a genuine, confirmed WCAG 4.1.2-class gap, independent of anything Phase 6-9 measured.

**Not independently verified this session (would need a live pass with an actual screen reader or a
contrast-ratio tool, per this project's live-verification standard —
[[feedback-live-verification-rigor]]):**
- Color contrast of `ErrorBrush`/`SuccessBrush`/`HairlineBrush` (fixed hex values in
  `ChatTheme.xaml:45-51`) against both VS light and dark theme backgrounds.
- Keyboard-only navigation completeness (tab order, whether every actionable surface — including
  popups, which Phase 6 already noted have no automation peer of their own — is reachable without a
  mouse).
- Whether transcript updates use `AutomationProperties.LiveSetting` so a screen reader announces new
  agent output as it streams in (grep found zero `LiveSetting` usages in the file).

**Fix shape:** treat this as two separable work items for a future phase — (a) a mechanical pass
adding `AutomationProperties.Name` to every icon-only/glyph-only interactive control (send, stop,
mic, all `✕` close buttons, expand/collapse toggles, etc.), which is low-risk and directly closes
the confirmed gap; (b) a live-verified pass with real assistive tech for contrast, live-region
announcements, and keyboard-only traversal, which needs the same rigor as prior live-verification
phases rather than being inferred from source alone.

## Method note

Bugs 1-6 were investigated by three parallel research agents (one per pair: 1+2, 3+6, 4+5), each
read-only and reporting file:line citations plus an explicit confidence level; all six were
confirmed end-to-end from the reported symptom through the actual code path, not inferred from
symptoms alone. Issue 7 was investigated directly in this session via targeted grep + read against
`ClaudeCodeChatControl.xaml`. No fixes were implemented in this phase — this doc is the handoff for
whichever future phase number picks these up.
