# Phase 17 - TEST-1 and the xUnit Test Port

**Date:** 2026-09-01

Eleventh and final phase of the baseline-parity implementation (continues [Phase 16](Phase%2016%20-%20Voice%20Dictation%20and%20Running-Cloud%20Sessions%20%28FEAT-8%2C%20FEAT-9%29.md)).
Closes TEST-1 from `docs/comparison-audit/implementation-backlog.md`, and separately ports the
headless `phase-*-unit.ps1`/`phase-*-vm.ps1` PowerShell suites from Phases 11-16 into a real xUnit
test project - a mid-phase request from the user ("if it's possible to include the non-automated
(headless only, not devenv live tests) as actual xUnit tests"), scoped explicitly to headless
suites only; the live `phase-*-live.ps1`/`*-verify.ps1` scripts stay in PowerShell.

TEST-2 (IDE MCP tools beyond `getDiagnostics`, live-driven) and TEST-3 (a real-hover helper folded
into `cdp-lib.ps1`) remain open test-debt, not blocking - tracked for Phase L.

## TEST-1 - driving paste/drag-drop on our own WPF side

Done, but not the way the backlog originally proposed. Measured first: an out-of-process automation
attempt (a `WM_DROPFILES`-style drop driven from a separate process, the clipboard-format
equivalent of the existing `Send-WmChar`/`Send-WmClick` helpers) hits a real access violation. WPF's
registered `IDropTarget` on a window is a raw in-process COM pointer - dereferencing it from another
process is `0xC0000005`, not merely hard to drive. A negative control (a plain, unrelated window)
also had a drop target property, because WPF registers one on every `HwndSource` regardless of
`AllowDrop` - so "a drop target exists on that HWND" proves nothing about the composer specifically.

So the in-process route isn't a fallback here, it's the **only correct one** - and it's exactly
what the xUnit port needed anyway. `AttachmentTests` (13 tests) drives the real
`ClaudeCodeChatControl` in-process: real `DataObject`/`DragEventArgs` through the real routed
events, real PNG/PDF/text files on disk, asserted against the chips the real data templates render.
Covers rows A1-A3 of the backlog's "needs a human" checklist.

## The xUnit conversion

Phases 11-16's `phase-*-unit.ps1`/`phase-*-vm.ps1` scripts (2,284 lines, 378 `Check` calls) become
one xUnit `Fact`/`Theory` per original check, against the same real transcripts/fixtures/CLI calls -
nothing re-implemented in C# just to be tested against itself:

| PowerShell source | xUnit test class |
|---|---|
| `phase-e-unit.ps1` | `DiffTabTests` |
| `phase-f-unit.ps1`, `phase-f-vm.ps1` | `SessionTitleTests`, `SessionTitleRefreshTests` |
| `phase-g-unit.ps1`, `phase-g-vm.ps1` | `McpAndPluginsTests`, `McpAndPluginsCliTests` |
| `phase-h-unit.ps1` | `WebContextAndFallbackTests` |
| `phase-i-unit.ps1` | `RewindTests` |
| `phase-j-unit.ps1` | `VoiceAndSessionsTests` |

`InternalsVisibleTo` grants the test assembly access to the internal seams the PowerShell harnesses
reached by reflection (`VoiceInput`, `AgentSessionsViewModel.Parse`, etc.) without making them
public API. A few genuinely private members (`VsDiffTab.ApplyForward` and neighbours) stay
reflection-only via a small `Reflect` helper rather than being widened just to be testable.

### Two real bugs the port itself surfaced (fixed in the tests, no product code changed)

- **SAPI's recognition engine is COM-affine to its owning STA thread.** A raw blocking wait on that
  thread (`ManualResetEventSlim.Wait`, `Thread.Sleep`) never pumps the thread's message queue, so
  no callback can ever be delivered - this is exactly what the PowerShell original's "must run on
  an STA thread" comment was warning about. An early, un-timed-out version of the dictation test
  hung the whole test run for **twelve real minutes** before a timeout even existed to catch it.
  Diagnosed by an isolated standalone PowerShell probe (confirming recognition actually completes
  in ~230ms outside the trap), then fixed with an active-pump wait (`Sta.PumpUntil`/`Sta.Pump`).
- **The silence CONTROL fed the recognizer a literal empty `PromptBuilder`**, which writes a
  zero-byte file with no WAV header at all - correctly rejected by SAPI as invalid, which is not
  the same test as "hears nothing in real silence." Fixed to synthesize an explicit 2-second silent
  break, producing real well-formed PCM silence.

Also fixed: one test had asserted against an invented placeholder folder path instead of the value
actually baked into the fixture JSON.

## Verification

Full suite: **182/182 passing**, ~25-29 seconds. A clean `MSBuild /t:Rebuild` of the extension
itself: 0 warnings, 0 errors.

**Files:** `tests/TeronClaudeCodeVS.Tests/` (new project - `Infrastructure/` and `Phases/`, 19
files), `TeronClaudeCodeVS.csproj` (`tests\**` exclusion + `InternalsVisibleTo`)

Commit: `a484843` "Phase K: TEST-1, and the headless suites as real xUnit tests (182 tests)"
