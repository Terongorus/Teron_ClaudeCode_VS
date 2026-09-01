# Phase 14 - Add Menu and Automatic Model Fallback (FEAT-6, FEAT-7)

**Date:** 2026-08-30

Eighth phase of the baseline-parity implementation (continues [Phase 13](Phase%2013%20-%20MCP%20Servers%20and%20Plugins%20Panels%20%28FEAT-4%2C%20FEAT-5%29.md)).
Implements FEAT-6 and FEAT-7.

## FEAT-6 - the `+` add menu

Three entries on the input area, read out of baseline's webview bundle rather than inferred:
**Upload from computer**, **Add context**, and **Browse the web**, with baseline's own tooltips.

- *Upload from computer* opens a file dialog into the existing attachment-staging path. That path
  now reports an unsupported file instead of skipping it silently - silence is right for a drop of
  twenty mixed files and wrong for one someone picked by hand.
- *Add context* inserts `@` and hands over to the mention picker, exactly what baseline's entry
  does. `UpdateInputPickers` was split out of the `TextChanged` handler because a programmatic
  insert sets `Text` and `CaretIndex` separately, so the picker has to be asked again once both
  have landed.
- *Browse the web* is a real, documented divergence. Baseline's entry inserts `@browser:`, whose
  expander calls the VS Code extension's own `ensureChromeMcpEnabled()`/`createNewBrowserTab()` -
  the Claude-in-Chrome integration, gated on `authMethod === claudeai`, not a CLI feature reachable
  by any flag. The entry keeps its label and delivers the same outcome through WebFetch/WebSearch
  instead: a URL becomes a fetch instruction, anything else a search. `WebContextComposer` records
  why in a comment, so the divergence is not "fixed" back later by someone who didn't know it was
  deliberate.

## FEAT-7 - automatic model fallback

`--fallback-model` wired through `ClaudeSessionStartOptions`, an off-by-default toggle plus a
target model on the options page, and an in-transcript notice.

The audit had attributed baseline's observed `Switched to claude-haiku-4-5-20251001` line to its
`switchModelsOnFlag` toggle. The binary says otherwise: that setting covers safeguard refusals,
while the line actually seen near a weekly usage limit matches the `model_consent_fallback`
subtype, whose own wording names usage credits. This phase handles all four subtypes the CLI
emits - `model_fallback`, `model_refusal_fallback`, `model_consent_fallback`,
`model_refusal_no_fallback` - surfacing each one's own finished sentence. All four are shown
regardless of our own setting, because the refusal path is governed by a CLI-side setting that is
on by default and is not ours to turn off. The model chip is deliberately left alone: assigning
`SelectedModel` restarts the session, and a `model_fallback` switch lasts one turn.

## Verification

140 checks, 54 of them against a live experimental instance:

- `phase-h-unit.ps1` (86) — the composer and all four subtypes, against fixtures built from the
  CLI binary's own schemas and message builders.
- `phase-h-live.ps1` (36) — a real IDE. Also carries the live pass Phase 13 never got: its MCP and
  Plugins panels are driven here for the first time.
- `phase-h-live-fallback.ps1` (18) — FEAT-7 end to end. The flag is absent from the real spawned
  `claude.exe` with the toggle off and present with it on, and the CLI accepts it - proven by the
  session reaching Ready, which only happens after it parses its flags. No prompt is sent, so no
  quota is used.

Two harness defects were found and fixed along the way: a Win32 common dialog raised by a VS
extension is an owned window, so it is a UIA *Descendant* of the desktop and never a *Child* of
it; and a running session is evidence about the setting as it was **when that session started**,
not as it is now.

**Not covered, and said plainly:** no `model_fallback` event has been seen arriving from a live
CLI, since producing one needs a real overload, refusal, or credit boundary. Build is clean - 0
warnings, 0 errors.

**Files:** `Core/ClaudeCodeSession.cs`, `Core/ClaudeCodeOptionsPage.cs`,
`Core/ClaudeCodeChatControl.xaml(.cs)`, `ViewModels/WebContextComposer.cs` (new),
`Protocol/ClaudeStreamEvents.cs` (`ModelFallbackEvent`),
`docs/comparison-audit/scripts/phase-h-unit.ps1`, `phase-h-live.ps1`, `phase-h-live-fallback.ps1` (new)

Commit: `c59e360` "Phase H: add menu and automatic model fallback (FEAT-6, FEAT-7)"
