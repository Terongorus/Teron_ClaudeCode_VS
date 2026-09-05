# Phase 23 - Context-Window Usage Indicator

Requested live by Kaloyan from a screenshot of the official VS Code extension: a small badge
showing how much of the context window is used, which can be clicked to compact. Implemented to
match that reference's exact formula and thresholds, confirmed by reading its installed source
rather than guessing - and deliberately improves on one confirmed bug in that reference rather than
reproducing it.

## Research first, then implementation

A background agent traced `webview/index.js`/`extension.js` in the installed
`anthropic.claude-code-2.1.261-win32-x64` extension (same technique as the Phase 22 "Archive
session" investigation - both bundles ship readable-enough minified JS on disk). Confirmed:

- **`usedTokens`** is the most recent **top-level** (`parent_tool_use_id` absent - never a Task-tool
  sub-agent's own turn) assistant API round's `usage.input_tokens + cache_creation_input_tokens +
  cache_read_input_tokens + output_tokens`. It **overwrites** on every round rather than
  accumulating - this is "how much context the next request will carry," not a session total.
- **The denominator** is `contextWindow - maxOutputTokens - 13000` (a hardcoded safety buffer),
  both sourced from the `result` message's own `modelUsage[<model>]` field - a field this codebase
  had never parsed before this phase.
- **The badge stays hidden until ≥50% used** - a display threshold, not a distinct auto-compact
  trigger (no separate 80%/92% constant exists in the reference).
- **The compact button has no dedicated request type and no busy-state gating at all** - it sends
  literal `"/compact"` through the exact same code path as any typed message, whether idle or
  busy. The confusing "I'm an agent, I can't run CLI commands" reply Kaloyan described happens
  entirely inside the external `claude` CLI's own stdin-queue handling once a message sent mid-turn
  is eventually dequeued - not a fixable client bug in the reference, and not something achievable
  differently from this extension either, since no alternate request type exists for it.

Full agent findings (byte offsets, code fragments) are in this session's transcript, not
reproduced verbatim here per [[no-codenames-in-public-output]]-style hygiene for internal tool
output - the summary above is what's load-bearing.

## What this extension does differently, and why

Given baseline has no protection against the busy-state bug, reproducing its "always send, never
gate" behavior would import the same confusing failure mode. Instead, **`ContextIndicatorButton` is
disabled (not hidden) while `IsBusy`**, with its tooltip explaining why
(`ContextButtonTooltip` on `ChatSessionViewModel`). This is a deliberate improvement, not parity -
worth remembering if a future baseline comparison flags this as a "difference" rather than a fix.

## Implementation

- `Protocol/ClaudeStreamEvents.cs`: `AssistantSnapshotEvent` gained `IsTopLevel` and four nullable
  usage fields; `ResultMessage` gained `ModelUsage` (`IReadOnlyDictionary<string, ModelUsageInfo>`,
  a new `ModelUsageInfo{ContextWindow,MaxOutputTokens}` type), parsed from the `result` message's
  own `modelUsage` object.
- `ChatSessionViewModel.cs`: `OnAssistantSnapshot` updates `_currentContextTokens` from top-level
  usage; `OnTurnCompleted` updates `_contextWindowSize`/`_maxOutputTokensSize` from
  `result.ModelUsage[SelectedModel.Value]` (falls back to the previous value if the current
  model's entry is absent, matching baseline). `ContextPercentUsed`, `IsContextIndicatorVisible`,
  `ContextIndicatorText`, `ContextButtonTooltip`, `CanCompact`, and `CompactAsync()` are the
  public surface; `IsBusy`'s setter now also notifies `CanCompact`/`ContextButtonTooltip`.
- `ClaudeCodeOptionsPage.cs`: new **Context Indicator** category, **Show Threshold (%)** setting
  (`ContextIndicatorThresholdPercent`, default 50) - requested live by Kaloyan as a follow-up to
  the same message, so the hidden-until-50% behavior isn't hardcoded the way baseline's is.
- `ClaudeCodeChatControl.xaml`/`.xaml.cs`: new chip button next to Model/Permission/Effort, a new
  `ContextPercentToBrushConverter` in `Controls/Converters.cs` (amber below 90% used, red at or
  above - the same two colors `ToolStatusToBrushConverter`/`McpStatusToBrushConverter` already use
  for "needs attention" vs. "broken", so the meaning reads consistently everywhere in the panel).

## Verification

Build clean, xUnit suite 181/182 (same pre-existing, unrelated failure - see below for a real
incident during this phase's own verification, not a regression).

**The math was verified end-to-end, not just reasoned through**: a standalone PowerShell probe
loaded the real compiled assembly, fabricated `AssistantSnapshotEvent`/`ResultMessage` objects via
reflection (`New-Object` results need `.psobject.BaseObject` unwrapping before a reflection
`MethodInfo.Invoke` will accept them as strongly-typed arguments - a real PowerShell/.NET interop
gotcha worth not rediscovering), and confirmed against hand-computed expected percentages:
45.81% used (correctly hidden below the 50% default threshold), 59.22% used (correctly visible),
and confirmed raising the threshold to 70% correctly re-hides the indicator at 59.22%.

**A real incident happened during that verification, caught and fixed the same session**: the
probe invoked the real `OnTurnCompleted` via reflection with a fabricated `ResultMessage
{ SessionId = "probe-session" }`, which - exactly as production code is supposed to - persisted a
new row to the *real* `%AppData%\TeronClaudeCodeVS\sessions.json` on this machine, since nothing
redirected the store to a sandbox first (unlike `SessionTitleRefreshTests.cs`'s own
`HistorySandbox`, which exists precisely to prevent this). Caught only because it broke an
unrelated pre-existing test (`SessionTitleTests.The_real_history_file_on_this_machine_still_round_trips`,
which asserts every real row has a working directory - the probe's fake row had none). Fixed by
manually removing the one contaminating row from the real file, confirmed against a `Read` of the
file's contents before and after so only that row was touched. **Lesson for next time this kind of
reflection-driven ViewModel probe is needed**: redirect `SessionHistoryStore`'s static path fields
via reflection first, exactly as the existing test's `HistorySandbox` already does, rather than
letting a throwaway probe run right through the same code path that writes real user data.

## Not yet live-verified

The actual on-screen appearance (badge placement, color, VS theme interaction) and the real CLI's
behavior when `/compact` is sent while busy in *this* extension specifically (to confirm the
disabled-button design actually avoids the bug rather than just being a defensible guess) both need
a real F5 pass.
