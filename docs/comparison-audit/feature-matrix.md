# Feature comparison matrix — official Claude Code baseline vs. Teron_ClaudeCode_VS

**Methodology**: the **official `anthropic.claude-code` extension for Microsoft VS Code** is the
baseline. It's built by Anthropic; its behavior is the reference standard. Every row below
**documents how the official extension actually behaves first** (live-driven, not guessed), and
then states whether **our** extension (Teron_ClaudeCode_VS, running inside Visual Studio) matches,
differs from, falls short of, or has no equivalent to that reference behavior. This is not a
side-by-side of two peers — it's "here is the standard, here is how close we are to it."

Full narrative, tooling notes, and bugs found:
[`../docs/Phase 6 - Live Comparison Audit.md`](../docs/Phase%206%20-%20Live%20Comparison%20Audit.md).
Screenshots: `screenshots/real-extension/` (baseline evidence) and `screenshots/our-extension/`
(comparison evidence).

Legend — **Verdict**: ✅ matches the baseline, live-proven on both sides · 🔶 present but differs
in style/behavior from the baseline (not wrong, just different — noted) · 🐛 present but broken —
a real bug found against the baseline · ⬜ present in ours, not yet live-driven against the
baseline · ❌ baseline has this, we don't · ➖ we have this, baseline doesn't (novel, not a gap).

## 1. Core session & CLI plumbing

| Feature | Baseline (official, documented) | Ours, checked against baseline | Verdict |
|---|---|---|---|
| Model selector | A pill/menu item showing the current model (e.g. "Sonnet 5"), inside the combined command palette. | Dedicated chip button + checkmark-list popup. Same end result (pick a model), different chrome. | 🔶 |
| Permission-mode selector | A pill showing current mode (`Manual`/`Edit automatically`/`Plan`/`Auto`), click-to-cycle or Shift+Tab; a small "Modes" quick-picker lists all four with descriptions (screenshot: real:04). | Dedicated chip + checkmark-list popup, same four+ modes plus `CLI Default`/`Don't Ask`/`Bypass Permissions`. | 🔶 |
| Effort/thinking level | A **5-dot slider** control inside the palette, plus separate "Thinking" and "Switch models when a message is flagged" toggle switches (screenshot: real:04). | Checkmark list (Low/Medium/High/...); thinking folded into the effort choice, no separate toggle; no safety-flag auto-switch. | 🔶 / ❌ (auto-switch specifically) |
| `@`-mention file picker | Typing `@` opens a live-filtered project file list inline in the input; empty workspace correctly shows "No files found" (screenshot: real:05). | **LIVE-CONFIRMED, matches.** Typing `@` opens the same kind of live file list (screenshot: our:20), selecting an entry inserts `@Class1.cs` correctly (our:21). Initial test attempts failed for a caret-position automation reason unrelated to the product — resolved by simulating a real keystroke instead of a bulk text-replace. | ✅ |
| Message queueing while busy | Input stays live while a turn is in flight; a message sent mid-turn is accepted and processed after the current turn finishes. | **LIVE-CONFIRMED, matches**, rigorously this time: second message sent while `StopButton` was directly confirmed visible (genuinely mid-turn, not just "sent quickly"), both completed correctly in order (our:19). | ✅ |
| `/compact` | Slash command; produces a "compacted" system message with tokens-freed count. | Matches exactly: `Compacted chat · manual · 32.7k tokens freed` (our:11). | ✅ |
| Retry-notice on unexpected CLI exit | **BASELINE TESTED DIRECTLY** — same experiment, same conditions: the isolated instance's own `claude.exe` (PID strictly verified by parent chain) was force-killed while idle, then a message was sent. Baseline **silently respawned the CLI and answered normally** — no error text, no "Try again" prompt, no reconnect notice. Respawn proven by process identity: old PID 37552 gone, new PID 16120 created at 22:10:19, the same second the message was sent. | Killing the CLI subprocess *while idle* triggers the **same silent, transparent respawn** on the next send (our:10). | ✅ (behaviour matches baseline exactly — previously logged as an open question, now resolved as parity, not a gap) |
| `-c`/`--continue`, `--dangerously-skip-permissions`, `--setting-sources` UI | Real CLI-flag-backed features. | Deliberately not duplicated (superseded by our own resume UI / `bypassPermissions` mode / not yet built). | ❌ (by design for the first two, real gap for the third) |

## 2. IDE companion / diagnostics integration

| Feature | Baseline (official, documented) | Ours, checked against baseline | Verdict |
|---|---|---|---|
| `getDiagnostics` (Problems panel bridge) | Real MCP tool, pulls editor diagnostics into context. | Live-verified in an earlier phase against VS's Error List (real 7-error test file). | ✅ |
| `openDiff` — inline diff | **BASELINE CAPTURED** (real:12): the chat shows an `Edit calc.py` block with an `Added 1 line` summary and a syntax-highlighted inline diff, the added line highlighted green in context. | A real line-level diff (single green `+` line, not a raw dump) renders inline inside the "Allow Edit file?" permission card (our:12/13). | ✅ (both show a real inline diff; presentation differs only cosmetically) |
| `openDiff` — separate native diff tab | **CORRECTION — an earlier claim in this matrix was wrong.** Baseline **does** open a separate **native side-by-side diff editor tab** in the main editor area, titled `[Claude Code] <path>`, showing original vs. modified with the added line highlighted, and carrying **accept / revert / next-change / prev-change / swap-sides toolbar buttons** in the editor tab itself (real:12). It does this *in addition to* the inline chat diff, not instead of it. The previous entry ("N/A — official's flow is inline-in-editor by design") was an unverified assumption and is retracted. | Confirmed via direct tab-list inspection that **no separate diff tab opens** — only the inline card diff. | ❌ (real gap, not "not applicable" as previously recorded) |
| Built-in tool permission cards | Numbered options + inline "Tell Claude what to do instead" text box + an `Esc to cancel` hint, all in one card. For a file edit the wording is `Make this edit to <full absolute path>?` with `1 Yes` / `2 Yes, allow all edits this session` / `3 No` (real:12); for a command it's the `2 Yes for this project` variant (real:02). | Unnumbered `Allow` / `Allow for Session` / `Deny`, no inline redirect box, no keyboard-number selection, no Esc hint. | 🔶 |
| `AskUserQuestion` — single-select | Real radio-button UI (same underlying CLI tool as ours). | **LIVE-CONFIRMED**: real `RadioButton` list, correct enforcement, correct follow-up (our:15). | ✅ |
| `AskUserQuestion` — multi-select | Real checkbox UI (same underlying CLI tool). | **LIVE-CONFIRMED**: real checkboxes with descriptions, full submit round-trip, correct follow-up (our:02/03). | ✅ |
| Active File / Selection context attachment | Part of official's broader `@`-mention system (also covers browser tabs as a context source). | **A real, reproducible bug found**: the "Active File" chip silently inserts nothing — and Claude gets zero context — when the active VS tab is a Markdown **Preview** tab. Confirmed root cause by switching to a normal code tab, where it worked correctly (`@Class1.cs` inserted, Claude correctly named the file) (our:16/17). Not yet fixed. | 🐛 |
| Open editors / selection / dirty-state / save MCP tools | Real tools backing the above. | Transport-layer tested via fake handlers in an earlier phase; not confirmed against real VS SDK objects in a live pass. | ⬜ |

## 3. Chat UX — attachments, transcript, status

| Feature | Baseline (official, documented) | Ours, checked against baseline | Verdict |
|---|---|---|---|
| Paste screenshot (clipboard image) | **BASELINE LIVE-DRIVEN** (real:22) — accepted a synthetic `ClipboardEvent` carrying a real `File`, staging a pending chip showing **filename + pixel dimensions** (`test-paste.png` `1×1`). | Implemented, build-clean. **Our side still not live-driven**: WPF's paste path goes through the real OS clipboard command (`DataObject.Pasting`), which the webview technique above does not exercise. | ⬜ (baseline now documented; our side still owed — see §6c) |
| Drag-and-drop (image/text/PDF) | **BASELINE LIVE-DRIVEN** (real:19) — accepted a synthetic `DragEvent` sequence with a `File` in `DataTransfer`. Chips are **type-aware**: image → thumbnail + dimensions, `.py` → file icon, no dimensions. | Implemented, build-clean. **Our side still not live-driven**: WPF drop genuinely goes through OLE `IDropTarget`/`DoDragDrop` COM interop, which the webview technique does not reach. | ⬜ (baseline now documented; our side still owed — see §6c) |
| Transcript view modes (Summary/Normal/Thinking/Verbose) | No equivalent concept exists. | **LIVE-CONFIRMED** working correctly — Verbose reveals raw tool output + auto-expands Thinking, re-collapse to Normal also confirmed (our:04). | ➖ (novel, working) |
| Live status line (elapsed/tokens/running-task count) | No persistent status line exists. | **LIVE-CONFIRMED**, including an undocumented nicety: shows "Claude has a question — see chat" instead of elapsed/tokens while a question is pending. | ➖ (novel, working) |
| Running-tasks panel | No user-facing equivalent — only an internal `background_tasks`/`stop_task` protocol handshake. | Tool-call card confirmed rendering correctly; the panel's mid-flight population specifically wasn't caught in a screenshot (task completed in under 4s both times it was tried). | ➖ / ⬜ (novel feature, partially proven) |
| Tool-call card | **Corrected in §7** — baseline is *not* always-visible. It **collapses by default** to a plain `1 tool call ⌄` line (with `· 1 failed` when relevant) and expands into an `IN:`/`OUT:` box (real:03). | Collapsed `Expander` — click to reveal detail (our:05). **Same collapse-by-default model as baseline.** Gap is only the grouped count + failure annotation on the collapsed line. | ✅ (closer than earlier passes recorded) |
| Command/settings menu architecture | **One combined palette** ("/") — full contents dumped live from baseline v2.1.250 (real:04, real:08): **Context** (Attach file…, Mention file from this project…, Clear conversation, Rewind) · **Model** (Switch model…, Thinking, Switch models when a message is flagged) · Account & usage… · **Customize** (Output styles, Agents, Hooks, Memory, Permissions, MCP servers, Manage plugins) · Open Claude in Terminal · **Settings** (Switch account, General config…, Enable Remote Control for all sessions, Focus view) · **Slash Commands** · **Support** (View help docs, Report a problem, version string). A *separate*, smaller "Modes" quick-picker also exists off the mode pill. Menu opens with a **"Filter actions…" search box** at the top, and renders Thinking / auto-switch as real **toggle switches** with the current model shown inline as a right-aligned value ("Haiku"). | **Palette contents dumped live** (our:22): `THIS SESSION` (turns · cost · in/out tokens) → `Account & Usage →` → `COMMANDS`. That's the whole menu — the Context/Customize/Settings/Support sections have **no equivalent**. Model/Permission/Effort/Transcript are instead four always-visible chips (state readable without opening anything, which baseline can't do); Attach-file/Mention are instead the Active File/Selection chips + `@`. | 🔶 (different architecture; several baseline sections genuinely absent — itemized below) |
| Slash-command list contents | 52 commands, **alphabetically sorted**, incl. `/btw`, `/feedback`, `/remote-control` (real:08). | 50 commands, **not sorted** (skill/source order). Diff vs baseline is exactly three missing: **`/btw`, `/feedback`, `/remote-control`**. Every other command matches. | 🔶 (3 missing + unsorted ordering) |
| Model picker | "Select a model" header; **five** options each with a descriptive subtitle carrying real cost/capability guidance — Default *(recommended)*, Sonnet, Fable *(“Requires usage credits”)*, Opus *(“~2× usage vs Sonnet”)*, Haiku *(“Fastest for quick answers”)* (real:09). | **Same five models**, correct current-selection checkmark — but a bare name-only list: no header, **no descriptions, no cost/usage hints**, and a different order (Default, Sonnet, Opus, Haiku, Fable) (our:23). | 🔶 (model set matches; guidance text absent) |
| Permission-mode picker | "Modes" header + **`⇧ + tab to switch` keyboard-cycling hint**; **three** modes, each with a one-line description: Manual, Edit automatically, Plan (real:10). | **Five** modes — Manual, Accept Edits, Plan Mode, Auto (background safety checks), Bypass Permissions — i.e. a superset of baseline's three, but **name-only with no descriptions**, and **no Shift+Tab cycling** (verified absent by source search, not just by observation) (our:24). | 🔶 + ➖ (more modes than baseline; missing descriptions and the Shift+Tab shortcut) |
| Response rating (thumbs up/down, star survey) | Real feature. | Not implemented. | ❌ |
| Voice dictation (mic button) | Real feature. | Not implemented. | ❌ |
| "Side question" panel | Real feature — secondary lightweight chat. | Not implemented. | ❌ |
| Onboarding checklist / walkthrough | Real feature. | Not implemented — arguably not applicable, since a VS extension inherits VS's own onboarding. | ❌ (by design) |

## 4. Plan Mode

| Feature | Baseline (official, documented) | Ours, checked against baseline | Verdict |
|---|---|---|---|
| Plan review card | Own plan review card (accept/manual-approve/keep-planning equivalent). | Opens as a real native VS editor tab; "Accept this plan?" card with the same three choices. Live-verified in Phase 4 (multiple real bugs found and fixed there — see that doc). | ✅ (parity confirmed in an earlier phase, not re-driven this pass) |
| Inline comment-on-selection → feedback flow | Not confirmed to exist in official. | Original design on our side. | ➖ |
| Native rendered Markdown preview for plan tab | N/A. | Attempted, reverted — crashed VS with an `AccessViolationException`. Documented, not a hidden gap. | ❌ (known, deliberate) |

## 5. Account, usage, and session management

| Feature | Baseline (official, documented) | Ours, checked against baseline | Verdict |
|---|---|---|---|
| Account & Usage view | **BASELINE DOCUMENTED under controlled conditions** (real:06 — captured only after a real message round-trip in a fresh session, real:07, so the usage counters had genuine activity to report). Reachable via `/usage` or the combined palette's "Account & usage…" row. Card shows **ACCOUNT** — Auth method, Email, **Organization**, Plan — and **USAGE** — **Session (5hr)** % with reset countdown, **Weekly (7 day)** % with reset countdown, and a "Manage usage on claude.ai" link. Card ends there: **no per-session turn/cost/token breakdown**. A persistent banner outside the card mirrors the weekly %. | **LIVE-CONFIRMED** (our:18). Matches baseline field-for-field on ACCOUNT (incl. Organization) and on both USAGE bars. Adds one block baseline has no equivalent for: **THIS SESSION** — turn count, session cost, in/out token totals. | ✅ + ➖ (matches baseline; adds per-session cost/token block) |
| Session history / resume | **BASELINE DOCUMENTED LIVE** (real:11): panel opens with a **Local / Web tab switch** (Web = cloud/remote-control sessions), a "Search sessions…" box, and rows showing a **short auto-generated session title** ("Watermelon", "Pineapple") + relative age, each row carrying **inline rename (pencil) and delete (trash) actions**. | Flat list, real prior sessions, working "Search sessions…" box — search matches baseline (our:25). Differences: **no Local/Web tabs** (no cloud-session concept), row titles are the **truncated raw first message** rather than a generated short title, and there are **no per-row rename/delete actions**. | 🔶 (search matches; missing cloud tab, generated titles, and row-level rename/delete) |
| Multiple simultaneously-open sessions | Real feature. | Deliberately not built — considered and explicitly rejected as a Desktop-shaped feature this extension's architecture doesn't need (see Phase 5). | ❌ (by design) |
| Rewind (restore code to an earlier message) | Real feature, appears in the combined palette. | Not implemented. | ❌ |
| Git worktree creation from the UI | Real feature. | Not implemented. | ❌ |

## 6. MCP & external integrations

| Feature | Baseline (official, documented) | Ours, checked against baseline | Verdict |
|---|---|---|---|
| MCP config via CLI flags | Real, native CLI flags. | Supported via Options page. | ✅ |
| MCP server management UI | **Confirmed live** in baseline's menu: Customize → "MCP servers" (real:08). | Not implemented. | ❌ |
| Browser / debugger / Jupyter MCP integrations | Real features. | Not implemented. | ❌ |
| Plugin management / marketplace | **Confirmed live**: Customize → "Manage plugins" (real:08). | Not implemented. | ❌ |
| Remote-control / cloud sessions | **Confirmed live and active**: Settings → "Enable Remote Control for all sessions", plus a persistent in-chat banner "Remote Control is active · Continue here, on your phone, or at claude.ai/code" (real:07). Also exposed as `/remote-control`. | Not implemented. | ❌ |

### Baseline menu sections with no equivalent on our side

**Every row below was opened and driven live in the baseline extension**, not read off a menu label — so what each one actually *does* is documented, which materially changes how expensive each is for us to match.

A key discovery from doing this: **five of the seven "Customize" items are not GUI features at all.** They render an in-chat hand-off card — *"Continue in Terminal to …?"* with `1 Continue in Terminal` / `2 Never mind` and a sentence explaining that the setting syncs back to the IDE (real:13). Matching those is cheap; only two Customize items are real GUI panels.

| Baseline menu item | What it actually does (driven live) | Ours |
|---|---|---|
| **Rewind** | **Real, full feature** (real:16): modal "Rewind to…" picker — *"Select a message to restore code and fork the conversation from that point."* Lists prior user messages with relative timestamps, with a `↑↓ to navigate · Enter to select · Esc to close` footer. | ❌ not implemented — **the single largest genuine feature gap found** |
| Clear conversation | Clears the current conversation. | 🔶 closest is the `✚` New Session button |
| Switch models when a message is flagged | Toggle switch. Baseline was **observed doing this live**, printing `Switched to claude-haiku-4-5-20251001` mid-session near the usage limit (real:07). | ❌ |
| Output styles | **Terminal hand-off card** — "Output style is set via `/config`. After changing it in Terminal and reloading this extension, you'll be able to use it here." | ❌ (cheap to match — it's a prompt, not a GUI) |
| Agents | **Terminal hand-off card** — "Once agents are configured in Terminal, you can reload this extension and ask Claude to use them here." | ❌ (cheap to match) |
| Hooks | **Terminal hand-off card** — "Once hooks are configured in this repository, they'll be active in your IDE, too." | ❌ (cheap to match) |
| Memory | **Terminal hand-off card** — "Once configured, memories will be picked up by Claude Code here in your IDE." (real:13) | ❌ (cheap to match) |
| Permissions | **Terminal hand-off card** — "Permission settings are shared between Terminal and this IDE." | 🔶 permission *mode* chip exists; no rules editor — but baseline has no rules GUI either |
| MCP servers | **Real GUI panel** (real:14): titled list with empty state *"No MCP servers configured. Use `claude mcp add` to add servers."* + "Learn more about MCP" link. | ❌ |
| Manage plugins | **Real GUI panel** (real:15): modal with **Plugins / Marketplaces tab strip**, empty state *"No plugins available. Add a marketplace to discover plugins."* | ❌ |
| Attach file… | Opens a native VS Code file dialog (nothing rendered in-webview). | 🔶 Active File / Selection chips cover the common case |
| Mention file from this project… | **Just inserts `@` into the input** and opens the same file picker as typing `@` — not a separate mechanism. | ✅ we have the `@` picker (live-confirmed) |
| Open Claude in Terminal | Launches the CLI in a VS Code terminal. | ❌ |
| Switch account | Account switch flow (**not driven — deliberately skipped to avoid signing the shared account out**). | ❌ |
| General config… | Runs `/config`, printing the full config key list into the chat (`verbose=true\|false`, `workflows=true\|false`, `worktreeBaseRef=fresh\|head`, ~30 more) (real:17). Not a settings GUI. | 🔶 VS Options page is arguably nicer than baseline here |
| Enable Remote Control for all sessions | Toggle; when on, a persistent chat banner reads *"Remote Control is active · Continue here, on your phone, or at claude.ai/code"*. | ❌ |
| Focus view | **Corrected** — a **real persisted setting**: clicking it wrote `"claudeCode.focusView": true` into the profile's `settings.json` (no visible change *in this layout*, which is not the same as a no-op). | ❌ |
| View help docs / Report a problem | Support links. | ❌ |
| Version string in menu | Baseline prints `v2.1.250` at the menu foot. | ❌ |

## 6b. Input-area affordances and message actions (baseline, driven live)

| Baseline control | What it does | Ours |
|---|---|---|
| **`+` "Add" menu** | Three entries with icons (real:18): **Upload from computer**, **Add context**, **Browse the web**. | 🔶 Active File / Selection chips cover "add context"; **no upload picker and no web-browsing entry at all** |
| **Browse the web** | Baseline exposes web browsing as a first-class attach/context action. | ❌ |
| **Voice dictation** (mic) | Real control with tooltip **"Tap or hold to record · `Ctrl+D`"** — a dedicated keybinding. | ❌ |
| **Message actions** (per-message `…`) | **NOW CAPTURED** (real:19) — the menu is hover-gated, so it only opens under a *real* `Input.dispatchMouseEvent` hover, not a synthetic `.click()`. Contents are a **three-way per-message choice**: **Fork conversation from here** · **Rewind code to here** · **Fork conversation and rewind code**. Present on every message. | ❌ no per-message action affordance at all — and note this makes Rewind finer-grained than the menu-level "Rewind to…" suggested: baseline separates *conversation forking* from *code restoration* |
| **Copy code** button | Per-code-block copy, `aria-label="Copy code to clipboard"`. | ❌ (we have a global Copy Raw Output, not per-block) |
| **Dismiss warning / Disconnect Remote Control** | Banners carry their own inline dismiss/disconnect buttons. | ➖ n/a |
| Input placeholder | Doubles as a keyboard hint: *"ctrl esc to focus or unfocus Claude"*. | 🔶 ours is a plain prompt with no shortcut hint |

## 6c. Limit-testing pass — cases reached with non-obvious techniques

Everything here was previously either untested or written off as untestable. Each was reached by
changing technique rather than by concluding the feature was absent.

| Case | Technique that unlocked it | Result |
|---|---|---|
| **Hover-gated Message actions** | Real `Input.dispatchMouseEvent` `mouseMoved` at page coordinates (iframe offset + in-frame rect), instead of `element.click()` | Opened. Three-way menu documented (real:19) |
| **Paste an image** — previously logged as "automation gap, cannot be driven" | Synthetic `ClipboardEvent("paste")` carrying a real `File` in a `DataTransfer` | **Works.** Baseline stages a pending chip showing **filename + pixel dimensions** (`test-paste.png` `1×1`) (real:22) |
| **Drag-and-drop a file** — previously logged as "needs OLE `IDropTarget`, unreachable" | Synthetic `DragEvent` sequence (`dragenter`→`dragover`→`drop`) with a `File` in `DataTransfer` | **Works.** Chip is **type-aware**: image chips carry a thumbnail + dimensions, a `.py` chip carries a file icon and no dimensions (real:19) |
| **Shift+Tab mode cycling** | `Input.dispatchKeyEvent` with `modifiers: 8` (Shift) | **Confirmed working**: Manual → Edit automatically → Plan → Manual, a closed three-way cycle |
| **Light-theme behaviour** | Wrote `workbench.colorTheme` into the isolated profile's `settings.json` + window reload | Panel adapts fully; **the terracotta accent does not** (see §7) |
| **Empty state** | Fell out of the post-reload fresh session | Wordmark + **terracotta pixel-art robot mascot** + a rotating tip ("Use Claude Code in the terminal to configure MCP servers. They'll work here, too!") + a dismissible *"Prefer the Terminal experience? Switch back in Settings."* hint (real:20) |
| **History → Web tab** | Clicking the tab (it is a `button`, not a `[role=tab]`) | **Cloud session sync.** Lists sessions from other machines by generated machine name (`kaloyan-pc-wild-wozniak`, `kaloyan-pc-glistening-gosling`, …) with relative age, so a session started on another device or the phone can be resumed here (real:21) |
| **`theme=` config key** | `/config theme=light` | **CLI-side only** — the webview did not change. Baseline's 7 theme variants (incl. **daltonized** colour-blind and **ANSI** variants) apply to the terminal renderer, not the IDE panel |
| **Focus view** | Inspecting the isolated profile's `settings.json` after clicking it | It is a **real persisted setting**, `claudeCode.focusView: true` — not a no-op as the earlier pass recorded |

## 7. UI style comparison (measured, not eyeballed)

Baseline values below are **computed styles read out of the live DOM**; ours are read out of
`Core/ClaudeCodeChatControl.xaml`. This is the part of the audit that was previously missing.

| Aspect | Baseline (measured) | Ours (from XAML) | Read |
|---|---|---|---|
| **User message** | **No bubble at all** — `background: transparent`, `border-radius: 0`, full column width (486px), `padding: 14px 0 12px`, 4px bottom margin, inherits theme foreground | **Solid terracotta bubble** — `Background=#D97757`, `Foreground=White`, `CornerRadius="10,10,2,10"`, `Padding="10,6"`, `Margin="48,4,4,4"`, right-aligned, `MaxWidth=460` | 🔶 **The single largest visual divergence.** Baseline reads as a *document*; ours reads as a *messenger app*. |
| **Accent usage** | Terracotta `#C6613F` used **only** on the ~26px send button | `#D97757` used as a **large fill** behind every user message | Baseline treats the accent as a rare highlight; we use it as a dominant surface |
| **Body font** | 13px, VS Code's own UI stack, `line-height: 19.5px` (1.5) | Mostly 11 / 11.5 / 12px | Ours is noticeably smaller and denser than baseline |
| **Font-size variety** | Effectively **two**: 13px body, 11.05px small chrome | **Nine** distinct sizes (9, 10, 10.5, 11, 11.5, 12, 12.5, 13, 14) | Baseline has a strict type scale; ours is ad-hoc |
| **Corner radii** | 5–6px for controls/cards, 8px top-corners on banners | **Eight** distinct radii (3, 4, 5, 6, 8, 10, 11, 15) | Same story — baseline is consistent, ours is not |
| **Tool-call card** | **Two states** (corrected): *collapsed* is a plain unboxed line — `1 tool call ⌄`, transparent background, `radius: 0`, 13px, with a failure annotation when relevant (`1 tool call · 1 failed ⌄`); *expanded* is a `#191A1B` card with a `1px #2A2B2C` border at `radius: 6px`, `padding: 4px 6px`, 13px sans (not monospace) | Collapsed `Expander` | ✅ closer than first recorded — **both collapse by default.** Baseline's edge is the *grouped count with failure annotation* on the collapsed line; ours names a single tool |
| **Assistant turn layout** | Bulleted timeline: each step (thinking, tool call, prose) is a row with a small leading status dot, italic muted text for interruptions ("Tool interrupted") | Message blocks | 🔶 baseline reads as a step-by-step activity log |
| **Theme adaptation** | **Measured under a real light theme**: body text `#BBBEBF` → `rgb(59,59,59)`, header `#191A1B` → `#F8F8F8`, i.e. every surface/text token derives from the host theme — **but the send button stays `#C6613F` in both themes** | User bubble is a hardcoded `#D97757` fill regardless of theme | 🔶 Baseline's rule is *"all surfaces theme-derived, exactly one theme-invariant brand accent"*. Ours applies a hardcoded accent to the largest repeated surface, which is the inverse of that rule |
| **Empty state** | Wordmark + terracotta pixel-art robot mascot + rotating tip + dismissible "Prefer the Terminal experience?" hint (real:20) | — | ❌ we have no designed empty state |
| **Code blocks** | Monospace 13px, **transparent background, no radius, no padding** — plain, unboxed | Boxed | 🔶 baseline is deliberately understated |
| **Surfaces** | Panel transparent (inherits theme); header and cards `#191A1B`; hairline borders `#2A2B2C`; text `#BBBEBF` / `#BFBFBF` | VS theme brushes + own overlays (`#22FFFFFF`, `#22808080`, `#14808080`, `#33808080`) | Both theme-aware; baseline's surface palette is tighter |
| **Warning banner** | bg ≈ `#473823`, fg ≈ `#D2B285`, `radius: 8px 8px 0 0`, 11.05px, own dismiss button | — | ➖ we have no equivalent usage/limit banner |
| **Theme integration** | Inherits VS Code font + foreground rather than imposing its own | Inherits VS theme brushes | ✅ same philosophy on both sides |

## 7b. Visual / UX improvements to adopt from the baseline

Concrete, screenshot-backed changes we could make to **our** extension, using the official one as the reference. Ordered by value-for-effort — none of these require new backend work.

| # | Improvement | Baseline reference | Why it's better than what we do now |
|---|---|---|---|
| 1 | **Add descriptions to the model picker** | real:09 | Baseline gives each model a subtitle with real decision-support — "~2× usage vs Sonnet", "Requires usage credits", "Fastest for quick answers". Ours is a bare name list, so the user can't tell the cost implication of switching. |
| 2 | **Add descriptions + a `⇧+Tab` hint to the permission picker** | real:10 | Baseline explains each mode in one line and advertises Shift+Tab cycling. We expose *more* modes than baseline (5 vs 3) but explain none of them, which makes the extra modes harder to use, not easier. |
| 3 | **Numbered permission-card options + inline "Tell Claude what to do instead"** | real:02, real:12 | Baseline's card is keyboard-driveable (`1`/`2`/`3`), states the **full path** being edited, shows an `Esc to cancel` hint, and lets the user redirect Claude without leaving the card. Ours has unnumbered buttons and no redirect box. |
| 4 | **A "Filter actions…" search box at the top of our palette** | real:08 | Baseline's menu is searchable. Our palette lists ~50 commands with no filter. |
| 5 | **Sort the slash-command list alphabetically** | real:08 | Baseline is A→Z and therefore scannable; ours is in skill/source order, which looks arbitrary. |
| 6 | **Generated short session titles + per-row rename/delete in history** | real:11 | Baseline shows "Watermelon"/"Pineapple" with inline pencil/trash icons. Ours shows a truncated raw first message and offers no row actions. |
| 7 | **Modal-card visual language** (dimmed backdrop, title + close X, centered empty-state text, tab strip where relevant) | real:15, real:16 | Baseline's Plugins/Rewind cards read as deliberate modals. Adopting this pattern would make our own popups feel less like bare dropdowns. |
| 8 | **Keyboard-affordance footers** (`↑↓ to navigate · Enter to select · Esc to close`) | real:16 | Baseline tells the user the keys in the UI itself. None of our popups do. |
| 9 | **Terminal hand-off cards for Memory / Agents / Hooks / Output styles / Permissions** | real:13 | This is how baseline itself "supports" those five features. Cheap to match, and it closes five checklist gaps without building any GUI. |
| 10 | **Show the extension version in the menu footer** | real:08 | Baseline shows `v2.1.250`; useful for bug reports, trivial to add. |
| 11 | **Reconsider the solid terracotta user-message bubble** | §7 | Measured: baseline user messages have *no* bubble — transparent, square, full-width. Our solid `#D97757` fill with a 10,10,2,10 radius is the biggest single style divergence, and it spends the brand accent on the most-repeated element instead of reserving it (baseline uses terracotta only on the ~26px send button). |
| 12 | **Collapse our type scale from nine font sizes to ~two** | §7 | Baseline uses 13px body + 11px chrome, full stop. We use nine sizes between 9 and 14px, which is what makes the panel look busier than baseline at a glance. |
| 13 | **Normalise corner radii to 5–6px** | §7 | Baseline: 5–6px everywhere (8px only for banner top corners). Ours: eight different radii from 3 to 15px. |
| 14 | **Make tool-call cards flat bordered cards, not collapsed expanders** | §7, real:03 | Baseline shows tool calls inline in a `#191A1B` card with a `#2A2B2C` hairline at 6px radius — readable without interaction. Ours hides them behind a disclosure arrow. |
| 15 | **Per-code-block "Copy code" button** | §6b | Baseline puts copy on each code block; we only have a global Copy Raw Output. |
| 16 | **Put keyboard hints in the UI** | §6b | Baseline's input placeholder is *"ctrl esc to focus or unfocus Claude"* and its mic tooltip reads *"Tap or hold to record · Ctrl+D"*. Ours advertises no shortcuts anywhere. |
| 17 | **Adopt the "one theme-invariant accent" rule** | §7 | Measured across dark *and* light: baseline re-derives every surface and text colour from the host theme and keeps exactly one constant brand colour (`#C6613F`, send button only). We hardcode `#D97757` onto the largest repeated surface in the panel. This is the rule behind improvement #11, and it's now measured rather than asserted. |
| 18 | **Per-message actions (fork / rewind code / both)** | real:19 | Baseline puts a `…` on *every* message offering **Fork conversation from here**, **Rewind code to here**, **Fork conversation and rewind code**. Separating conversation-forking from code-restoration is a genuinely better model than a single "rewind". |
| 19 | **Type-aware attachment chips** | real:19, real:22 | Baseline's pending chips show a thumbnail + pixel dimensions for images and a file icon for text/code. We already stage attachments — matching the chip presentation is cheap polish. |
| 20 | **Annotate collapsed tool calls with a grouped count and failure state** | §7 | Baseline's collapsed line reads `1 tool call ⌄` / `1 tool call · 1 failed ⌄`. Surfacing failure count without expanding is genuinely useful. |
| 21 | **Design an empty state** | real:20 | Baseline's new-session view has a wordmark, a mascot illustration, and a rotating tip. Ours starts blank. |

## Remaining test debt (our side only)

The baseline side of paste and drag-and-drop is now **done** (§6c). What remains is **our** WPF
side, which genuinely does go through OS-level pipelines the webview techniques don't touch:
OLE `IDropTarget`/`DoDragDrop` COM interop for drop, and the real clipboard-paste command for
`DataObject.Pasting`. The `Send-WmChar`/`Send-WmClick` helpers in `scripts/uia-lib.ps1` resolved
several *other* false gaps by delivering window messages directly — the equivalent
`WM_DROPFILES` / clipboard-format approach is the obvious next attempt before concluding these
are out of reach.

Also still owed: several IDE MCP tools (open editors / selection / dirty-state / save) beyond
`getDiagnostics`, which remain transport-tested only, not driven against real VS SDK objects.

## Summary read

- **Confirmed matching the baseline**: `@`-mention picker, message queueing (rigorously proven),
  `/compact`, `AskUserQuestion` single- and multi-select, `getDiagnostics`, inline diff,
  **CLI-crash silent respawn**, and **collapse-by-default tool cards**.
- **One real bug, not yet fixed**: "Active File" chip silently fails on Markdown Preview tabs.
- **Biggest genuine feature gaps**: Rewind / per-message fork (baseline separates *conversation
  forking* from *code restoration*), the **native side-by-side diff tab**, cloud/remote sessions
  (History → Web), MCP-server and plugin panels, Browse-the-web, voice dictation.
- **Biggest genuine style gaps** (all measured): the solid terracotta user bubble vs. baseline's
  no-bubble document layout, 9 font sizes vs. 2, 8 corner radii vs. 5–6px, no designed empty
  state, and the inverted accent rule (we spend the brand colour on the most-repeated surface;
  baseline reserves it for one control and keeps it theme-invariant).
- **Cheaper than they look**: five "Customize" gaps (Memory/Agents/Hooks/Output styles/
  Permissions) are just terminal hand-off cards in baseline too — prompt cards, not GUIs.

**For planning, use [`implementation-backlog.md`](implementation-backlog.md)** — the same findings
reorganised by work item, with acceptance criteria taken from the measured baseline values.
