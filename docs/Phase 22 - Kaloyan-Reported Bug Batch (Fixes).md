# Phase 22 - Kaloyan-Reported Bug Batch (Fixes)

Implements all 7 items root-caused in
[Phase 21](Phase%2021%20-%20Kaloyan-Reported%20Bug%20Batch%20(Research).md). **Verification status:
build clean (`TeronClaudeCodeVS.csproj` and the test project both build with 0 errors) and the
xUnit suite is green (181/182 - the one remaining failure, `SessionTitleTests.A_whole_file_read_still_finds_the_title`,
is pre-existing and unrelated: it depends on a specific live transcript file on this machine that
has since been deleted, which the test is explicitly designed to skip out loud over rather than
fail on - this build's vstest runner just doesn't render that dynamic skip as a clean "Skipped").
**No live F5 pass has been run yet** on any of these seven fixes - per this project's own standard
([[feedback-live-verification-rigor]]), treat this phase as implemented-and-tested, not
live-verified, until that pass happens.

## 1. Session history now scoped to the current workspace

`ChatSessionViewModel`'s constructor still loads `_allSessions` (the full, machine-wide store,
unchanged), but no longer copies it into the UI-bound `SessionHistory` collection - `_workingDirectory`
isn't known yet at that point. `Initialize(...)` now populates `SessionHistory` right after it sets
`_workingDirectory`, filtering to entries whose `WorkingDirectory` matches (case-insensitive,
trailing-separator-tolerant via the new `IsSameWorkingDirectory` helper).

Because `SessionHistory` is now a filtered *subset* of `_allSessions` rather than an index-aligned
mirror, `SaveOrUpdateSession` was rewritten to stop assuming the two lists move in lockstep: the
move-to-front and 100-entry-cap trim now look up/remove by reference in each list independently
(`SessionHistory.Remove(oldest)` rather than `SessionHistory.RemoveAt(sameIndex)`), so trimming the
globally-oldest entry can no longer remove the wrong row from the current workspace's view.

`tests/.../SessionTitleRefreshTests.cs` constructed `ChatSessionViewModel` directly and asserted
against `SessionHistory` without ever calling `Initialize` - relying on the old
population-at-construction behavior. Fixed by calling `vm.Initialize(null, Cwd)` right after
construction in all three tests; the fixture rows in `HistorySandbox.Seed()` are already seeded
with `cwd` equal to this exact repo path, so the filter naturally admits all three.

## 2. Permission mode (and model / thinking-level) changes made mid-turn are no longer dropped

`RestartIfIdle()` (shared by `SelectedModel`, `SelectedPermissionMode`, and `SelectedThinkingLevel`
- this bug applied to all three, not just permission mode) used to silently no-op if a turn was in
flight. It now sets a new `_restartPending` flag instead of doing nothing; the `IsBusy` setter's
false-transition applies the deferred restart the moment the session actually goes idle again
(guarded on the session still being alive - if it isn't, the next `StartSession()` picks up current
settings anyway, same as before).

**Not resolved, and flagged as open in Phase 21**: whether the CLI's `control_request` channel
secretly supports a live `set_permission_mode` (as the public Agent SDK's `setPermissionMode`
does), which would let the mode apply without any restart at all, even mid-turn. That needs a live
wire-capture to answer and was out of scope for this pass; the durable-restart fix here is strictly
better than before regardless of that answer.

## 3. Tab-switch no longer kills the running session

Two changes, both required - fixing only one would have left the other still killing the session:

- `ClaudeCodeChatControl.OnUnloaded` no longer calls `_vm.Dispose()`. It only stops/disposes the
  mic engine (`VoiceInput`), which is lazily recreated on next use and safe to tear down on any
  visibility change.
- `OnLoaded` now guards its entire one-time setup (options defaults, cwd resolution, project
  indexing, `Initialize()` + `StartSession()`) behind a new `_initialized` flag, since WPF fires
  `Loaded` again on every tab-switch-back even though the tool window never closed - without this
  guard, `OnLoaded` itself would still restart the CLI process on every switch even with the
  `OnUnloaded` fix in place. A re-entry now just restores input focus.

Real teardown moved to a new `ClaudeCodeChatControl.DisposeSession()`, called from
`ClaudeCodeToolWindow`'s own `Dispose(bool disposing)` override - the actual close/teardown point,
as opposed to WPF's `Unloaded`, which fires on a mere shared-pane tab switch.

## 4. Queued messages now render in the order their responses actually answer them

Added `_pendingUserMessages`, a FIFO queue of user `ChatMessageViewModel`s awaiting a response
(the CLI processes queued turns strictly sequentially, per the existing comment in
`SendMessageAsync`, so FIFO order is always correct). `SendMessageAsync` enqueues the new user
bubble; `EnsureAssistantMessage` dequeues the front entry when a new assistant turn starts and
inserts the response immediately after that user message's own position in `Messages`, instead of
always appending at the end. `StopSessionCore` clears the queue, since any turns still queued
behind a torn-down session will never be answered - without that, a stale queue entry could
mis-position a later, unrelated response.

## 5. "Try again" relabeled to "Resend"

The underlying behavior - resending the original message text verbatim as a new turn - was left
unchanged: it's a deliberate, live-verified design (a 2026-08-26 finding that `--resume` doesn't
reliably recover an abandoned message on every failure mode, documented in the `AddRetryNotice`
doc comment). Changing that mechanism risked reintroducing the exact resume-fidelity problem it
was built to avoid. The fix instead makes the UI honest about what it does: the button now reads
"Resend" with a tooltip ("Resends your original message verbatim as a new turn"), instead of "Try
again," which implied an in-place regenerate.

## 6. Expanding a card no longer scrolls the chat to the bottom

`OnChatScrollChanged`'s sticky-scroll fired on *any* `ExtentHeightChange`, unable to distinguish "a
new message was appended" from "an existing card just grew because it was expanded." Both
`Expander`s (`ThinkingBlockTemplate`, `ToolCallTemplate`) now wire `Expanded`/`Collapsed` to a new
`OnCardExpanderToggled` handler, which sets a `_suppressAutoScroll` flag and schedules its own
clear at `DispatcherPriority.ContextIdle` - long enough to cover every `ScrollChanged` the resulting
layout pass produces, short enough to be gone before the next real interaction.
`OnChatScrollChanged` now returns immediately while that flag is set.

## 7. Accessibility: icon-only controls now carry real accessible names

Added `AutomationProperties.Name` to all 15 icon-only/glyph-only interactive controls that had
none: `SendButton`, `StopButton`, `NewSessionButton`, `HistoryButton`, `SettingsButton`,
`AddMenuButton`, `PaletteButton`, the plan-comment remove button, both history-row buttons (rename
`✎` / delete `✕`), the pending-image and pending-file remove buttons, and the four panel-close `✕`
buttons (History, Account & Usage, MCP servers, Plugins, Rewind). `MicButton` already did this
correctly (`Name="Dictate"`) and was the proof the pattern was known, just inconsistently applied.

**Deliberately not attempted this pass** (per Phase 21's own scoping - these need a live pass with
real assistive tech or a contrast tool, not source-reading): color-contrast verification of the
fixed status brushes against both VS themes, full keyboard-only traversal (including popups, which
have no automation peer of their own per the Phase 6 finding), and `AutomationProperties.LiveSetting`
on the transcript so a screen reader announces streamed output as it arrives. These remain open for
a future pass.

## Addendum (same day): item 1's real root cause was deeper than the original fix

Kaloyan installed the `v0.6.0-beta.1` build and immediately found the per-workspace filter above
was necessary but not sufficient. Side-by-side screenshots of the same `Teron_Game_Engine`
workspace: the official VS Code extension's History showed a dozen-plus sessions spanning weeks;
this extension's History showed exactly **one**, from the day before, and that one wasn't even
among the sessions VS Code listed.

**The real bug**: this extension has always kept its own small, flat cache
(`%AppData%\TeronClaudeCodeVS\sessions.json`), written to only when a turn completes *while running
through this extension itself* (`SaveOrUpdateSession`, from `OnTurnCompleted`). It never read the
CLI's own per-cwd transcript store (`~/.claude/projects/<encoded-cwd>/*.jsonl`) - the same real,
complete history the official VS Code extension reads directly, which accumulates a session
regardless of which client started it (this extension, a terminal, VS Code). The original fix in
this doc correctly scoped that small local cache to the current workspace, but scoping the wrong
data source doesn't produce the right list - it just filters an already-incomplete one. A user who
mostly runs Claude Code via terminal or VS Code, and only occasionally through this extension,
would see almost nothing.

**The fix**: `ChatSessionViewModel` gained `BeginDiscoverUntrackedSessions()`, run once from
`Initialize()` and again every time `OpenSessionHistory()` opens the panel (mirroring
`BeginRefreshSessionTitles()`'s existing off-thread pattern exactly). It lists `*.jsonl` files in
the CLI's real per-cwd folder (`TranscriptReplay.FindProjectDirectory`, a small refactor exposing
logic `FindTranscriptPath` already had internally), skips any session id already known to
`SessionHistory`, and reads a title for each new one via the existing `SessionTitleReader`. These
"discovered" rows are added to the UI-bound `SessionHistory` only - never persisted into
`_allSessions`/`sessions.json` - so the method stays a pure, repeatable read with nothing to
reconcile if a discovered session is never touched again. `SaveOrUpdateSession` gained a matching
fallback lookup (`_allSessions` first, then `SessionHistory`) so resuming a discovered session and
completing a turn promotes the *same* entry object into the persisted cache instead of creating a
visible duplicate row.

Verified: build clean, xUnit suite still 181/182 (same pre-existing, unrelated failure). Not yet
re-verified live against the actual `Teron_Game_Engine` discrepancy from the screenshots - that
needs Kaloyan's own machine, since it depends on his real `~/.claude/projects/` contents.

## Second addendum (same day): resize/tab-switch lag, and Delete Session's real semantics

Two more items from Kaloyan, both while testing `v0.6.1-beta.1` live.

**Lag/stutter resizing the docked pane, and switching to/from its tab.** Root cause: the message
transcript (`MessageList`) was a plain `ItemsControl` with a default `StackPanel`, so every message
ever sent was fully realized in the visual tree at once - and most messages hold at least one
`FlowDocumentScrollViewer` (`Controls/MarkdownViewer.xaml`), one of WPF's most expensive controls
to lay out, since a `FlowDocument` repaginates on every measure pass. Any full-tree layout pass -
resizing the pane, or a tab switch firing `Unloaded`/`Loaded` on the whole tree (per the earlier
tab-switch fix, the tree itself isn't destroyed, but layout still has to be redone on reattachment)
- forced every message's `FlowDocument` to repaginate simultaneously, regardless of how many were
actually visible. Fixed by switching `MessageList`'s panel to `VirtualizingStackPanel` with
`VirtualizationMode="Recycling"` and `VirtualizingPanel.ScrollUnit="Pixel"`, plus
`CanContentScroll="True"` on `ChatScrollViewer` (required for the panel's `IScrollInfo` to actually
be used). `ScrollUnit="Pixel"` was specifically chosen to preserve the exact continuous pixel-based
scrolling the existing code already depends on (`ScrollToEnd()`, the `VerticalOffset`/`ExtentHeight`
math in `OnChatScrollChanged`) - no changes needed there. Off-screen messages are no longer
realized at all, so a resize or tab switch only lays out what's actually visible.

**Not yet live-verified**: whether virtualization's extent-height estimation (which can shift
slightly as items realize/derealize during scrolling) interacts poorly with `OnChatScrollChanged`'s
`wasAtBottom` sticky-scroll heuristic from the earlier expand-scroll fix - reasoned through as
safe (both read the same `ScrollViewer` properties regardless of virtualization), but genuinely
needs a live pass with a long conversation, not just a build check.

**"Delete Session" didn't actually stick for a session outside this extension's own tracked
list.** Investigated by reading the official VS Code extension's actual installed source
(`%USERPROFILE%\.vscode\extensions\anthropic.claude-code-2.1.261-win32-x64\`) rather than
guessing: its own "Delete" button is literally labeled **"Archive session"**, and traces to
`context.globalState.update("hiddenSessionIds", [...])` - it never touches the real transcript
file. This extension's `DeleteSessionEntry` only ever removed the row from its own local list, so
a session discovered on disk (via `BeginDiscoverUntrackedSessions`, added earlier this same day)
but never resumed through this extension would simply reappear the next time History refreshed,
since nothing remembered it had been dismissed. Fixed by adding a small persisted hidden-ids file
(`SessionHistoryStore.LoadHiddenIds`/`SaveHiddenIds`, kept separate from `sessions.json` since a
session can be hidden before ever being tracked there), consulted by both the workspace filter in
`Initialize()` and the discovery scan. Deliberately scoped to match only what was asked (Delete
hides permanently) - did not add an "Archived" tab/unhide UI, which the real extension also has
but which nobody requested here.

Both committed (`08821be`, `c720eab`). Build clean, xUnit suite still 181/182 (same pre-existing,
unrelated failure).

## What still needs Kaloyan's own hands

A real F5 pass against a live Exp instance, covering at minimum: switching away from and back to
the tool window's tab mid-response (item 3, the highest-blast-radius fix in this batch), changing
permission mode while a turn is in flight and confirming it now applies once the turn ends (item
2), sending two or three messages back-to-back while a turn streams and checking the transcript
order (item 4), and a screen reader spot-check of the newly-named icon buttons (item 7). Per
[[feedback-live-verification-rigor]], nothing here should be treated as done until that happens -
this doc records what changed and why, not that it's been proven live.
