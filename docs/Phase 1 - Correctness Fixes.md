# Phase 1 - Correctness Fixes

**Date:** 2026-08-26

First implementation pass of the [Full CLI Parity Roadmap](Phase%200%20-%20Full%20CLI%20Parity%20Roadmap.md)
- six real bugs found during that survey, fixed before any new feature work, per the user's
explicit ordering decision. Two of them required live protocol verification against the real
`claude.exe` (v2.1.246) before implementation; both are documented below with the actual observed
wire traffic, not just research.

## Findings and fixes

- **Invalid `--permission-mode default`.** The real CLI enum (confirmed via live `--help`) is
  `acceptEdits | auto | bypassPermissions | manual | dontAsk | plan` - there is no `default` value,
  but the chat popup and Options page both offered/could send exactly that string.
  `PermissionModeOption.Value` became nullable (mirroring the existing `ModelOption`/
  `ThinkingLevelOption` pattern for "omit the flag, let the CLI use its own default"); the popup's
  list now has a real "CLI Default" (omits the flag), "Manual" (was mislabeled "Default"), and a
  new "Don't Ask" entry; `ClaudeCodeSession.Start` now only appends `--permission-mode` when a
  value is actually selected; the Options page's dropdown and description text were corrected to
  match. The extension's own startup default ("Accept Edits") is now selected by value rather than
  array index so future reordering can't silently change it.
- **Stop killed the process instead of interrupting it.** Live-verified: sent a real
  `claude.exe` (same flags this extension uses) a long-streaming prompt, then wrote
  `{"type":"control_request","request_id":"<uuid>","request":{"subtype":"interrupt"}}` to its
  stdin mid-stream. Observed: the CLI aborted the in-flight turn (`"aborted":true` on the assistant
  snapshot, `"terminal_reason":"aborted_streaming"` on the result), replied with a correlated
  `control_response` (`{"subtype":"success","request_id":"...","response":{"still_queued":[]}}}`),
  and - critically - **the process stayed alive** and accepted a normal follow-up user turn
  immediately after, with no `--resume` needed. Implemented `ClaudeCodeSession.SendInterruptAsync`
  (writes the interrupt request, correlates the reply via a `request_id`-keyed
  `TaskCompletionSource` dictionary) and a new `control_response` case in the protocol parser
  (previously silently dropped - `ClaudeMessage.Parse` fell to its `default: return null` for that
  type). `ChatSessionViewModel.StopSession()` (now `StopSessionAsync`) tries the interrupt first and
  only falls back to the old kill-the-process path if the session isn't running or no
  `control_response` arrives within 5s.
- **Resumed sessions didn't restore the visible transcript.** Live-verified, in two parts: (1)
  started a session, got one reply, captured its `session_id`; (2) started a **second** process
  with `--resume <that id>` and watched its stdout for 5 seconds with **no input sent** - only
  `init`/`status` arrived, confirming `--resume` does **not** replay prior turns over the
  stream-json wire (the CLI recovers conversation state server-side - the resumed process
  correctly answered "what did you just say" from cache-read context alone - but the client gets
  nothing to rebuild the UI from). This ruled out the roadmap's preferred option (reusing the live
  wire's snapshot parser) and confirmed the fallback was actually required: reading the CLI's own
  on-disk transcript at `~/.claude/projects/<cwd-hash>/<session-id>.jsonl`. Also live-confirmed the
  exact cwd-to-folder-name hash by cross-referencing several real `~/.claude/projects/*` folder
  names against their source paths: `:`, `\`, `/`, `_`, and ` ` are each replaced 1-for-1 with `-`
  (no collapsing, case preserved) - e.g. `Teron_Extensions` -> `Teron-Extensions`, `AddOn Projects`
  -> `AddOn-Projects`. Added `ViewModels/TranscriptReplay.cs`: a tolerant, read-only parser
  (different envelope schema from the live wire - full per-turn snapshots, no incremental deltas,
  extra fields, occasional `queue-operation`/`attachment`/sidechain lines to skip) that reuses the
  live parser's `ExtractText` helper (promoted from `private` to `internal` for reuse) and groups
  transcript lines back into per-turn chat bubbles matching live behavior - a tool call and its
  follow-up text response are separate "assistant" transcript lines but belong in one bubble, the
  same way live streaming keeps them together until the turn's `result` arrives.
  `ChatSessionViewModel.ResumeSessionEntry` now hydrates `Messages` from this before starting the
  session, wrapped in try/catch so a hydration failure can never block actually resuming.
- **`MessageStopEvent` dead branch.** Parsed but never handled in `ClaudeCodeSession.HandleLine`.
  Investigation found this matches an existing, already-shipped precedent -
  `ContentBlockStopEvent`/`BlockStopped` is fully wired as a raised .NET event but never subscribed
  to by `ChatSessionViewModel`, because nothing in the current design needs a
  finalization/`IsStreaming` signal (text re-renders live off deltas). Added an explicit,
  intentionally-empty `case MessageStopEvent:` with a comment recording this reasoning, so it isn't
  reopened as a mystery later.
- **Dead `SendOnCtrlEnter` setting.** Defined on the Options page, never read anywhere.
  `ClaudeCodeChatControl` now caches it in `OnLoaded` and branches the Enter-key handler on it.
- **Missing `xhigh` effort level.** `claude --help` lists `low/medium/high/xhigh/max`; the UI
  stopped at `max`. Added to both the chat popup's list and the Options page's converter.

## Verification

`dotnet build TeronClaudeCodeVS.csproj` - 0 warnings, 0 errors after every change in this phase.

Live-verified (not just research-verified, per this project's own standing practice):
- Interrupt: real `claude.exe` process, exact invocation flags this extension uses, confirmed
  process survival + correct `control_response` + working follow-up turn (see script output,
  not reproduced here).
- Transcript replay: loaded the real, just-produced `.jsonl` transcript for two live test
  sessions (one plain text exchange, one with a real `Read` tool call) through the actual built
  `TeronClaudeCodeVS.dll` (via PowerShell `Add-Type` + reflection, not a reimplementation) and
  confirmed correct message count, role ordering, and - for the tool-call case - correct grouping
  of the tool call + its follow-up text into a single assistant bubble with the tool's real output
  attached.
- Permission-mode, `SendOnCtrlEnter`, and `xhigh` changes are self-contained UI/argument-building
  logic with no protocol dependency - covered by the clean build; live smoke-testing in the VS
  experimental instance (F5) is still recommended before a release, but not required to trust the
  logic itself.
