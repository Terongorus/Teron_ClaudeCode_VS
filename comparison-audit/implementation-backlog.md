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
