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

## What still needs Kaloyan's own hands

A real F5 pass against a live Exp instance, covering at minimum: switching away from and back to
the tool window's tab mid-response (item 3, the highest-blast-radius fix in this batch), changing
permission mode while a turn is in flight and confirming it now applies once the turn ends (item
2), sending two or three messages back-to-back while a turn streams and checking the transcript
order (item 4), and a screen reader spot-check of the newly-named icon buttons (item 7). Per
[[feedback-live-verification-rigor]], nothing here should be treated as done until that happens -
this doc records what changed and why, not that it's been proven live.
