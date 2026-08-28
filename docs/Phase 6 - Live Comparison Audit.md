# Phase 6 - Live Comparison Audit

**Date:** 2026-08-28

A full live, automated, side-by-side comparison between the real "Claude Code for VS Code"
extension (`anthropic.claude-code-2.1.250-win32-x64`) and this extension, run against the actual
running apps rather than source-reading alone. Requested explicitly to establish which features
are genuinely working and verified, which are implemented-but-unverified, and which are still
missing - covering functional correctness, UI/visual style, and stability, not just a pass/fail
checklist.

## Methodology

No existing driver fits a VS extension tool window (native WPF hosted in `devenv.exe`) or a VS
Code webview (Electron/Chromium) - neither is a CLI, server, TUI, or browser page a standard
Playwright/xvfb setup can drive. Two purpose-built, from-scratch automation toolkits were used
instead, both **background-safe** (no stolen focus, no physical mouse/keyboard hijacking, safe to
run underneath whatever else the user is doing on the machine):

- **Our extension**: Windows UI Automation (`System.Windows.Automation` via PowerShell). No
  `AutomationId`s existed anywhere in the XAML beforehand - added to the ~20 top-level interactive
  controls (`InputBox`, `SendButton`, `StopButton`, the four split popup buttons,
  `TranscriptModeButton`, history/settings/raw-toggle buttons, etc.) as prep work. Clicks/toggles
  go through `InvokePattern`/`TogglePattern`/`SelectionItemPattern`/`LegacyIAccessiblePattern.
  DoDefaultAction()` - purely programmatic, no `SendInput`/`mouse_event` simulation anywhere.
- **Real extension**: Chrome DevTools Protocol over a raw `System.Net.WebSockets.ClientWebSocket`
  (no Node/Playwright install needed). VS Code webviews are double-nested - an outer wrapper frame
  (VS Code's own preload/sandbox script) contains an inner `active-frame` child with the
  extension's actual Preact app - reached via `Page.createIsolatedWorld` scoped to that child
  frame's `frameId`, then `Runtime.evaluate` with the resulting `contextId`. Plain
  `Runtime.evaluate` on the outer frame only ever sees the wrapper script, never the real UI.
- **Screenshots**: `PrintWindow` (`PW_RENDERFULLCONTENT`), not `CopyFromScreen` - captures a
  window's real contents regardless of whether it's foreground, occluded, or behind other windows.
- **Isolation**: `CLAUDE_CONFIG_DIR` (confirmed empirically to fully redirect the CLI's
  config/session storage) pointed at scratch directories for both test instances, each seeded
  with a copy (not the original) of the real `.credentials.json` so live message tests could
  authenticate without writing test conversations into the user's real `~/.claude` history. VS
  Code launched with its own `--user-data-dir` and `--extensions-dir` pointed read-only-in-effect
  at the real extensions folder, opened only on an empty scratch workspace folder - no real user
  settings, workspaces, or extension state touched.
- **Scope-down**: the shared account's weekly usage was already at 89% (shared because both test
  subjects authenticate as the same real account, by design, to exercise the real CLI). Real
  OS-level drag-and-drop (`IDropTarget`/`DoDragDrop` COM interop) was also out of reach of a
  background-safe driver without physical mouse hijacking. Both constraints cut the original
  ~15-18 item live-test list down to the highest-value items below; the rest are listed as
  still-needing-a-live-pass rather than guessed at.

## Bugs found during the pass (not present before this session)

1. **Stale package-registration cache after a fresh `VSIXInstaller` deploy.** Building with plain
   `dotnet build` does not refresh what the `Exp` hive has registered - it only updates the loose
   files on disk. After deploying a fresh build via `VSIXInstaller.exe /rootSuffix:Exp` (needed to
   pick up the just-added `AutomationId`s), the extension failed to load at all:
   `CreateInstance failed for package [ClaudeCodePackage] ... Could not load file or assembly
   ...\extensions\zrgnvbn2.hp4\TeronClaudeCodeVS.dll ... The system cannot find the file
   specified.` The registration still pointed at the *previous* F5 session's now-deleted extension
   folder hash. Fixed with `devenv /rootsuffix Exp /updateconfiguration` to force a re-scan. Not a
   code bug, but a real gotcha worth documenting: any workflow that mixes F5 debugging and a
   VSIXInstaller-based deploy into the same `Exp` hive will hit this.
2. **(Tooling, not product) PowerShell async/Task interop leak.** The CDP client's first version
   hung indefinitely. Root cause: `Task.GetAwaiter().GetResult()` on a *void*-returning async call
   (`ConnectAsync`), when its result isn't piped to `Out-Null`, leaks a stray `VoidTaskResult`
   sentinel into the function's output stream in Windows PowerShell 5.1 - silently turning
   `$ws = Connect-Cdp ...` into a 2-element array `[VoidTaskResult, ClientWebSocket]` and breaking
   every later `$ws.Method(...)` call with "does not contain a method named ...". Fixed by piping
   every void-returning `GetResult()` call to `Out-Null`. Noted here only because it burned real
   time and is a non-obvious trap for any future PowerShell-based .NET async automation in this
   family of tools.

## Results

Legend: **[LIVE-CONFIRMED]** = actually driven end-to-end this pass with a screenshot;
**[LIVE-CONFIRMED, prior session]** = confirmed in an earlier live pass, not repeated here;
**[STILL UNVERIFIED]** = not exercised this pass (quota/drag-drop constraints); **[NO REAL-EXT
EQUIVALENT]** = novel UI with nothing to compare against.

### Newly live-confirmed this pass

- **Core send/receive round-trip** - both apps: message in, real streamed response out, result
  footer with live cost/token counts. Style note: ours shows `Done · 1.7s · $0.0660 · 10 in / 53
  out tok` as a footer under the response; the real extension shows a lighter `Thought for 0s`
  per-step marker and no persistent per-turn cost/token footer in the transcript itself (cost/
  usage instead lives in the account/usage surfaces on both sides).
- **`AskUserQuestion` multi-select (checkboxes)** - **[LIVE-CONFIRMED]**, previously the single
  biggest unverified gap in the whole Phase 3-5 batch. Real checkboxes with label + description
  text, Submit/Skip buttons, "Submitted 1 answer(s)" resolution text, and Claude's follow-up
  correctly reflecting the multi-selection ("You selected Blue and Yellow."). No visual issues.
- **Transcript view modes (Summary/Normal/Thinking/Verbose)** - **[LIVE-CONFIRMED]**, tested for
  free by re-toggling an existing transcript rather than sending new messages. Verbose mode
  correctly revealed the `AskUserQuestion` tool's raw `Output:` block (hidden by default) and
  auto-expanded a `Thinking` block showing real reasoning text. Re-collapsing back to Normal also
  confirmed working. **[NO REAL-EXT EQUIVALENT]** - this is original UI, no reference to compare
  against, but it behaves correctly.
- **Live status line** - **[LIVE-CONFIRMED]**. Notable, undocumented-but-correct behavior found:
  while an `AskUserQuestion` prompt is pending, the status line doesn't show elapsed/token/task
  count at all - it shows `⚠ Claude has a question — see chat` instead, which is a sensible extra
  bit of polish beyond what Phase 5's doc originally described. **[NO REAL-EXT EQUIVALENT]**.
- **Tool-call card rendering** - both apps, via an equivalent `Bash "dir"` prompt. **Style
  difference worth noting**: ours renders a collapsed `Expander` (icon + "Run command" + summary
  text + status checkmark) that must be clicked to reveal detail; the real extension renders an
  inline, always-visible `IN:`/`OUT:` monospace terminal-style box under the tool name, no
  click-to-expand needed. Both are reasonable choices; the real extension's is slightly more
  information-dense by default, ours is more compact/summary-first. Worth a design discussion, not
  a bug.
- **Permission-prompt style comparison** - the real extension's default permission mode is
  `Manual`, so the same `Bash "dir"` prompt triggered a real permission card there:
  numbered `1 Yes` / `2 Yes, allow dir for this project (just you)` / `3 No` plus a "Tell Claude
  what to do instead" free-text box. Ours (Phase 2, prior session) uses unnumbered `Allow` /
  `Allow for Session` / `Deny` buttons with no inline free-text redirect option on the permission
  card itself (that exists elsewhere, via the retry/interrupt flow). Functionally equivalent
  intent, visually and interaction-wise distinct - the real extension's numbered-shortcut style
  and inline "tell it what to do instead" box are the more notable UX differences to potentially
  borrow.
- **Split command chip buttons (Model/Permission/Effort/Palette)** - **[LIVE-CONFIRMED]**, exercised
  implicitly throughout (visible showing live state: `Haiku` / `Auto (background safety checks)` /
  `Low`). No popup mutual-exclusion issues observed. The real extension instead uses a single
  pill showing the current mode (`Manual`) plus a separate `/` command-menu button - ours splits
  this into four independent chips. Different information architecture, both discoverable; ours
  surfaces more state at a glance without opening a menu, the real extension's is more compact.

### Second pass: remaining items, plus a dedicated style-comparison round

A follow-up round covered the rest of the original Tier A list and a deliberate style-focused
comparison the user asked for explicitly (both UI visual style *and* functional correctness -
not just one or the other, for each item). New composite-capture tooling was needed first: WPF
`Popup`s render as **separate top-level HWNDs**, not children of the main window, so the
original `PrintWindow(mainHwnd)` screenshots never showed open dropdown menus at all (they came
back looking identical to the closed state). Fixed with `screenshot-composite.ps1`, which
enumerates all top-level windows owned by the target process, `PrintWindow`s each one
individually (main window + any open popups), and composites them at their correct
screen-relative offsets - still zero `SetForegroundWindow`/`CopyFromScreen`, still background-safe.

- **`/compact`** - **LIVE-CONFIRMED, functional**: status strip live-updated to "Compacting…",
  final result read exactly `Compacted chat · manual · 32.7k tokens freed`, matching the
  documented format precisely. **Style**: renders as a plain understated system-message row, no
  card/border, consistent with the rest of the transcript.
- **Retry-notice** - **partially confirmed, and the real finding differs from what was expected.**
  Killing the CLI subprocess *while idle* (the only crash scenario testable without racing a
  live in-flight turn) did **not** produce the documented "Try again" UI at all - instead the
  extension silently detected the dead process and transparently respawned a fresh one on the
  very next send, with zero visible interruption. This is arguably better UX than an error
  banner, but it means the specific retry-notice code path is reserved for a crash *during* an
  active turn - a narrower, harder-to-hit race this pass didn't attempt given the quota cost of a
  precisely-timed extra send. Recorded as a real, positive finding rather than a gap.
- **`openDiff`** - **partially confirmed.** The edit-approval flow itself is fully confirmed
  end-to-end: a real edit (adding an XML doc comment to `IsPrime`) was proposed, previewed
  inline as a genuine line-level diff (single green `+` line, not a raw dump) inside an "Allow
  Edit file?" permission card, approved, and applied correctly. **Not** confirmed: whether this
  specifically also opens a **separate native VS diff editor tab** (the Phase 3-documented
  `IVsDifferenceService` flow) - the tab strip still showed only the pre-existing tabs 5 seconds
  after approval. Possibly it opens-and-closes faster than the capture window, or is conditional
  on something this pass didn't exercise (e.g. Manual permission mode specifically, where the
  user is expected to review before the edit lands, vs. Auto mode's fire-and-forget approval used
  here).
- Full drag-and-drop pipeline (images/text/PDF) and paste-screenshot - **STILL UNVERIFIED live**,
  confirmed out of reach of this automation approach: both require genuine OS-level input (OLE
  drag-drop via `IDropTarget`/`DoDragDrop` COM interop, or a real clipboard-paste command) that
  UI Automation's control patterns and CDP DOM events don't go through - those call the app's
  event handlers directly rather than the OS's real drag-drop/clipboard-command pipeline.
  Recommend a manual check, or explicit sign-off to temporarily allow physical input simulation
  for just this pair of tests.
- Running-tasks panel - **partially confirmed**: the tool-call card itself renders correctly, but
  the `dir` command completed in under 4 seconds, too fast to catch the panel actually populated
  mid-flight in a screenshot. Code path (`RunningToolCalls` add/remove tied to `ToolCallViewModel.
  Status`) was reviewed and is correct, but "reviewed correct" is exactly the kind of claim this
  audit exists to avoid resting on - flagging it explicitly as **STILL UNVERIFIED live** rather
  than claiming success.

### Style comparison (dedicated round)

- **Command/settings menu architecture - the most significant style difference found.** The real
  extension's single "/" button opens **one combined command palette**: sections for Context
  actions (Attach file, Mention file, Clear conversation, Rewind), Model (shows current model as
  a trailing label, e.g. "Sonnet 5"), **Effort as a 5-dot slider control** (not a list), **Thinking
  and auto-switch-on-flag as toggle switches**, and Account & usage - one scrollable menu, one
  entry point. Ours splits the same functionality into **four independent popups** (Palette /
  Model / Permission / Effort), each a simple checkmark list anchored to its own always-visible
  chip button. Trade-off, not a bug: the real extension's approach is more compact (one button)
  but requires opening and scanning a longer menu to check any single setting; ours surfaces all
  four current states at a glance in the input row itself, at the cost of more visual chrome.
  The **Effort slider specifically stood out** as worth considering - a 5-dot low↔high spectrum
  communicates the setting more directly than our plain checkmark list of discrete levels.
- **Permission-prompt style** (Phase 2/3, re-confirmed via screenshot this pass) - real extension:
  numbered `1 Yes` / `2 Yes, allow for this project` / `3 No` plus an inline "Tell Claude what to
  do instead" free-text box, all inside the same card. Ours: unnumbered `Allow` / `Allow for
  Session` / `Deny` buttons, no inline free-text redirect on the card itself.
- **Session history** - ours is a flat list (title, relative time, hover-reveal edit/delete
  icons) with a working search box. The real extension groups sessions into folders (session
  groups - deliberately descoped for this extension, see Phase 5, since per-workspace separation
  already does this extension's version of that job). Real information-density difference for
  heavy users with many sessions, not just cosmetic.
- **Tool-call card** (re-confirmed) - ours: collapsed `Expander`, click to reveal detail. Real
  extension: always-visible inline `IN:`/`OUT:` monospace terminal-style box, no click needed.

### Tier C - confirmed not implemented (source-research only, no live test needed to prove an absence)

No change from the Phase 0-5 research: multi-session/session-groups (deliberately dropped, see
Phase 5), voice dictation, side-question panel, rewind, browser/debugger/Jupyter MCP integrations,
plugin marketplace, response rating, git worktree creation from the UI, onboarding
walkthrough/checklist. All still absent from this extension, all confirmed real in the official
extension via source-reading in earlier sessions.

### Third pass: full live-testing sweep of remaining unverified items

A further round, explicitly requested to leave nothing at "build-verified only" that could
actually be driven live. All items below were driven against the real running extension, not
inferred from code.

- **Message queueing** - **LIVE-CONFIRMED**. Two messages sent back-to-back with no wait between
  them: the input box stayed enabled and accepting text the whole time (`IsEnabled: True` while
  the first turn was in flight), the second send was accepted immediately, and both turns
  completed correctly in order (`ONE/TWO/THREE` finished first, then `QUEUED` ran as its own
  subsequent turn) - not dropped, not merged, not out of order.
- **`AskUserQuestion` single-select (radio buttons)** - **LIVE-CONFIRMED**. Real `RadioButton`
  list rendered correctly with descriptions, single selection enforced, Submit produced the
  correct follow-up ("You've selected Fall as your favorite season.").
- **`openDiff` native diff tab - resolved definitively.** A direct check of the open document
  tab list after the earlier edit-approval test (via UI Automation, not a screenshot timing
  guess) confirmed **no diff tab exists anywhere** - only the pre-existing tabs. Under **Auto**
  permission mode specifically, only the inline permission-card diff renders; no separate native
  `IVsDifferenceService` tab opens. Whether Manual mode (where the user is expected to review
  before anything lands) behaves differently is still open - not tested this pass.
- **A real, reproducible bug found: the "Active File" context chip silently fails on preview
  tabs.** With a Markdown **Preview** tab active (`fancy-whistling-ladybug.md [Preview]`),
  clicking "Active File" inserted nothing into the input box, and Claude's own response
  confirmed it: *"I don't have direct visibility into which file your Active File button is
  currently showing."* Switching the active tab to a normal code file (`Class1.cs`) and repeating
  the exact same steps worked correctly - `@Class1.cs` appeared as a real inline chip token, and
  Claude correctly named `Class1.cs` when asked. Root cause is almost certainly VS's active-
  document resolution not recognizing a Preview-mode tab as a normal open document (a well-known
  VS quirk with `IVsMonitorSelection`/`DTE.ActiveDocument`-style APIs). Not yet fixed - this is a
  live finding for the backlog, not a fix applied during the audit (consistent with this project's
  convention of finding-then-fixing-separately during live verification passes).
- **"Selection" context chip, no-selection edge case** - checked for free (no message sent): with
  no text selected in the editor, clicking "Selection" did not visibly add anything new to the
  input box. Inconclusive rather than confirmed clean - an attempt to clear the input box first
  did not obviously take effect, so this result should be treated as a loose end, not a verified
  no-op, and revisited in a future pass with a cleaner setup.
- **Account & Usage panel** - **LIVE-CONFIRMED**, both sides now screenshotted with real data
  (Plan: Claude Pro, real email/org, Session and Weekly usage bars, live per-session token/cost
  totals). Weekly usage climbed from 89% to 91% over the course of this round, consistent with
  the volume of live-message testing performed - tracked explicitly rather than assumed stable.

### Fourth pass: corrections after user review, plus the `@`-mention gap

The user reviewed the third pass and flagged three real problems, addressed directly:

1. **Message queueing was not actually proven.** The original test checked `InputBox.IsEnabled`
   before sending the second message - but that property is `True` regardless of busy state in
   this UI (that's the point of the feature), so the check proved nothing. Worse, the two
   messages may simply have been sent sequentially after the first one already finished, which
   would look identical in a screenshot to genuine mid-turn queueing. **Redone properly**: a
   multi-step task was used specifically so there was a real window to hit, and the second
   message was only sent after directly confirming `StopButton.IsOffscreen == false` (the actual
   busy indicator) - proof, with timestamps, that message 2 was sent while message 1 was
   genuinely still in flight (sent 6s into a 7.9s task). Both completed correctly, in order.
2. **The `@`-mention file picker had never been tried at all.** Tested on both sides. Initial
   attempts on our side failed to open the picker; investigation (reading the actual
   `OnInputTextChanged`/`FindAtTokenStart` source, not guessing) found the real cause was a test
   artifact, not a product bug: WPF's `TextBox.Text` setter does not move `CaretIndex` to the end
   of the new text the way real typing does, so `ValuePattern.SetValue("@Class")` left the caret
   at position 0, and the app's caret-relative `@`-detection logic (correctly) found nothing to
   trigger on. Fixed by sending a real `WM_CHAR` message straight to the window (see new
   `Send-WmChar` helper below) instead of bulk-replacing text - confirmed working correctly on
   **both** extensions once tested properly: typing `@` opens a live-filtered project file list on
   our side (matches the official extension's same behavior), and selecting an entry (via a new
   `Send-WmClick` helper, needed because `ListBoxItem`'s "choose" behavior is wired to a mouse
   routed event that `SelectionItemPattern.Select()` doesn't raise) correctly inserts
   `@Class1.cs`. This was a testing-tool gap, not a real product gap - worth stating plainly since
   the initial failed attempts could easily have been misreported as a bug otherwise.
3. **Methodology correction**: the comparison matrix (`comparison-audit/feature-matrix.md`) was
   restructured so the **official extension is the documented baseline** for every row, with our
   extension's status stated as a checked-against-that-baseline verdict - not two peers compared
   side by side. This matches how the user actually framed the request from the start ("full
   comparison... so we know exactly which features are working, which need to be implemented").

Two new reusable, background-safe automation techniques came out of chasing #2, saved in
`scripts/uia-lib.ps1`:
- **`Send-WmChar`** - `WM_CHAR` sent directly to a window's message queue. Delivered straight to
  that HWND regardless of OS-level foreground focus; never moves the real cursor or steals input.
  Needed for any caret-relative text-input detection (not just `@`-mentions - likely also
  relevant to the `/` slash-command trigger if that's ever automated the bulk-SetValue way).
- **`Send-WmClick`** - `WM_LBUTTONDOWN`/`WM_LBUTTONUP` sent directly to a window at a target
  element's client coordinates. Same non-disruptive property - no cursor movement, no focus
  steal. Needed for any control whose activation is wired to a mouse routed event rather than an
  invocable UIA pattern.

## Stability observations

- No crashes, hangs, or visual corruption in our extension across the whole live pass (paste
  history, multi-select submission, transcript mode toggling mid-transcript, a real tool call).
- The one real instability hit was infrastructure-level (the stale package-registration cache
  above), not a runtime crash - once resynced, the extension ran cleanly for the rest of the
  session.
- The real extension was similarly stable throughout - no observed hangs or rendering glitches.

## Verification

`dotnet build TeronClaudeCodeVS.csproj` - 0 warnings, 0 errors (AutomationId additions only, no
behavior change). All "LIVE-CONFIRMED" items above were driven against the actual running apps
this session, with screenshots reviewed for each, not inferred from code alone.

## Fifth pass: full behavioural documentation of the baseline extension

Driven by a direct correction: the audit had been checking *parity on things we already built*.
The instruction was to document how the official extension behaves **fully - including features
we have not implemented** - and to extract visual/UX improvements from it. This pass did that.

### Recovering the test rig (three real environment bugs)

The isolated VS Code test instance had to be rebuilt mid-pass, which surfaced three genuine
gotchas worth recording:

1. **`ELECTRON_RUN_AS_NODE=1` is inherited from the VS Code extension host.** Because this
   session runs *inside* VS Code, every `Code.exe` launched from it inherited that variable and
   started as a bare Node runtime, rejecting every flag with `bad option: --user-data-dir=...`.
   Fix: scrub all `ELECTRON_*` and `VSCODE_*` variables from the child environment before
   launching. Without this, the launch fails silently in a way that looks like "VS Code just
   didn't start".
2. **A stale `code.lock` in the isolated `user-data-dir`** makes every relaunch hand off to the
   already-dead previous instance and exit with no error at all. Delete `code.lock` (and
   `DevToolsActivePort`) after force-killing an instance.
3. `ProcessStartInfo.ArgumentList` / `.Environment` are **.NET Core-only** - in Windows
   PowerShell 5.1 they are `$null`, so building a launch that way silently produces an
   argument-less process. Use `.Arguments` and `.EnvironmentVariables`.

### A methodology failure, corrected

An earlier claim in this audit - that our Account & Usage panel was *more detailed* than the
baseline's - was **wrong, and was retracted**. The baseline capture behind it had been taken in a
session with **no message activity**, so its Session/Organization rows simply had nothing to
render. Re-tested properly (fresh session -> real message round-trip -> *then* `/usage`), the
baseline shows **Organization and a Session (5hr) bar too** (real:06). The only genuine delta is
our extra `THIS SESSION` cost/token block. Rule reinforced: **never compare two UIs until both
have been driven into an equivalent state.**

A second wrong entry was caught the same way. The matrix had claimed openDiff's separate native
diff tab was "N/A - official's flow is inline-in-editor by design". Driving a real edit proved
the opposite: baseline opens a **full side-by-side native diff tab** titled `[Claude Code] <path>`
with accept / revert / next / prev / swap-sides toolbar buttons, *in addition to* the inline chat
diff (real:12). That is a real gap on our side, not an inapplicable comparison.

### What the baseline actually does (driven, not read off labels)

Every menu entry was opened and exercised. The most useful finding is that **five of the seven
"Customize" items are not GUI features at all** - Memory, Agents, Hooks, Output styles and
Permissions each render an in-chat *"Continue in Terminal to ...?"* hand-off card with
`1 Continue in Terminal` / `2 Never mind` (real:13). Only **MCP servers** (real:14) and
**Manage plugins** (real:15, with Plugins/Marketplaces tabs) are real panels. That reframes five
apparent feature gaps as cheap prompt-cards rather than GUIs we'd have to build.

Other behaviour documented live:

- **Rewind** is a real, complete feature (real:16) - a modal "Rewind to..." picker listing prior
  user messages with relative timestamps and a `up/down to navigate - Enter to select - Esc to
  close` footer. This is the largest genuine feature gap found in the whole audit.
- **Session history** has a **Local / Web tab switch** (Web = cloud/remote-control sessions),
  generated short session titles, and per-row rename/delete icons (real:11).
- **Remote Control** is real and was observed active, with a persistent chat banner.
- **Automatic model downgrade** was observed live: baseline printed
  `Switched to claude-haiku-4-5-20251001` mid-session as the weekly limit approached (real:07).
- **`Mention file from this project...`** simply inserts `@` - it is the same picker we already
  match, not a separate mechanism.
- **`General config...`** just runs `/config` and prints the key list into chat (real:17); it is
  *not* a settings GUI, so our VS Options page is arguably ahead of baseline here.
- **Retry-notice**: the same idle-CLI-kill experiment was run against the baseline with its own
  `claude.exe` identified strictly by parent chain. Baseline **silently respawns and answers
  normally**, exactly like ours - proven by process identity (old PID gone, new PID created the
  same second the message was sent). Previously an open question, now resolved as **parity**.

### Visual/UX improvements extracted

Section 7 of `comparison-audit/feature-matrix.md` is new: ten concrete, screenshot-backed
improvements to make to our own UI using the baseline as reference - model/permission-picker
descriptions, numbered permission cards with an inline redirect box, a palette filter box,
alphabetical command sorting, generated session titles with row actions, the modal-card visual
language, keyboard-affordance footers, the five cheap terminal hand-off cards, and an in-menu
version string. None require new backend work.

### Safety note

While tracing processes for the retry test, the first `claude.exe` examined resolved - via parent
chain - to the user's **real** VS Code window, not the isolated instance. Nothing was killed. The
lesson is procedural: **always resolve a process's full parent chain to the known-isolated root
PID before acting on it**, never match on process name alone. The corrected mapping is what made
the retry-notice test safe to run.

## Sixth pass: UI style measurement, and the controls the audit had skipped

Prompted by a direct challenge - "are you sure you documented the baseline functional and UI
style behaviour fully?" The honest answer was no, on two counts, both now closed.

### Controls that had been enumerated but never opened

The button enumeration had listed several controls that were never actually driven. Opening them
found real features:

- **`+` "Add" menu** (real:18): **Upload from computer**, **Add context**, **Browse the web**.
  Web browsing as a first-class context action is a capability we have no equivalent for.
- **Voice dictation** carries a real keybinding - tooltip *"Tap or hold to record - `Ctrl+D`"*.
- **Copy code** exists per code block (`aria-label="Copy code to clipboard"`); ours is a single
  global Copy Raw Output.
- **Message actions** (`...`) exists on *every* message, user and assistant. This one is **still
  undocumented**: the menu is hover-gated and does not open from a synthetic click. Recorded as
  not-captured rather than guessed.
- The input placeholder doubles as a keyboard hint: *"ctrl esc to focus or unfocus Claude"*.

### UI style, measured rather than eyeballed

Previous passes documented *what features exist* and called that a style comparison. It wasn't.
This pass read **computed styles straight out of the live DOM** and compared them against our
`ClaudeCodeChatControl.xaml` values. New section 7 of the feature matrix holds the table. The
findings that matter:

- **User messages are the biggest divergence.** Baseline: `background: transparent`,
  `border-radius: 0`, full column width, `padding: 14px 0 12px`. Ours: solid `#D97757` fill,
  white text, `CornerRadius="10,10,2,10"`, right-aligned, `MaxWidth=460`. Baseline reads as a
  document; ours reads as a messenger app.
- **Accent discipline.** Baseline uses terracotta (`#C6613F`) on exactly one element - the ~26px
  send button. We use `#D97757` as a large fill behind every single user message, i.e. we spend
  the brand colour on the most-repeated element in the panel.
- **Type scale.** Baseline uses two sizes (13px body, 11.05px chrome) with a 1.5 line-height.
  Our XAML uses **nine** distinct font sizes between 9 and 14px.
- **Radii.** Baseline: 5-6px, with 8px only on banner top corners. Ours: **eight** distinct radii
  from 3 to 15px. Together with the type scale, this is what makes our panel look busier than
  baseline at a glance.
- **Tool-call cards.** Baseline renders them inline as a flat `#191A1B` card with a `#2A2B2C`
  hairline border at 6px radius, in 13px sans (not monospace) - readable with no interaction.
  Ours hides them behind a collapsed Expander.
- **Code blocks** in baseline are deliberately understated: monospace, but transparent
  background, no radius, no padding.
- Both extensions inherit their host theme's font and foreground rather than imposing their own,
  so the *philosophy* matches even where the execution differs.

Six further improvement items (11-16) were added to the matrix's improvement list off the back of
this: revisit the user-message bubble, collapse the type scale, normalise radii, flatten tool
cards, per-code-block copy, and surface keyboard hints in the UI.

## Seventh pass: limit-testing the baseline

Brief was to push the baseline extension to its limits and get full coverage of what it can do
and *how* - explicitly including experimenting with technique. The theme of this pass is that
**three things previously recorded as "cannot be tested" were all reachable by changing
technique, not by accepting the limitation.**

### Things previously written off, now driven

- **Hover-gated "Message actions"** - the sixth pass recorded this as not-capturable because
  `element.click()` did nothing. The fix was to stop using synthetic DOM clicks and dispatch a
  **real `Input.dispatchMouseEvent` `mouseMoved`** at page coordinates (webview iframe offset +
  in-frame bounding rect), which produces a genuine `:hover` state. The menu opened immediately.
  Its contents matter: a **three-way per-message choice** - *Fork conversation from here*,
  *Rewind code to here*, *Fork conversation and rewind code*. That means baseline separates
  **conversation forking from code restoration**, which is a finer-grained (and better) model
  than the single "Rewind to..." entry implied.
- **Paste of an image** - this matrix had carried "automation gap: requires a real OS clipboard
  paste, unreachable" since Phase 5. It is reachable: dispatch a synthetic
  `ClipboardEvent("paste")` carrying a real `File` inside a `DataTransfer`. Baseline accepted it
  and staged a pending chip reading `test-paste.png  1x1`.
- **Drag-and-drop** - likewise carried as "needs genuine OLE `IDropTarget`/`DoDragDrop` COM
  interop, not reachable without physical mouse input". Also reachable, for the webview side, via
  a synthetic `DragEvent` sequence (`dragenter` -> `dragover` -> `drop`) with a `File` in the
  `DataTransfer`. Baseline staged a second chip for `dropped.py`.
  **Caveat worth keeping honest:** this proves the *baseline's* drop handling, and it exercises
  the web `DataTransfer` path. It does **not** discharge the equivalent test on our WPF side,
  which really does go through OLE COM interop - that one still needs a different technique.
- **Shift+Tab cycling** - `Input.dispatchKeyEvent` with `modifiers: 8`. Confirmed a closed
  three-way cycle: Manual -> Edit automatically -> Plan -> Manual.

### Two earlier entries corrected

- **Tool-call rendering.** The style pass had recorded baseline as showing tool calls in an
  always-visible flat card, versus our collapsed Expander. Wrong - baseline has *two* states, and
  **it collapses by default too**: the collapsed form is a plain unboxed `1 tool call` line with a
  chevron, and only the expanded form is the `#191A1B` bordered card. Ours is closer to baseline
  than recorded. Baseline's real edge is the annotation: it groups and reports failures on the
  collapsed line (`1 tool call - 1 failed`).
- **Focus view.** Recorded in the fifth pass as "no visible change". It is in fact a **real
  persisted setting** - inspecting the isolated profile's `settings.json` afterwards showed
  `"claudeCode.focusView": true` had been written. The absence of a visual change in that layout
  was not the same as the control doing nothing.

### Theme behaviour, measured in both themes

Two distinct theme systems exist and had been conflated:

- The `theme=` key in `/config` (7 values, including **daltonized** colour-blind variants and
  **ANSI** variants) is **CLI-side only** - setting it changed nothing in the webview.
- The IDE panel simply follows VS Code's theme. Verified properly by writing
  `workbench.colorTheme` into the isolated profile and reloading: body text went
  `#BBBEBF` -> `rgb(59,59,59)` and the header `#191A1B` -> `#F8F8F8`, **while the send button
  stayed `#C6613F` in both themes**.

That yields a crisp, reusable design rule: **every surface and text colour is derived from the
host theme, and exactly one brand accent is theme-invariant.** Our extension currently does the
inverse - it hardcodes `#D97757` onto the largest repeated surface in the panel (the user-message
bubble). This is now the measured basis for improvement items #11 and #17.

### Newly documented surfaces

- **Empty state** (surfaced by the post-reload fresh session): wordmark, a terracotta pixel-art
  robot mascot, a rotating tip, and a dismissible *"Prefer the Terminal experience? Switch back
  in Settings."* hint. We have no designed empty state at all.
- **History -> Web tab** (never clicked before): **cloud session sync**. It lists sessions from
  other machines under generated machine names (`kaloyan-pc-wild-wozniak`,
  `kaloyan-pc-glistening-gosling`, ...) with relative ages, so a session started on another device
  or on the phone can be resumed in the IDE.
- **`+` Add menu**: Upload from computer / Add context / **Browse the web**.

Improvement list grew from 16 to 21 items.
