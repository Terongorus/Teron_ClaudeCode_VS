# Phase 10 - Terminal Hand-off and Injected Commands (GAP-1..GAP-3)

**Date:** 2026-08-29

Fourth phase of the baseline-parity implementation (continues [Phase 9](Phase%209%20-%20High-Value%20UX%20Polish%20%28UX-1..UX-12%29.md)).
Implements GAP-1..GAP-3 from `docs/comparison-audit/implementation-backlog.md`.

## GAP-3 - are /btw, /feedback, /remote-control CLI-provided or extension-injected?

Measured against the shipped CLI (v2.1.251) in the same `-p --input-format stream-json` mode this
extension uses: its `init` event lists 50 slash commands, and none of these three is among them.
They are injected by the official extension, so all three had to be built here too.

None of them turned out to be proprietary to VS Code. Each is backed by a control-request subtype
the CLI itself dispatches - `side_question`, `submit_feedback`, `remote_control` - on the same
channel this extension already speaks for interrupts and permission responses.
`SendInterruptAsync` was generalised into `SendControlRequestAsync`, and the three commands are
thin callers on top.

`/feedback` and `/remote-control` are gated behind a confirmation card - the first uploads this
session's transcript to Anthropic, the second publishes the session to claude.ai/code, and both
leave the machine, so neither fires on the command alone. Turning Remote Control **off** is not
gated - that direction only reduces exposure. This is deliberately more conservative than
baseline, which toggles both at once.

## GAP-1 - terminal hand-off cards

Five in-chat hand-off cards (Memory, Agents, Hooks, Output styles, Permissions) with baseline's
`1 Continue in Terminal` / `2 Never mind` wording, lifted verbatim from the W30 table in baseline's
webview bundle rather than paraphrased - these are promises about how configuration propagates
back to the IDE, and a reworded promise is a different promise. Baseline skips its own sixth entry
(plugins) when building this menu because plugins get a real GUI panel (built later, Phase 13);
this extension skips it identically.

## GAP-2 - "Open Claude in Terminal"

An honest divergence. Visual Studio exposes no scriptable integrated terminal - not on DTE, no SDK
service, and `View.Terminal` only opens the window - so `TerminalLauncher` opens Windows Terminal
externally, in the solution directory, falling back to a console host. Same CLI, different frame;
documented here, not papered over.

## Verification

Verified live in the VS 18 Experimental instance: 21/21 structural checks in the new
`phase-d-verify.ps1`, plus a driven session. The terminal launch was checked against the real
process table (`wt.exe` with the right `-d` and a genuine child `claude.exe` running `/hooks`),
`/btw` returned and rendered a real model answer, and both confirmation cards were shown and
declined so nothing was uploaded and the bridge was never enabled.

Also adds `Get-DocumentTexts` to `uia-lib.ps1`, correcting an assumption Phase 9's scripts were
built on: enumerating UIA `Name`s is structurally blind to markdown content, since `FlowDocument`s
expose their text only through `TextPattern` and carry an empty `Name`. A `Name`-only sweep can
report that a card rendered while being unable to see whether it rendered anything inside it.

**Files:** `Core/ClaudeCodeChatControl.xaml(.cs)`, `ViewModels/ContentBlocks.cs`,
`docs/comparison-audit/scripts/phase-d-verify.ps1` (new), `uia-lib.ps1` (`Get-DocumentTexts`)

Commit: `294e10a` "Phase D: terminal hand-off and the injected commands (GAP-1..GAP-3)"
