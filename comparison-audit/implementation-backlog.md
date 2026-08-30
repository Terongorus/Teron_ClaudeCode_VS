# Implementation backlog — Teron_ClaudeCode_VS, derived from the baseline audit

This is [`feature-matrix.md`](feature-matrix.md) **inverted for planning**. The matrix is organised
for auditing (by feature area, baseline vs. ours). This file is organised by **work item**: what to
build, what "done" means in measured baseline terms, and what it depends on.

**Every acceptance criterion below is a value measured off the live baseline**, not a preference.
Where a number appears (`#C6613F`, `radius: 6px`, `13px`), it came out of the running extension —
see the referenced screenshot in `screenshots/real-extension/`.

**Nothing here is committed work.** It is a menu to plan from. Sizes are relative
(S ≈ hours, M ≈ a day, L ≈ multi-day, XL ≈ a phase of its own).

---

## How to read this

| Field | Meaning |
|---|---|
| **ID** | Stable reference for planning (`ST-*` style, quotable in a plan or commit) |
| **Size** | S / M / L / XL, relative effort |
| **Evidence** | Screenshot or matrix section that justifies the item |
| **Done when** | Acceptance criteria, in measured baseline terms |

Tiers are ordered by *value per unit of effort*, not by feature glamour.

---

## Implementation status

Updated as each phase of the Phase 7 parity build lands on `dev`. Commit SHAs are collected in
`docs/Phase 7 - Baseline Parity Implementation.md`; this table is the quick index.

| ID | Status | Phase | Note |
|---|---|---|---|
| **BUG-1** | ✅ done | A | Fixed by resolving the shell's `SEID_DocumentFrame` first. Verified live on the exact Markdown Preview tab that failed in the audit. |
| **ST-1** | ✅ done | B | `Core/ChatTheme.xaml`. |
| **ST-2** | ✅ done | B | 9 sizes → 2 (`FontSizeBody` 13, `FontSizeChrome` 11), plus 2 explicitly-separate glyph metrics. |
| **ST-3** | ✅ done | B | 8 radii → 2 (`RadiusControl` 5, `RadiusCard` 6), plus 2 shape radii (circle/pill). |
| **ST-4** | ✅ done | B | Verified by pixel sampling in both themes - see below. |
| **ST-5** | ✅ decided | B | **Bubble kept**, deliberately. Rationale recorded at the template in `ClaudeCodeChatControl.xaml`. |
| **UX-1** | ✅ done | C | Descriptions on all 5 models, taken from the CLI binary's own model table. |
| **UX-2** | ✅ done | C | Descriptions on all 7 modes + `⇧ + tab to switch` hint + Shift+Tab cycling. |
| **UX-3** | ✅ done | C | Numbered actions, full absolute path, `Esc to cancel`, inline redirect box. |
| **UX-4** | ✅ done | C | Filter box; measured 50 → 8 rows on the filter `co`. |
| **UX-5** | ✅ done | C | Sorted at populate; verified A–Z over all 50 live commands. |
| **UX-6** | ✅ done | C | Picker footers, permission-card key hints, and a placeholder naming the real focus chord. |
| **UX-7** | ✅ done | C | Per-turn grouped annotation; observed rendering `2 tool calls · 1 failed`. |
| **UX-8** | ✅ done | C | Copy button per fenced block, as a FlowDocument `Floater`. |
| **UX-9** | ⚠ built, unverified | C | Chips show name + pixel dimensions + a type glyph. Rendering not yet driven — needs the paste/drop harness from **TEST-1**. |
| **UX-10** | ✅ done | C | `v0.3.0` in the palette footer, read from the shipped VSIX manifest. |
| **UX-11** | ✅ done | C | Designed empty state on a new session. |
| **UX-12** | ✅ done | C | One `PopupCardStyle` behind every popup, plus shared hint/footer styles. |
| **GAP-1** | ✅ done | D | Five hand-off cards, baseline's wording verbatim; each launches `claude /<key>` on confirm. |
| **GAP-2** | ✅ done | D | Launches Windows Terminal in the solution directory. External, not in-frame — see below. |
| **GAP-3** | ✅ done | D | Measured extension-injected, then built: `/btw`, `/feedback`, `/remote-control` (+ `/rc`). |
| **FEAT-2** | ✅ done | E | Native VS diff tab, auto-opened on an edit prompt. Accept/revert stay on the card — see below. |

**ST-4 measurement (Phase B, VS 18 Experimental instance, 2026-08-29).** Sampled from
`PrintWindow` captures, not judged by eye:

| Surface | Light theme | Dark theme |
|---|---|---|
| Chat panel background | `#F9F9F9` | `#282828` |
| Solution Explorer background (control) | `#F9F9F9` | `#282828` |
| Chat input area background | `#EFEFEF` | `#2F2F2F` |
| Send button fill (accent) | `#D97757` | `#D97757` |

The chat panel tracks a stock VS tool window exactly in both themes, and the accent is
byte-identical across them - which is what ST-4 asks for. Evidence:
`screenshots/our-extension/28-PhaseB-tokens-light.png`, `29-PhaseB-tokens-dark.png`,
`30-PhaseB-tokens-model-popup.png`.

> **On ST-2/ST-3 and the "two values" criterion.** Two of the nine font sizes and four of the
> eight radii were never typography or corner treatment: they size icon glyphs inside fixed-size
> buttons, and they round an element to a circle or a pill from its own height. Those are geometry.
> They are kept as separately-named tokens (`GlyphSize`, `GlyphSizeSmall`, `RadiusCircle`,
> `RadiusPill`) rather than folded into the scale, so that the two-value rule stays a real
> constraint on the type and corner scales instead of being quietly widened to four.

### Phase C notes (2026-08-29)

**Where the wording came from.** UX-1 and UX-2 descriptions are not invented. The permission-mode
strings were read out of the official extension's own `webview/index.js`, and the model subtitles
out of the CLI binary's model table, so the five modes and four models we share with baseline use
baseline's exact words. Only two entries are ours, because baseline ships no picker row for
either: `CLI Default`, and `Don't Ask` — whose real behaviour is the opposite of what its name
suggests. The CLI documents it as *"Don't prompt for permissions, deny if not pre-approved"*, so
the description says it denies rather than auto-approves. That was worth writing down: the name
alone actively misleads.

**Two judgement calls that diverge from a literal reading of the criteria.**

* **UX-7** asks for a *collapsed row* annotated with a grouped count. Baseline collapses a whole
  run of tool calls into one row; our transcript keeps each call as its own already-collapsed card,
  which is more informative when calls succeed. Rather than restructure the card list, the count
  and failure state are rendered once per assistant turn above the run. It is deliberately silent
  for a single successful call, where it would only restate the card beneath it.
* **UX-6** asks the input placeholder to name its focus shortcut. Baseline uses `ctrl esc`, which
  is unusable on Windows — the OS claims it for the Start menu. We bind `Ctrl+Alt+Y`, chosen by
  querying the live VS command table for a chord free in every scope and not a chord prefix. The
  placeholder does not hard-code it: it asks VS at runtime what the command is actually bound to,
  so it cannot advertise a chord the user has rebound.

**Three things this phase learned the hard way, all recorded in code comments:**

1. **A changed `.vsct` does nothing until `ProvideMenuResource`'s version is bumped.** VS caches the
   merged command table against that number. The key binding silently did not exist until it went
   from 1 to 2, with no build or load error of any kind.
2. **VS silently drops a VSIX default key binding that collides with an existing one.** The first
   attempt used `Ctrl+Alt+C`, which VS already gives to `Debug.CallStack`. The only symptom was
   `Commands.Item(...).Bindings` coming back empty at runtime.
3. **`PrintWindow` cannot capture the VS 18 frame while it is occluded**, and reports success
   anyway. All four flag values return an essentially blank bitmap. WPF Popups are unaffected
   because they are their own top-level windows, which is why the picker screenshots below exist
   and the in-frame ones do not. See `scripts/screenshot-toolwindow.ps1` for the full negative
   result, and use UIA text assertions for anything inside the frame.

**Phase C verification.** `scripts/phase-c-verify.ps1` — 19/19 checks pass against the running
control in the VS 18 Experimental instance, plus three driven turns covering the surfaces that
only exist mid-conversation. Highlights, all read back from the live visual tree rather than
inferred from a clean build:

| Check | Result |
|---|---|
| Chat control instantiates; 301 elements in the tree | every token and the 3 new styles resolve |
| Model picker | Opus row reads `… · ~2× usage vs Sonnet` |
| Permission picker | all 7 descriptions + `⇧ + tab to switch` |
| Permission card | `1 Allow` / `2 Allow for Session` / `3 Deny`, `Esc to cancel` |
| Permission card path | `D:\…\Test_Project_Claude\PhaseCProbe.txt` (rooted, not the shortened summary) |
| Redirect box | denies with a reason; card resolves to `Redirected: …`, and **no file was written** |
| Slash commands | 50 commands, A–Z under `OrdinalIgnoreCase` |
| Palette filter | `co` narrows 50 → 8 |
| Tool-call annotation | `2 tool calls · 1 failed` |
| Code blocks | 2 per-block `Copy` buttons rendered |
| Placeholder | `Ask Claude anything…  (Ctrl+Alt+Y to focus)` |
| Palette footer | `v0.3.0` |

Evidence: `screenshots/our-extension/31-PhaseC-model-descriptions.png`,
`32-PhaseC-permission-descriptions.png`, `33-PhaseC-palette-filter.png`.

**Not verified by driving, and why.** Two items are implemented and compile but were not exercised
end to end:

* **UX-2's Shift+Tab cycle.** WPF reads `Keyboard.Modifiers` from real keyboard state, so a
  synthetic `PostMessage` carries no Shift. Driving it needs `SendInput`, which steals focus and
  mutates real modifier state — both ruled out by this harness's background-safety constraint. The
  hint renders and the handler is in `OnInputPreviewKeyDown`; the cycle itself is unproven.
* **UX-9's attachment chips.** Requires a real paste or drop. That is exactly what **TEST-1**
  exists to build, so it is deferred there rather than faked.

**Also fixed in Phase C:** five `FontSize` literals that Phase B's sweep missed because they were
written as `<Setter Property="FontSize" Value="11"/>` rather than as attributes. ST-2's two-value
rule now genuinely holds across the file.

---

### Phase D notes (2026-08-29)

**GAP-3 is answered, and the answer was "implement".** The item asked us to determine whether the
three missing commands are CLI-provided (a passthrough bug) or extension-injected (real work).
Measured directly against the shipped CLI binary (v2.1.251) by running it in the same
`-p --input-format stream-json` mode this extension uses and reading the `init` event: it lists
**50** slash commands, and `btw`, `feedback` and `remote-control` are **not among them**. They are
injected by the official extension. So all three had to be built.

What made them cheap was the second half of that investigation: none of the three is proprietary
to VS Code. Each is backed by a **control-request subtype the CLI itself handles**, on the same
stdin/stdout channel this extension already speaks for interrupts and permission responses
— `side_question`, `submit_feedback`, and `remote_control`, all three confirmed present in the
CLI binary's own request dispatcher, not merely in the official extension's SDK wrapper. The work
was therefore generalising `SendInterruptAsync` into `SendControlRequestAsync` and adding three
thin callers.

| Command | Wiring | Confirmed live |
|---|---|---|
| `/btw` | `side_question` control request; answer rendered in its own card | real model answer returned and rendered |
| `/feedback` | `submit_feedback`, **behind a confirmation card** | card renders; declined, nothing sent |
| `/remote-control` (and `/rc`) | `remote_control`, **behind a confirmation card** | card renders; declined, bridge never enabled |

**Two of the three are gated behind a confirmation, deliberately.** `/feedback` uploads this
session's transcript to Anthropic and `/remote-control` publishes the session to claude.ai/code
where it can be driven from another device. Both leave the machine and neither is trivially
undoable, so neither fires on the command alone — typing it renders a card with baseline's
numbered `1`/`2` convention and waits. Turning Remote Control back **off** is not gated, since
that direction only reduces exposure. Baseline toggles both immediately; this is a deliberate
divergence and the one place in Phase D where we are intentionally more conservative than the
measured baseline.

**GAP-2 is an honest divergence, not a match.** Baseline calls `vscode.window.createTerminal()`
and gets a terminal docked inside the IDE. Visual Studio exposes no equivalent: its Terminal tool
window is not on DTE, there is no VS SDK service for creating one or sending text to it, and
`View.Terminal` only opens the window — what shell it starts and what is typed into it are not
scriptable. So `TerminalLauncher` opens an **external** terminal: Windows Terminal when the
`WindowsApps\wt.exe` alias exists, a console host otherwise. Same CLI, same working directory,
different frame. Verified against the real process table rather than by looking at a window:

```
wt.exe -d "D:\Projects\Visual Studio Projects\Test_Project_Claude" ...\claude.exe /hooks
```

with a genuine interactive `claude.exe` as its child, and the card resolving to
`Opened Claude in a terminal running /hooks.`

**All GAP-1 wording is baseline's own**, lifted from the `W30` table in the official extension's
`webview/index.js` rather than paraphrased — these are promises about how configuration
propagates back to the IDE, and a reworded promise is a different promise. Baseline's table has a
sixth entry, `plugins`, which it deliberately skips when building this menu because plugins get a
real GUI panel; we skip it identically (see FEAT-5). Note also that baseline keys the "Output
styles" card to `config`, not `output-style` — there is no `/output-style` command; the setting
lives inside `/config`, which is what its description says. All five commands were confirmed to
exist as real interactive commands in the CLI binary. They are absent from the headless
`slash_commands` list precisely because they open interactive TUI surfaces, which is the reason
they need a terminal at all.

**A harness discovery that invalidates an assumption the earlier scripts were built on.**
Phase C's verification enumerated UIA elements and asserted on `.Current.Name`. That sweep is
**structurally blind to markdown content**: everything `MarkdownViewer` renders — assistant
replies, thinking blocks, tool output, the `/btw` answer — lives in a `FlowDocument`, which UIA
exposes as a `ControlType.Document` with an **empty Name** and its text reachable only through
`TextPattern`. A Name-only assertion can therefore report "the card rendered" while being unable
to see whether it rendered anything inside it. `Get-DocumentTexts` was added to
`scripts/uia-lib.ps1` and is the correct tool whenever the assertion is about model-produced text;
Name enumeration stays correct for chrome (labels, buttons, menu rows, status lines). Used here,
it read the real `/btw` answer back out of the running control.

**Phase D verification.** `scripts/phase-d-verify.ps1` — 21/21 structural checks pass, plus a
live driven session covering the surfaces that only exist mid-conversation.

| Check | Result |
|---|---|
| CUSTOMIZE section | all 5 rows + all 5 baseline menu descriptions |
| Hand-off card | title, body, `claude /memory`, `1  Continue in Terminal`, `2  Never mind` |
| Hand-off launch | real `wt.exe` process with the right cwd and `/hooks` |
| Injected commands | `/btw`, `/feedback`, `/remote-control` all listed; 50 → 53 rows |
| Sort not regressed | merged list still A–Z under `OrdinalIgnoreCase` (UX-5) |
| `/btw` | real answer returned over `side_question` and rendered |
| `/feedback` | confirmation card shown, declined, nothing uploaded |
| `/remote-control` | confirmation card shown, declined, bridge never enabled |
| `/rc` | resolves to the same card (baseline's alias) |

Evidence: `screenshots/our-extension/37-PhaseD-customize-section.png`,
`38-PhaseD-handoff-card.png`, `39-PhaseD-injected-commands.png`, `40-PhaseD-side-question.png`,
`41-PhaseD-remote-control-confirm.png`. Unlike Phase C's in-frame attempts these are not blank,
because the composite capture path works while the frame is visible — the PrintWindow
limitation documented in `scripts/screenshot-toolwindow.ps1` applies to an **occluded** frame.

**Not verified, and why.** The `1`/`2` keyboard shortcuts on the choice cards are implemented and
gated exactly like the permission card's, but driving them needs `SendInput` for real modifier and
key state, which steals focus — the same constraint that left UX-2's Shift+Tab cycle unproven in
Phase C. Both are waiting on **TEST-1**. The buttons themselves were driven and work.

**One deliberate behavioural difference from baseline** worth recording: baseline's hand-off is a
modal dialog that disappears once answered. Ours resolves in place and stays in the transcript
with an italic outcome line, so a session's history shows what was offered and what was chosen.

### Phase E notes (2026-08-29)

**What VS gives and what it does not.** Baseline's diff tab carries five toolbar buttons: accept,
revert, next change, previous change, swap sides. Visual Studio's own difference window supplies
the navigation half outright — `Previous difference` and `Next difference` were read out of the
live UIA tree, not assumed — plus its side-by-side/inline view switch. The other two cannot be
had: `IVsDifferenceService` is read-only browsing UI with no apply mechanism and no way to add
commands to the window it creates, which this codebase had already established when the MCP
`openDiff` path was built. So accept and revert stay on the chat card, driving the same permission
response they always did. **The tab is the view; the card is the control.**

Both sides are temp files marked read-only, because it really is a view: a user who typed into a
pane and saved would otherwise be editing scratch while believing they had edited their file. That
attribute is also why this path does not hand VS the `VSDIFFOPT_*IsTemporary` flags the MCP path
uses — `File.Delete` throws on a read-only file, so cleanup is ours. (That is a justification for
the choice, not a measured claim about what VS would have done.)

**When it opens.** Automatically on an approval prompt, which is the moment a human is already
being asked to look at something — so under `acceptEdits` or `bypassPermissions`, where no prompt is
raised, no tabs pile up behind a long agent run. One tab per file, replaced rather than stacked.
Both the approval card and the finished tool call carry a manual button; the approval card drops
its button once answered, because its comparison assumes nothing has touched the file yet and that
stops being true the moment the edit is allowed.

**The correction that mattered.** FEAT-2 needs the "before" side of an edit that has already been
applied, and the tool input cannot supply it — a `Write` call carries no record of what it
overwrote. The CLI's own checkpoint store can, and `ViewModels/SessionCheckpointStore.cs` reads it.
The first version of that reader was **wrong in a way that looked right**: it assumed one
`file-history-delta` per edit. In fact the CLI writes a delta only the first time it backs up a
given file; after that the file is carried forward in each turn's `file-history-snapshot`, under
`trackedFileBackups`. Reading deltas alone returns a real backup of the right file **from the wrong
point in its history** — a plausible-looking wrong diff, which is worse than no diff because it
invites trust. Caught by a live run, corrected, and re-verified.

A second, latent version of the same mistake was found while writing this up: the pending-edit
branch read `before = backup ?? diskText`, harmless today only because every pending caller passes
no tool-use id. Had one ever been passed — entirely natural, since a permission request has one —
a proposal would have been compared against a stale snapshot instead of the working copy. The
lookup now lives inside the applied branch, where it is the only honest source.

**Verification.** Three scripts, 52 checks, all passing.

| Script | What it establishes |
|---|---|
| `scripts/phase-e-verify.ps1` (21) | The whole Edit path live: auto-open, labels, real difference navigation, temp files read-only with correct contents, the edit landing on disk, one tab per file, the approval card dropping its button once answered. |
| `scripts/phase-e-verify-write.ps1` (7) | That the applied "before" really comes from the CLI's store. A `Write` cannot be reverse-reconstructed by design, so a correct left side has only one possible source. |
| `scripts/phase-e-unit.ps1` (24) | The branches a live session structurally cannot reach — see below. |

Evidence: `screenshots/our-extension/42-PhaseE-proposed-diff-tab.png`,
`43-PhaseE-applied-diff-tab.png`, `44-PhaseE-write-diff-tab.png`. The last one shows the rendered
comparison (`1 change · -4 · +1`), not merely that a tab exists.

**Why a third script exists.** The live run leaves real branches unexecuted, and unexecuted is
untested however green the run looks. The CLI writes a backup for every edit, so the
reverse-reconstruction fallback never runs; the model will not emit `replace_all` on request; no
temp directory is ever a day old during a test; and no error string is ever displayed when
everything works. `phase-e-unit.ps1` reaches all of those **through the real built assembly by
reflection**, not through a copy of the logic pasted into a test project, and checks
`SessionCheckpointStore` against the actual transcripts and backup files the live runs left in
`~/.claude`. It needs no IDE, opens no window, and takes no focus.

**Harness lessons, all of which first showed up as a "product bug" that was not one.**

| Symptom | Cause |
|---|---|
| Tab reported missing though it was open | PowerShell `-like` reads `[Claude Code]` as a character class. Assertions on that caption need ordinal `Contains`. |
| A card's button "not invokable" moments after its text appeared | Text and automation peer do not arrive together. `Find-InvokableByName` retries; waiting for text is a different question from finding its control. |
| Tool card would not expand | A WPF Expander header is a ToggleButton and the Expander a Group with `ExpandCollapsePattern` — neither supports `InvokePattern`, so a walk-up-for-something-invokable finds nothing. `Expand-UiaByLabel` added. |
| A real diff of the wrong tool call | Every tool card carries the same `AutomationId`, so a lookup by id returns whichever comes first in the tree. Collapse the cards a test is not talking about. |
| "4 tabs" for one tab | The caption also appears on the window frame, the document and the title bar. Count `TabItem`s, not name matches. |
| Several unit assertions passing while exercising nothing | Setting a `JObject` indexer through PowerShell's object adapter silently does nothing, so the inputs were empty. Build inputs by parsing JSON. |
| Parameter-count mismatch from reflection | A `JObject` is enumerable, so `$bound += $jobject` appends its properties. Build argument arrays by index. |

**Deliberately not covered.** `NotebookEdit` gets no tab: a cell edit inside a `.ipynb` would mean
re-serialising the notebook and diffing our guess at the CLI's output. It is refused with a
sentence rather than silently ignored, and that refusal is asserted.

### Phase F notes (2026-08-30)

**FEAT-3 is a read, not a generation problem.** The CLI already names its own sessions and writes
the result into the transcript, as `{"type":"ai-title","aiTitle":…}` and, when the user renames a
session themselves, `{"type":"custom-title","customTitle":…}`. So the work was to read that
correctly, and `ViewModels/SessionTitleReader.cs` was written against 99 real transcripts on this
machine rather than against a fixture matching an assumption. Three facts the reading depends on,
each measured:

1. **Neither record appears once.** They are re-emitted as the session runs — one transcript holds
   236 — and the generated title is genuinely revised along the way ("Teronserver services
   consolidation" later became "Consolidate projects into common solution"). The last record of a
   kind is current; the first is stale.
2. **The last title record in the file is not the answer.** In several transcripts a `custom-title`
   the user typed ("11.08.26 - Import and review previous session history") is followed by a later
   `ai-title` carrying the generated text — and the real client still shows the custom one. This is
   not last-wins: the last `custom-title` wins outright, and `ai-title` answers only when no custom
   title was ever set. A test fixture is chosen specifically because a last-wins rule gives a
   different answer on it.
3. **Field order varies** between `sessionId` and the title field, so records are parsed as JSON,
   not pattern-matched. Every title record across all 99 files named its own file's session, so the
   file itself is treated as the identifying fact.

**Cost, which shaped the design.** Transcripts here reach 45 MB and the history list holds up to
100 sessions, so a naive full scan per row would be seconds of file I/O on the thread drawing the
overlay. The reader takes a 1 MB window off the end of the file and falls back to a full scan only
when that window holds no title at all — 9 ms versus 202 ms on the 45 MB transcript, measured. On
top of that each row records the transcript's size and write time, so an unchanged file is not read
a second time, and the whole refresh runs on a background thread and is applied back on the
dispatcher.

**A rename typed here always wins.** `SessionHistoryEntry.HasUserTitle` is set by
`CommitSessionEntryTitle` and persisted, so the generated title never overwrites a name the user
typed — including when the rename happens while a refresh is already in flight, which is checked
rather than argued.

**What it is worth.** On the real history file on this machine, **24 of 26 rows** would get a better
title on the next history open, replacing truncated first messages like *"Use the Edit tool to
replace the word ALPHA with BRAVO in…"*.

**Verification.** Two scripts, 52 checks, all passing, **neither needing Visual Studio**.

| Script | What it establishes |
|---|---|
| `scripts/phase-f-unit.ps1` (36) | The reader against real transcripts in every shape that occurs (custom-beats-later-ai, revised generated titles, small files, no titles at all), the tail window and its full-scan fallback, decoy content lines, a seek landing mid-character, and `ComputeTitleUpdates`' skip/stamp/no-change branches. Also that the existing pre-Phase-F `sessions.json` still deserializes with the two new fields defaulted. |
| `scripts/phase-f-vm.ps1` (16) | The view-model path: the constructor's refresh, nothing applied before the dispatcher runs, persistence, and the rename-during-a-refresh race — deterministic, because the apply cannot run until the script pumps a frame. The history store's path is redirected into TEMP first and the real `%APPDATA%` file is asserted untouched. |

Phase E's third script existed because a live run could not reach certain branches. Phase F needed
no live run at all: the view model constructs on any STA thread and its dispatcher can be pumped
in-process, which reaches more than an IDE session would and takes no focus.

**Harness lessons, both of which first presented as product failures.**

| Symptom | Cause |
|---|---|
| Two decoy checks failing against a reader that was behaving correctly | Inside `@(...)`, `'a' + ('x' * 400), 'b', 'c'` binds the `+` across the whole comma list, so the array collapsed to one string and the fixture file was written with a single line. Build long strings on their own line, then assert the array's length. |
| "No update computed", and two *passing* checks that proved nothing | `MethodInfo.Invoke` on a **void** method returns `$null`, and PowerShell emits that `$null` into the enclosing function's output — inflating every result array. Because `$null.Count` is 0 in PowerShell, the "expected no update" checks passed for entirely the wrong reason. `[void]` the call, and keep arrays intact across `return` with a leading comma. |

### Phase G notes (2026-08-30)

**FEAT-4 and FEAT-5 are the two Customize entries that are real GUI, and both are windows onto the
CLI.** Neither panel owns any state: `Core/ClaudeCliQuery.cs` runs a subcommand headlessly, and the
two view models turn its output into rows. That is why the empty states match baseline — for MCP,
the sentence the panel shows *is* the sentence `claude mcp list` printed, not a copy of it.

**What the CLI actually offers, measured rather than assumed** (shipped CLI 2.1.251, 2026-08-30):

| Query | `--json`? | Shape |
|---|---|---|
| `claude mcp list` | **no** — its only option is `-h` | text, one line per server |
| `claude plugin list` | yes | bare array of installed plugins |
| `claude plugin list --json --available` | yes (`--available` *requires* `--json`) | `{ "installed": [...], "available": [...] }` |
| `claude plugin marketplace list --json` | yes | bare array of marketplaces |

So the plugins panel parses JSON and the MCP panel parses text — and the text format was not
guessed. It was read out of the shipped binary's own renderer:

```
sse:            `${name}: ${url} (SSE) - ${o}`
http:           `${name}: ${url} (HTTP) - ${o}`
claudeai-proxy: `${name}: ${url} - ${o}`
stdio:          `${name}: ${command} ${args.join(" ")} - ${o}`
                o = issue ? `${status} — ${issue}` : status        (that second dash is an em dash)
```

with a closed status vocabulary of nine strings: `✓ Connected`, `! Connected · tools fetch failed`,
`! Needs authentication`, `- Not configured`, `✗ Failed to connect`, `✗ Connection error`,
`⏸ Pending approval (run \`claude\` to approve)`, `✗ Rejected (see disabledMcpjsonServers in
settings)`, `⊘ Disabled for this project (re-enable via /mcp)`.

**Two defects that only that vocabulary could have revealed**, both found by the harness and fixed:

* `✗ Rejected (see disabledMcpjsonServers in settings)` was classified as *Disabled*, because the
  status names the setting that caused it and a case-insensitive search for "disabled" matches the
  sentence before "Rejected" does. Rejection is now tested first, and disablement is matched on the
  fuller phrase.
* `- Not configured` begins with the separator's own characters, so `name: cmd - - Not configured`
  splits one character late — leaving a stray dash on the target and eating the status's leading
  marker. Detected by exactly that stray dash and undone.

Neither is hypothetical: both statuses are ones the shipped CLI emits.

**The working directory is part of the answer.** `claude mcp list` resolves project-scoped servers
out of the `.mcp.json` beside the current directory. Run from the extension host's own cwd — which
is wherever `devenv.exe` lives — a solution's own servers simply do not appear, and no amount of
parser testing would show it. `ClaudeCliQuery` therefore takes a working directory, the panel passes
the solution directory, and the panel prints that directory under its title so the reader knows what
scope they are looking at.

**A divergence from baseline's empty state, taken deliberately.** FEAT-5's acceptance criterion is
baseline's sentence, *"No plugins available. Add a marketplace to discover plugins."* — which is
correct advice when there is no marketplace to discover anything from, and misleading once there is
one. It is used verbatim in exactly the case it describes; when marketplaces exist but nothing is
installed, the CLI's own *"No plugins installed. Use `claude plugin install`…"* is shown instead.
Both branches are covered by tests. (Baseline's MCP sentence as transcribed in the Phase 6 audit
said "to add servers."; the shipped CLI says "to add a server." Since the panel surfaces the CLI's
own line, ours is right by construction — the audit's transcription was one word off.)

**Verification — 141 checks, no Visual Studio instance, no window, no focus taken.**

| Script | Checks | What it establishes |
|---|---|---|
| `comparison-audit/scripts/phase-g-unit.ps1` | 99 | The parsers, against real captured output plus every status in the CLI's vocabulary; both hard format cases above; both JSON shapes; noise, chatter-prefixed JSON and malformed rows; the empty-state branch; **and every `{Binding …}` path the two new panels declare, resolved against the real view-model types** — the one XAML failure mode (a silent typo) that a headless run can still catch. |
| `comparison-audit/scripts/phase-g-vm.ps1` | 42 | The real view models driving the **real CLI**: two project-scoped MCP servers found in one directory and none in the directory next door (the working-directory proof, re-run in reverse as a control); a bogus CLI path producing an error rather than a serene empty state; the timeout path; and a real marketplace with a real installed plugin, created under a throwaway `CLAUDE_CONFIG_DIR` so the user's own configuration is never written — asserted unchanged before and after. |

Every check that asserts something is *empty* is paired with a positive control that runs the same
code and must not be — the rule Phase F's void-`Invoke` trap earned.

**Two incidental findings worth keeping.** `CLAUDE_CONFIG_DIR` is honoured by the CLI and is the
clean way to exercise plugin state without touching a real machine's configuration; and
`ClaudeCliLocator.Find(null)` resolves to the CLI bundled with the VS Code extension
(`anthropic.claude-code-2.1.251`) on this machine rather than the one on `PATH`, so the two harnesses
between them exercised two different CLI builds and got identical output shapes from both.

**Not covered by this phase's tests:** the rendered XAML itself — layout, the tab strip's underline,
the modal shadow — and the six `Click` handlers, which are three lines each. Those need a live
instance and are deferred to TEST-1 with the rest.

---

### Phase I notes (2026-08-30)

FEAT-1 was the backlog's one XL item and its "largest genuine feature gap". It came in far smaller
than that, because the mechanisms already existed in the CLI and only had to be found — but finding
them changed the design twice, so the route is worth recording.

#### The CLI does all three of these itself

Everything below was measured against the shipped binary (v2.1.251) **before** any of FEAT-1 was
written, in a throwaway session in a scratch directory that was given two turns — write `ALPHA` to a
file, then change it to `BETA`.

| What was needed | What the CLI already has | How it was established |
|---|---|---|
| Restore files changed since a message | `rewind_files` **control request** — `{user_message_id, dry_run}` → `{canRewind, error?, filesChanged?, insertions?, deletions?, skippedLinks?}` | Sent on the same stdin/stdout control channel this extension already uses for `interrupt`. Answered `canRewind:true` with the real file list; the same call with `dry_run:false` put the file back to `ALPHA`. |
| A preview to confirm against | the same request's `dry_run` | Returned the file list and `+1 −1`, and the file on disk was still `BETA` afterwards. |
| Fork the conversation at a point | `--fork-session` **plus the hidden `--resume-session-at <id>`** | Forking the two-turn session at the first turn's last entry produced a new session id, a transcript holding turn one and the new prompt only, and an original left untouched. |

`--resume-session-at` is hidden from `--help` and its own text says it keeps everything up to **and
including** the id it is given — so forking "from" a message means resuming at the entry *before*
it. That entry is the nearest preceding `assistant`/`user` record, which is baseline's own rule and
is **not** always the record's `parentUuid`.

**The plan's design changed as a result.** It called for walking `file-history-delta` records and
writing the backups back ourselves. That is now explicitly not what happens: `SessionCheckpointStore`
stays a read, and the restore is asked of the CLI. Re-deriving its rules from outside — which paths
it refuses, what counts as "already tracked", how a symlink is handled — would be wrong the first
time any of them changed, and the store's own history in this repo is a reminder that a plausible
wrong answer is worse than none.

#### Two surfaces, three actions, and one deliberate difference from baseline

Baseline's copy is carried verbatim throughout, read out of its webview bundle: the picker title
*Rewind to…*, the empty state *No messages to rewind to yet.*, the hint, the three option labels,
*A new forked conversation will be created after rewinding.*, *The code has not changed, so no code
will be restored.*, the outcome line *Code rewind successful* with its explanation of what a skipped
file means, and the CLI's warning that *Rewinding does not affect files edited manually or via bash*.

The difference: **baseline's picker only ever does "restore code and fork" together**, and keeps the
three-way choice for the per-message `…` menu. This item's own acceptance criterion is that the two
are "independently selectable, from both a picker and a per-message affordance", so a row here is
selected first and then offered the same three actions the menu offers.

A fork on its own writes nothing to the working tree, so it runs immediately. Anything that restores
files stops at the confirmation, which is where the dry run is shown.

#### Verification — 127 checks, 59 of them against a live IDE

| Script | Checks | What it establishes |
|---|---|---|
| `scripts/phase-i-unit.ps1` | 68 | The transcript reader against a **captured real session** (`fixtures/`): two prompts out of four `user` records, newest first, the fork anchor, the first-message case, ordinals, ages in baseline's wording, the outcome sentences. Plus the fork flags on a **real spawned command line** — `Start` spawns the process itself, so the args are read back off the process rather than from a seam in our own code. |
| `scripts/phase-i-live.ps1` | 59 | **A real experimental instance, driven end to end.** The empty state; two real Haiku turns that create and then change a scratch file; the picker listing exactly the two prompts with their ages; the actions disabled until a row is selected; a real dry run naming the real file; *Never mind* leaving the disk untouched; the per-message menu; **the file on disk going back to `ALPHA`**; and a fork that produces a different session id, a trimmed view, a prefilled composer, and a transcript holding the kept turn and not the dropped one. |

**Four harness defects, each found by a check that disagreed with something already proven** — and
each one a lesson worth keeping, because three of them were checks that *passed* when they should
not have:

* *"and it closes" passed against a popup that had never opened.* A WPF `Popup` has no automation
  peer at all, so asking whether `RewindPopup` exists asks about something that never exists.
  Openness is now asked of an element **inside** the popup.
* *"the forked-from turn is gone" passed whether it was there or not.* A user message is rendered by
  the markdown viewer into a FlowDocument, which UIA exposes as a Document with an **empty Name** —
  so a Name sweep is structurally blind to it. `uia-lib.ps1` has carried that warning since Phase D
  and this script still made the mistake; it now reads through `TextPattern`.
* *"the fork has a genuinely different session id" passed against a transcript from an earlier run.*
  The "sessions that existed before" list held `<id>.jsonl` and was compared against bare ids, so it
  excluded nothing. Fixed, and then strengthened: the check now also asserts the forked transcript
  holds the kept turn and not the dropped one, and that the original still holds both.
* *"turn 1 finished" passed instantly*, because `Ready` is also what the status says before anything
  is sent. It now waits for the send button to become the stop button first.

A fifth issue was not a harness defect at all: **retrying a toggle by clicking again just closes
what it opened.** Every surface is now opened by looking first and clicking only if it is shut.

**One real defect found by the live run and fixed:** the picker's rows announced themselves to the
accessibility tree as `TeronClaudeCodeVS.ViewModels.RewindPoint` — a `ListBoxItem` with no
`AutomationProperties.Name` falls back to `ToString()`. A screen reader would have read the type
name and nothing else. `RewindPoint.ToString()` now returns the prompt.

**Not covered, and stated rather than glossed:** the working tree used in the live run was this
repository, so a rewind that had to refuse a path — a symlink, or a file whose directory moved — was
never exercised. `skippedLinks` is surfaced with the CLI's own explanation, but that branch has been
run only through fixtures.

---

### Phase H notes (2026-08-30)

**Verification changed in this phase, and the change applies backwards.** Phases G and H were built
with headless harnesses only — reflection against the built assembly, plus the real CLI driven out
of process. That covered the parsers and the view models and covered them well, but it never put a
single one of these panels on screen. Phase H therefore also carries the pass Phase G should have
had: `phase-h-live.ps1` drives the **MCP and Plugins panels** in a real experimental instance
alongside FEAT-6's own menu. Both were fine — but "were fine" was not something anyone knew before
it was run, and one Phase H defect (below) was invisible to every headless check.

---

#### FEAT-6 — the `+` add menu

**What baseline's menu actually does**, read out of the shipped VS Code extension's webview bundle
(v2.1.251) rather than inferred from the screenshot:

| Entry | Baseline's tooltip | What it does |
|---|---|---|
| Upload from computer | Attach files from your computer | calls the host's attach handler |
| Add context | Add files or folders to the conversation | inserts the literal `@` and lets the mention picker take over |
| Browse the web | Add browser tabs to the conversation | inserts the literal `@browser:` |

Two of the three are reproduced exactly, including the hand-off to the mention picker — that is
genuinely what baseline does, not a convenient reading of it.

**"Browse the web" is the one real divergence, and it is a hard one.** `@browser:` is not a CLI
feature. Its expander calls the *VS Code extension's own* `ensureChromeMcpEnabled()` and
`createNewBrowserTab()`, and the menu entry is gated on `browserIntegrationSupported`, which that
extension defines as `authMethod === "claudeai"`. It is the Claude-in-Chrome integration: a browser
extension plus that host's MCP bridge. There is no flag we can pass the CLI to obtain it, and no
amount of implementation effort on our side reaches it.

What the CLI *does* give every session is `WebFetch` and `WebSearch`. So the entry keeps baseline's
label and position and delivers the same outcome — web content as conversation context — by the
route that exists here: a small box that composes one line of prompt text, a fetch instruction for a
URL and a search instruction for anything else. `WebContextComposer` carries the whole explanation
at the top of the file so a later reader does not "fix" it back toward `@browser:`.

**One behavioural difference from a drag-and-drop, on purpose.** The staging path is shared, but a
file type the CLI cannot be handed is skipped *silently* on a drop (twenty mixed files, baseline's
own behaviour) and *named in the transcript* when it came from the file dialog — a file someone
picked by hand and then saw nothing happen to just looks broken.

---

#### FEAT-7 — automatic model fallback

`--fallback-model <model>` is real, takes a comma-separated chain, and its help says explicitly
"only works with --print" — which is the mode every session here already runs in.

**The audit's attribution was wrong, and the binary says so.** The backlog records the observed
`Switched to claude-haiku-4-5-20251001` as "driven by its *Switch models when a message is flagged*
toggle". That setting is `switchModelsOnFlag`, and the CLI's own description of it is *"When
safeguards flag a message, automatically switch to a different model to keep chatting"* — safeguard
refusals, not usage. The line seen near a weekly limit matches a different subtype entirely, whose
own text reads `Switched to {model} … · {original} requires usage credits · /model to change`. Worth
recording because it changes what had to be built: not one event, four.

**The four subtypes, with their real fields and their real sentences** (schemas and message builders
both read out of the binary):

| Subtype | When | Notes |
|---|---|---|
| `model_fallback` | primary overloaded / not found / blocked / unretryable | `trigger` ∈ {model_not_found, permission_denied, overloaded, server_error, last_resort, model_blocked}; **turn-scoped** — the primary is retried next turn |
| `model_refusal_fallback` | `stop_reason: "refusal"`, retried on the fallback | driven by `switchModelsOnFlag`, **on by default and not ours to set** |
| `model_consent_fallback` | usage-credit boundary, user consented | the subtype behind the line the audit saw |
| `model_refusal_no_fallback` | refusal with nothing to fall back to | no `fallback_model`; the only one that is bad news |

All four carry a finished `content` sentence, so the transcript shows the CLI's own words. All four
are surfaced regardless of our setting — the refusal path is governed by a CLI-side setting we do
not own, so gating our display on our own flag would hide events that still happen.

**The model chip is deliberately not updated** on a switch. Assigning `SelectedModel` restarts the
session, which would discard the very turn the CLI just rescued; and a `model_fallback` switch lasts
one turn, so the chip would be right once and wrong afterwards.

---

#### Verification — 140 checks, of which 54 are against a live IDE

| Script | Checks | What it establishes |
|---|---|---|
| `scripts/phase-h-unit.ps1` | 86 | The composer (URL vs. search, and the narrow host test that keeps `src/Program.cs` from becoming a URL); all four fallback subtypes parsed from lines built out of the binary's own builders; older-CLI lines with no `content`; the "says nothing" line that is dropped; that the flag is emitted only when the toggle is on *and* a model is named, exercised on the real view model rather than re-implemented. |
| `scripts/phase-h-live.ps1` | 36 | **A real experimental instance.** The MCP and Plugins panels open, render, and switch tabs (Phase G's missing pass); the `+` menu shows baseline's three entries; "Browse the web" composes a real fetch line into the real input box; "Add context" inserts `@` **and the mention picker actually opens on it**; "Upload from computer" opens a real file dialog with the real filter, which is then cancelled. |
| `scripts/phase-h-live-fallback.ps1` | 18 | **FEAT-7 end to end, no model call.** Both settings on the real Tools ▸ Options page with the declared defaults; the flag absent from the real spawned `claude.exe` command line with the toggle off; present, with the configured model, after turning it on in the real UI and reloading; and the CLI accepting it — proven by the session reaching "Ready", which only happens after it has parsed its flags and emitted `init`. The setting is restored afterwards. |

No prompt was ever sent to the model, so none of this consumed quota.

**Two harness defects this run, both caught by the rule that a failing check is a hypothesis about
technique before it is a hypothesis about the product:**

* *"No file dialog opened."* Two real "Attach files" dialogs were on screen at the time. A Win32
  common dialog raised by a VS extension is an **owned** window: in the UIA tree it hangs off the VS
  main window, so it is a Descendant of the desktop and never a Child of it. The desktop-Children-
  by-process-id idiom that finds tool windows finds nothing here. Now documented in the script.
* *"The flag is absent with the toggle off"* failed against a session process that had been started
  earlier, while the toggle was on. A running session is evidence about the setting **as it was when
  that session started**, not as it is now; the script reloads the tool window before reading.

**Not covered, and stated rather than glossed:** no `model_fallback` event has been seen arrive from
a live CLI. Producing one needs a real overload, a real refusal, or a real credit boundary, none of
which can be arranged on demand. The parsing is tested against the binary's own schemas and the
notice's rendering path is the one every other system notice already uses; the join between them —
`OnModelFallback` calling `AddSystemNotice` — is three lines and is the only part of FEAT-7 nothing
has executed.

---

## Tier 0 — Correctness (do first; this is a real defect)

| ID | Item | Size | Evidence | Done when |
|---|---|---|---|---|
| **BUG-1** | **"Active File" chip silently fails on Markdown Preview tabs.** Inserts nothing, Claude receives zero context, no error surfaces. Root-caused by switching the active tab to a normal code file, where it works. Almost certainly VS's active-document resolution not treating a Preview tab as a normal document. | S–M | our:16 / our:17 | Chip either inserts the correct underlying file for a Preview tab, **or** reports clearly that it can't. Silent no-op is the actual bug — a visible message is an acceptable fix. |

---

## Tier 1 — Style foundations (cheap, compounding, no backend work)

These are the changes that make the panel *look* deliberate. They're grouped because doing them
together is far cheaper than one at a time — they all touch the same XAML resources.

| ID | Item | Size | Evidence | Done when |
|---|---|---|---|---|
| **ST-1** | **Define a design-token resource dictionary.** Prerequisite for ST-2..ST-5 — one place for the type scale, radii, surfaces, and accent. | S | §7 | A single `ResourceDictionary` holds the scale; the chat control references tokens, not literals. |
| **ST-2** | **Collapse the type scale from nine sizes to two.** Baseline uses exactly **13px body** (line-height 19.5px ≈ 1.5) and **11px chrome**. We currently use nine sizes: 9, 10, 10.5, 11, 11.5, 12, 12.5, 13, 14. | S | §7 | No more than two or three `FontSize` values remain in `ClaudeCodeChatControl.xaml`. |
| **ST-3** | **Normalise corner radii to 5–6px.** Baseline uses 5–6px for controls and cards, 8px only for banner top corners. We use eight values (3, 4, 5, 6, 8, 10, 11, 15). | S | §7 | Radii come from tokens; the long tail (10, 11, 15) is gone. |
| **ST-4** | **Adopt the "one theme-invariant accent" rule.** Measured in *both* themes: baseline re-derives every surface and text colour from the host theme, and keeps exactly one constant brand colour — `#C6613F`, on the send button alone. Verified by flipping the isolated profile to light: body text `#BBBEBF → rgb(59,59,59)`, header `#191A1B → #F8F8F8`, **accent unchanged**. | S | §7, real:20 | Every surface/text brush derives from a VS theme key; exactly one hardcoded brand colour remains. |
| **ST-5** | **Reconsider the solid terracotta user-message bubble.** *The single largest visual divergence.* Baseline user messages have **no bubble**: `background: transparent`, `border-radius: 0`, full column width, `padding: 14px 0 12px`. Ours is `Background=#D97757`, white text, `CornerRadius="10,10,2,10"`, right-aligned, `MaxWidth=460`. Baseline reads as a **document**; ours reads as a **messenger app**. This is the concrete instance of ST-4 — we spend the brand colour on the most-repeated element in the panel. | M | §7 | A decision is made deliberately. Full parity is *not* required — but if the bubble stays, it should be a considered choice, not an inherited default. |

> **Note on ST-5:** this is the one item where "match the baseline" may be the wrong call. A VS
> extension has different visual conventions to a VS Code webview. The audit's job is to make the
> divergence *measured and visible*; the design call is yours.

---

## Tier 2 — High-value UX polish (small items, immediately felt)

| ID | Item | Size | Evidence | Done when |
|---|---|---|---|---|
| **UX-1** | **Model-picker descriptions.** Baseline gives each model a decision-support subtitle: Opus *"~2× usage vs Sonnet"*, Fable *"Requires usage credits"*, Haiku *"Fastest for quick answers"*, Default *"(recommended)"*. Ours is a bare name list — the user cannot see the cost implication of switching. | S | real:09 | Each of our five models shows a one-line subtitle; cost/credit implications are visible. |
| **UX-2** | **Permission-picker descriptions + `⇧+Tab` cycling.** Baseline shows a "Modes" header, a one-line description per mode, and a `⇧ + tab to switch` hint — and the cycle is **confirmed working**: Manual → Edit automatically → Plan → Manual. We expose *more* modes (5 vs 3) but explain none, which makes the extras harder to use, not easier. Shift+Tab is **absent from our source** (verified by search). | S–M | real:10, §6c | All five modes carry a description; Shift+Tab cycles them; the hint is visible in the UI. |
| **UX-3** | **Numbered permission cards + inline redirect box.** Baseline: `1 Yes` / `2 Yes, allow all edits this session` / `3 No`, keyboard-selectable, states the **full absolute path**, shows `Esc to cancel`, and offers *"Tell Claude what to do instead"* inline. Ours: unnumbered `Allow`/`Allow for Session`/`Deny`, no redirect box, no keyboard selection. | M | real:02, real:12 | Options are number-keyed, the full path is shown, and the user can redirect Claude without leaving the card. |
| **UX-4** | **Palette "Filter actions…" search box.** Baseline's menu opens with a filter field. Ours lists ~50 commands with no filter. | S | real:08 | Typing filters the palette live. |
| **UX-5** | **Sort the slash-command list alphabetically.** Baseline is A→Z (52 commands). Ours is in skill/source order, which reads as arbitrary. | XS | real:08 | List is sorted. |
| **UX-6** | **Keyboard-affordance footers.** Baseline puts `↑↓ to navigate · Enter to select · Esc to close` in its pickers, `ctrl esc to focus or unfocus Claude` in the input placeholder, and `Tap or hold to record · Ctrl+D` on the mic. Ours advertises no shortcuts anywhere. | S | real:16, §6b | Pickers show their key hints; the input placeholder names its focus shortcut. |
| **UX-7** | **Annotate collapsed tool calls with count + failure state.** Baseline's collapsed line reads `1 tool call ⌄` / `1 tool call · 1 failed ⌄`. **We already collapse by default** (this was mis-recorded earlier as a gap) — only the annotation is missing. | S | §7 | Collapsed rows show a grouped count and surface failures without expanding. |
| **UX-8** | **Per-code-block "Copy code" button.** Baseline puts copy on each block; we only have a global Copy Raw Output. | S | §6b | Each rendered code block has its own copy affordance. |
| **UX-9** | **Type-aware attachment chips.** Baseline's pending chips show a thumbnail + pixel dimensions for images (`test-paste.png 1×1`) and a file icon for text/code. We already stage attachments — this is presentation only. | S | real:19, real:22 | Chips differentiate images from files and show dimensions for images. |
| **UX-10** | **Show the extension version in the menu footer.** Baseline prints `v2.1.250`. Useful for bug reports. | XS | real:08 | Version string is visible in the palette footer. |
| **UX-11** | **Design an empty state.** Baseline's new-session view has a wordmark, a terracotta pixel-art robot mascot, a rotating tip, and a dismissible *"Prefer the Terminal experience?"* hint. Ours starts blank. | S–M | real:20 | New sessions render a designed empty state rather than an empty list. |
| **UX-12** | **Modal-card visual language.** Baseline's Plugins/Rewind cards use a dimmed backdrop, title + close X, centred empty-state text, and a tab strip where relevant — they read as deliberate modals rather than bare dropdowns. | M | real:15, real:16 | Our popups share one modal style consistent with ST-1's tokens. |

---

## Tier 3 — Cheap gap-closers (five checklist items, no GUI needed)

The audit's most useful structural finding: **five of baseline's seven "Customize" items are not
GUIs at all.** Memory, Agents, Hooks, Output styles and Permissions each render an in-chat
hand-off card — *"Continue in Terminal to …?"* with `1 Continue in Terminal` / `2 Never mind`,
plus one sentence explaining that the setting syncs back to the IDE.

| ID | Item | Size | Evidence | Done when |
|---|---|---|---|---|
| **GAP-1** | **Terminal hand-off cards** for Memory, Agents, Hooks, Output styles, Permissions. This is how baseline itself "supports" them. Closes five gaps with one small pattern. | S–M total | real:13 | Each of the five menu entries offers to open the CLI in a VS terminal, with baseline's explanatory wording. |
| **GAP-2** | **Open Claude in Terminal.** Prerequisite for GAP-1 and useful alone. | S | real:08 | Launches the CLI in a VS terminal in the right working directory. |
| **GAP-3** | **Missing slash commands** — `/btw`, `/feedback`, `/remote-control` are the *only* three of baseline's 52 we lack. | S | real:08 | Verify whether these are CLI-provided (then it's a passthrough issue) or extension-injected (then implement). |

---

## Tier 4 — Real features (genuine build work)

| ID | Item | Size | Evidence | Done when |
|---|---|---|---|---|
| **FEAT-1** | **Rewind / per-message actions.** *The largest genuine feature gap.* Baseline exposes this **two** ways: a modal "Rewind to…" picker listing prior user messages with relative timestamps, **and** a per-message `…` menu offering a **three-way** choice — *Fork conversation from here* · *Rewind code to here* · *Fork conversation and rewind code*. The important design insight: baseline treats **conversation forking and code restoration as separate concerns**, not one "undo". | XL | real:16, real:19 | Restoring code and forking the conversation are independently selectable, from both a picker and a per-message affordance. |
| **FEAT-2** | **Native side-by-side diff tab.** Baseline opens a real editor tab titled `[Claude Code] <path>` with **accept / revert / next-change / prev-change / swap-sides** toolbar buttons, *in addition to* the inline chat diff. We open no separate tab. (An earlier matrix entry wrongly called this "N/A by design" — retracted.) | L | real:12 | An edit opens a real VS diff tab with accept/reject controls alongside the inline card. |
| **FEAT-3** | **Session-history parity**: generated short titles (baseline shows "Watermelon"/"Pineapple", we show the truncated raw first message) and **per-row rename/delete** actions. Search already matches. | M | real:11 | Rows carry generated titles plus inline rename and delete. |
| **FEAT-4** | **MCP servers panel.** One of only two real GUI panels in baseline's Customize section: a titled list with the empty state *"No MCP servers configured. Use `claude mcp add` to add servers."* + a "Learn more" link. | M | real:14 | A panel lists configured servers with that empty state. |
| **FEAT-5** | **Manage plugins panel.** The other real GUI panel: a modal with a **Plugins / Marketplaces** tab strip and the empty state *"No plugins available. Add a marketplace to discover plugins."* | M–L | real:15 | Panel with both tabs and the empty state. |
| **FEAT-6** | **`+` Add menu** — *Upload from computer*, *Add context*, **Browse the web**. Web browsing as a first-class context action has no equivalent on our side at all. | M (menu) / L (web) | real:18 | An add-menu exists; web browsing is scoped separately as its own decision. |
| **FEAT-7** | **Auto model-downgrade on usage limits.** Baseline was **observed doing this live**, printing `Switched to claude-haiku-4-5-20251001` mid-session near the weekly limit, driven by its "Switch models when a message is flagged" toggle. | M | real:07 | A toggle exists and the switch is announced in-transcript. |
| **FEAT-8** | **Voice dictation.** Baseline has a mic with a real keybinding (`Ctrl+D`, "Tap or hold to record"). | L | §6b | — |
| **FEAT-9** | **Cloud / remote sessions (History → Web).** Baseline syncs sessions to the account and lists them by generated machine name (`kaloyan-pc-wild-wozniak`, …) with relative age, so a session started on a phone or another PC resumes in the IDE. Paired with Remote Control and its persistent banner. | XL | real:21, real:07 | Out of scope for a near-term plan; recorded so it isn't "missed". |

---

## Tier 5 — Test debt (not features; closes holes in this audit)

| ID | Item | Size | Evidence | Done when |
|---|---|---|---|---|
| **TEST-1** | **Drive paste + drag-and-drop on *our* side.** The **baseline** side is now done, but our WPF path genuinely goes through OLE `IDropTarget`/`DoDragDrop` and the real clipboard command — the webview techniques don't reach it. Next attempt: the `WM_DROPFILES` / clipboard-format equivalent of the existing `Send-WmChar`/`Send-WmClick` helpers. | M | §6c | Both are driven live on our side, or proven genuinely unreachable. |
| **TEST-2** | **IDE MCP tools beyond `getDiagnostics`** — open editors / selection / dirty-state / save remain transport-tested only, never driven against real VS SDK objects. | M | matrix §2 | Each is exercised against a live VS instance. |
| **TEST-3** | **Message actions menu** — now opened and documented, but only via a *real* hover (`Input.dispatchMouseEvent`); worth folding that technique into the reusable scripts. | S | §6c | `cdp-lib.ps1` gains a real-hover helper. |

---

## Suggested sequencing

1. **BUG-1** alone — it's a real defect and independent of everything else.
2. **ST-1 → ST-2 → ST-3 → ST-4** as one batch. ST-1 is the prerequisite; done together they're a
   single pass over the XAML resources rather than four.
3. **Tier 2 UX items**, cheapest first (UX-5, UX-10, UX-1, UX-7 are all trivial once ST-1 exists).
4. **GAP-2 → GAP-1** — one small pattern closes five checklist gaps.
5. **ST-5** as a deliberate design decision, once the token work makes the alternative easy to try.
6. **FEAT-2** (diff tab) before **FEAT-1** (rewind) — smaller, and both touch edit history.
7. Everything else as its own scoped phase.

**Deliberately excluded** (already triaged as out of scope in Phase 0/5, not oversights): multiple
simultaneous sessions, session groups, side-question panel, response rating, onboarding
walkthrough, git worktree UI, browser/debugger/Jupyter MCP integrations.
