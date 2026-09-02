# Phase 20 - Post-Fix Live Re-Verification

**Date:** 2026-09-02

A scoped, visual-first re-comparison against the real "Claude Code for VS Code" extension,
requested specifically to (a) confirm the three bugs fixed earlier this session (`b79ff9e`,
`86caecd`) actually look and behave correctly when driven live, not just build-succeeded, and
(b) spot-check whether anything Phase 6-19 documented has visibly drifted since. Unlike Phase 6,
this pass did not need a fresh isolated VS Code instance - the baseline extension's own
screenshots from `docs/comparison-audit/screenshots/real-extension/` were reused throughout, since
nothing in this pass gave reason to think the baseline extension's UI has changed since 2026-08-28.

## Environment

The `Exp` instance already running from this session's earlier work (PID 13456, titled
`TestProjectClaude - ... - Microsoft Visual Studio - Experimental Instance`) was reused as
instructed rather than redeployed. Driven entirely through the existing
`docs/comparison-audit/scripts/` toolkit (`uia-lib.ps1`, `screenshot-composite.ps1`), background-safe
throughout - no `SetForegroundWindow`, no physical mouse or keyboard input. New screenshots live
under `docs/comparison-audit/screenshots/our-extension/phase-20/`.

## Quota check, done first

Per the brief, current usage was checked (Account & Usage popup, `OnAccountUsageClicked`) before
sending anything. It read **Session (5hr) 12%, Weekly (7 day) 2%** - a world away from Phase 6's own
89-91%, since the weekly window has reset since 2026-08-28. Given that much headroom, the pass was
not restricted to visual-only checks; a handful of real messages were sent where a live behavioural
check (rather than a screenshot of an existing transcript) was the stronger evidence, particularly
for the permission/choice-card keyboard fix. Usage at the end of the pass was **Session 20%, Weekly
2%** (`docs/comparison-audit/screenshots/our-extension/phase-20/P20-01-account-usage.png` for the
starting reading). Four short messages were sent in total; none approached any limit.

## The three bugs, checked live

### White tool-call output background (`b79ff9e`) - confirmed fixed

Every screenshot taken this pass that shows a tool-call's output or thinking block
(`P20-01-account-usage.png`, `P20-06-permission-card.png`, `P20-07-edit-permission-card.png`,
`P20-09-check-state.png`) renders it with the dark chat-panel background, not the stark white
`FlowDocumentScrollViewer` chrome the bug described. The `Background="Transparent"` added to
`Controls/MarkdownViewer.xaml`'s `Viewer` is doing its job under real rendering, not just compiling.

### Mic can't be stopped by a second click (`b79ff9e`) - source-verified, not live-driven

The diff was re-read directly: `OnMicButtonDown` now returns early when `_vm.IsDictating` is
already true, *before* setting `_micGestureHandled`, so the second tap's `Click` is no longer
swallowed and reaches `OnMicClicked`'s toggle-to-stop branch. This is the exact fix the bug
required and it reads correctly. It was **not** exercised with a real microphone this pass -
consistent with this project's own standing rule (Phase 19's C1-C4) that voice input needs genuine
physical/audio input a background-safe UIA harness cannot provide. That boundary hasn't moved;
this fix stays source-verified rather than live-confirmed, same as it would have been in any
earlier phase.

### Permission/choice-card keyboard shortcuts silently doing nothing (`86caecd`) - confirmed fixed, live

This got the most thorough live check of the three, because it's cleanly testable without
physical input and because the first attempt at testing it surfaced a real technique gap worth
recording.

**The fix itself, live-confirmed twice.** `86caecd` added two lines to
`ChatSessionViewModel`'s `PropertyChanged` handler in `ClaudeCodeChatControl.xaml.cs`: whenever
`PendingPermissionRequest` or `PendingChoiceCard` transitions to non-null, `InputBox.Focus()` is
called. To test the actual bug precondition - keyboard focus sitting on a button from a *previous*
card's mouse click, not on `InputBox`, when a *new* card appears - this pass:

1. Clicked `HistoryButton` then its close button, leaving real keyboard focus on `NewSessionButton`
   (confirmed via `AutomationElement.FocusedElement`).
2. Opened the palette and clicked the "Agents" terminal hand-off row, which raises a fresh
   `PendingChoiceCard` - the same code path `86caecd` touches, and free of quota cost (it's a local
   UI card, not a CLI turn).
3. Checked `InputBox.HasKeyboardFocus` immediately, with no further action: **`True`**, and
   `AutomationElement.FocusedElement.AutomationId` read `InputBox`.

The same result held for `PendingPermissionRequest` earlier in the pass: sending an edit request
under Manual permission mode raised a real "Allow Edit file?" card
(`P20-07-edit-permission-card.png`), matching UX-3's numbered `1 Allow / 2 Allow for Session /
3 Deny` layout with the inline "Tell Claude what to do instead" redirect and the native VS diff
tab auto-opened per FEAT-2 - no regression there either.

**A real technique gap in the project's own toolkit, found and worked around.** The first attempt
to answer a card with a real keystroke used this toolkit's existing `Send-WmChar` helper (`WM_CHAR`
only), the same helper Phase 6 built for `@`-mention testing. It typed a literal `"2"` into the
composer instead of triggering the shortcut. Root cause, confirmed by re-reading
`OnInputPreviewKeyDown`: the 1/2/3 handling lives on `PreviewKeyDown` (`e.Key`), which fires from
`WM_KEYDOWN` - a different Win32 message than `WM_CHAR`. `Send-WmChar` is correct for the
`@`-mention case (that logic is `TextChanged`-driven, character-level) but does not exercise a
`KeyDown`-gated shortcut at all. Switching to a direct `WM_KEYDOWN`/`WM_KEYUP` pair for `VK_2`
(`0x32`) worked correctly: the Agents card resolved to "Never mind." and `InputBox.Value` stayed
empty - no stray digit landed in the composer. This is a gap in the *harness*, not the product
(worth adding a `Send-WmKeyDown` helper alongside `Send-WmChar`/`Send-WmClick` in a future pass);
it is called out here rather than silently worked around because an initial, wrong read of that
first failed attempt would have looked exactly like the original bug recurring.

**A screenshot staleness artifact, also worth recording plainly.** After the keyboard fix was
confirmed via UIA text and property reads, `screenshot-composite.ps1` was called four more times to
get a clean picture of the resolved state. All four (`P20-10` through the now-deleted `P20-13`)
came back byte-identical to each other despite real, confirmed state changes in between (a CLI turn
completing, a card resolving, `InputBox` being typed into and cleared). This is the same
`PrintWindow`/DWM class of issue `screenshot-toolwindow.ps1`'s own header already documents, but
this is the first time it's been caught returning frozen pixels across *multiple* genuinely
different live states rather than one occluded capture. `P20-10-keyboard-shortcut-resolved.png` is
kept as a labelled example of the artifact, not as evidence of the resolved state - the UIA text
dump (`"Never mind."` resolution label present for both the Memory and Agents cards, `InputBox.Value
== ""`) is the real evidence, consistent with this project's own standing preference for a string
assertion over a screenshot when the two disagree.

## A new, previously-undocumented finding: native "Inconsistent Line Endings" dialogs on the diff tab

Not one of the three named bugs, but real and reproducible. Sending an edit request under Manual
permission mode reliably opened **two** native Win32 "Inconsistent Line Endings" dialogs (class
`#32770`, one per temp file VS's own diff view compares) asking whether to normalize line endings
in `...TeronClaudeCodeVS-difftab\<id>\Class1.after.cs` (and its `.before.cs` sibling). These are
genuine OS-level modal dialogs, not part of our WPF tree - `EnumWindows` found them as separate
titled top-level windows, which is also why `screenshot-composite.ps1` never rendered them (it
deliberately skips titled windows, since WPF popups are titleless and that's what it's built to
find). Being real modal dialogs, they correctly steal keyboard focus away from `InputBox` - nothing
in `86caecd`'s fix could or should prevent that, since a native modal dialog taking focus is
expected Windows behaviour. The actual gap: once dismissed, focus is **not** handed back to
`InputBox`, so the 1/2/3 permission shortcuts go quiet until the user clicks the composer again.
Both dialogs were dismissed with "No" (don't normalize) via a direct `BM_CLICK` to each button's
native HWND, touching only the temp diff-view files, not the real `Class1.cs`. Logged as a caveat
on FEAT-2's row in `implementation-backlog.md` rather than as a new numbered gap, since the feature
itself (the diff tab) still works correctly - this is a friction point in one specific input path
into it.

## Other areas checked (free of quota cost, reusing the existing transcript/panels)

- **Empty state** (`P20-02-empty-state.png`): wordmark, "Ask about this solution, or describe a
  change to make.", `@ to attach a file · / for a command` hint. Matches Phase 8/9's designed empty
  state (UX-11); no drift from baseline's own empty-state surface (`real:20`) in spirit - baseline's
  mascot/tip-of-the-day are still a deliberate, undocumented-as-a-gap difference, not new.
- **Terminal hand-off cards** (`P20-03-terminal-handoff-card.png`, Memory; a second live pass on
  Agents mid-keyboard-test): wording is still verbatim baseline's own ("Continue in Terminal to
  edit memory? Once configured, memories will be picked up by Claude Code here in your IDE." /
  `claude /memory` / `1 Continue in Terminal` / `2 Never mind` / `1/2 to choose`), matching
  `real:13` exactly. Both were dismissed via "Never mind" - no real terminal was launched.
- **MCP servers / Manage plugins panels** (`P20-04-mcp-panel.png`, `P20-05-plugins-panel.png`): no
  change since Phase L - MCP still correctly reports "No MCP servers configured. Use `claude mcp
  add` to add a server."; Plugins still shows the real `claude-plugins-official` marketplace with
  scrolling, real plugin descriptions on the Plugins tab.
- **Composer/type-scale in general**: visible in every screenshot this pass - still the two-tier
  `FontSizeBody`/`FontSizeChrome` scale from ST-2, still `RadiusControl`/`RadiusCard` from ST-3,
  terracotta still confined to the send button and the (deliberately kept, ST-5) user-message
  bubble. No regression toward the pre-Phase-8 nine-size/eight-radius state.
- **Permission-mode / Bash allow-listing, a non-bug worth noting so it isn't mistaken for one**:
  with the mode chip set to Manual, two different `Bash` commands (`dir`, then an `echo`) both ran
  without a permission card at all. This is because this exact scratch project
  (`TestProjectClaude`) has accumulated a CLI-side Bash allow-list from many earlier live-testing
  sessions - Edit-tool calls in the same session correctly still prompted every time. Not an
  extension bug; recorded so a future pass doesn't misread "Manual mode let a Bash command through"
  as a permission-mode regression.

## What was not tested, and why

- Voice dictation end-to-end (real microphone, real `Ctrl+D`, the hold gesture) - Category C from
  Phase 19's checklist, unchanged: needs genuine physical/audio input a background-safe harness
  cannot provide.
- The narrow-dock `MinWidth` fix - explicitly flagged in this task's brief as already known not to
  actually stop VS's splitter live; not re-litigated here.
- A fresh isolated VS Code + real-extension instance - not stood up, since nothing this pass found
  gave reason to doubt the baseline extension's UI has changed since Phase 6's 2026-08-28 capture,
  and the brief's own preference was to reuse existing baseline screenshots when possible.

## Verification

Live, against the real, already-running `0.4.0`-plus build in the `Exp` instance (PID 13456) that
included both `b79ff9e` and `86caecd`. Ten screenshots plus one UIA text dump saved under
`docs/comparison-audit/screenshots/our-extension/phase-20/`. State was left clean: the Manual
permission-mode switch made mid-pass was reverted back to Auto, the `InputBox` was cleared, all
opened popups/panels were closed, and the one edit request sent during testing was denied (not
applied) so `TestProjectClaude/Class1.cs` is unmodified. The Exp instance itself was left running,
per the task's instruction not to close anything the main session might still be using.
