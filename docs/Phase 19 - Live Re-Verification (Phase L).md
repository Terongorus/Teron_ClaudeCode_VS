# Phase 19 - Live Re-Verification (Phase L)

**Date:** 2026-09-01

The "Phase L" live re-verification agreed on 2026-08-31 (see
[Phase 16](Phase%2016%20-%20Voice%20Dictation%20and%20Running-Cloud%20Sessions%20%28FEAT-8%2C%20FEAT-9%29.md)'s
closing note): with A-K's feature work complete, close as much of
`docs/comparison-audit/implementation-backlog.md`'s "Needs a human" checklist as is actually mine
to close, against a live VS 18 Experimental instance running the extension at `0.4.0`.

## Scope

The checklist groups items by *why* they were unautomated. Only categories A (blocked on TEST-1,
now shipped), B (visual judgement), and parts of D (hard-to-provoke states) are things a live pass
can plausibly settle. Categories C (real human input - speaking, holding a button, a real
keystroke), the rest of D (a genuine usage-limit boundary), and E (outward-facing actions - a real
cloud session, a terminal launch that steals focus) stay the user's own job regardless of how
thorough this pass is; that was true before this phase and is unchanged by it.

## Environment

The extension was rebuilt at `0.4.0` (see [Phase 18](Phase%2018%20-%20Manual%20Repo%20Changes%20and%20VSIX%20Packaging%20Fix.md)),
deployed into the `Exp` hive with `VSIXInstaller.exe /quiet /rootSuffix:Exp`, and the hive was
force-refreshed with `devenv /rootsuffix Exp /updateconfiguration` before launch - the same stale
package-registration trap Phase 6 documented. Driven entirely through the existing
`docs/comparison-audit/scripts/` toolkit (UIA `InvokePattern`, `PrintWindow` composite screenshots),
background-safe throughout: no `SetForegroundWindow`, no physical mouse or keyboard input.

Three of that toolkit's own scripts (`phase-h-live.ps1`, `phase-i-unit.ps1`, `phase-j-unit.ps1`)
turned out to still have default parameter paths pointing at the pre-relocation `comparison-audit\`
folder from before Phase 18 moved it under `docs\`; fixed before this pass started (see Phase 18's
commit history) since running them unmodified would have silently written or read from a directory
that no longer exists.

## A4 / A5 - the MCP servers and Manage plugins panels, resolved

All six `Click` handlers (`OnMcpServersClicked`, `OnCloseMcpClicked`, `OnManagePluginsClicked`,
`OnPluginsTabClicked`, `OnMarketplacesTabClicked`, `OnClosePluginsClicked`) were driven live, not
just once each but through the actual open -> switch tab -> switch back -> close sequence a real
user would take. Both panels render against the real, un-sandboxed CLI configuration on this
machine, which turned out to already have a genuine marketplace installed
(`claude-plugins-official`, auto-installed by the CLI itself at some point before this session) -
so this pass exercises real data, not an empty state: the Marketplaces tab shows that marketplace's
real GitHub source and local install path, and the Plugins tab's Available list shows dozens of
real plugin entries with their real descriptions, scrolling correctly.

Both panels' layout holds up under direct visual inspection: card shadow, title, close glyph,
tab-strip underline on the selected tab, and the shared `PopupCardStyle` modal language from
Phase 9 are all present and consistent between the two panels. Screenshots:
`docs/comparison-audit/screenshots/our-extension/phase-l/L01-mcp-panel-empty.png`,
`L02-plugins-marketplaces-tab.png`, `L03-plugins-tab.png`.

One harness note: `Find-ByAutomationId` intermittently failed to locate `MarketplaceList` and
`MarketplacesEmptyStateText` even when the screenshot taken in the same call proved the content was
genuinely rendered and correct. Not chased further, since the visual evidence is unambiguous and
this is exactly the class of automation-peer flakiness (WPF popups, `ItemsControl`s with dynamic
content) this project's own docs have hit repeatedly before - a harness gap, not a product one.

## B4 - the Running tab with long paths and a long list, resolved

`docs/comparison-audit/screenshots/our-extension/phase-l/L04-history-running-tab.png`, taken
against this machine's real session history: rows with genuinely long absolute paths render with
`TextTrimming` rather than overflowing or wrapping badly, the list scrolls (scrollbar visible with
more rows below the fold), and row spacing stays consistent across interactive, background-running,
and background-done rows of very different content lengths.

## B1 - live theme re-derivation, attempted and genuinely blocked

Not resolved, and not for lack of trying. VS 2026 (VS 18) does not open Tools > Options as a
separate dialog window the way earlier VS versions do - it opens as an in-window settings tab,
found by locating a `Tree` control named "Table of Contents"
(`AutomationId=PART_SettingsHierarchy`). From there the real "Color theme" `ComboBox` was located
directly (not the adjacent "System light theme"/"System dark theme" cards, which turned out to be
a different, unrelated setting and which - checked the same way - are *also* not invocable). The
combo itself works correctly under `ExpandCollapsePattern`: it opened, and its live value (`Dark`)
and the full list of thirteen real theme names, including `Light`, were read directly off the
expanded dropdown.

Every item inside that expanded list, checked individually, supports exactly one UIA pattern:
`SynchronizedInputPatternIdentifiers.Pattern`. None of `InvokePattern`, `TogglePattern`,
`SelectionItemPattern`, or `LegacyIAccessiblePattern` - the four patterns
`docs/comparison-audit/scripts/uia-lib.ps1`'s `Invoke-UiaClick` tries, in that order, and the same
four every background-safe click in this project's whole toolkit has ever depended on - are
present. `SynchronizedInputPattern` can technically synthesize input, but only by supplying real
screen coordinates for a synthesized pointer-down/up, which is materially the same thing as physical
input simulation the standing rule exists to forbid. This was confirmed by direct pattern
inspection, not inferred from a single failed click - the dropdown was opened successfully, proving
the combo itself is reachable; only its items resist every read-only invocation path.

The dropdown was left collapsed and the theme setting untouched (`Dark`, as found) before moving on.

**What this means for the checklist:** B1 stays open, but with a real, dead-end reason attached
rather than "not attempted." A person can still do this in about ten seconds by hand. The indirect
case for it working stays strong - `Core/ChatTheme.xaml` and every literal it replaced route
through `DynamicResource` against `VsBrushes` keys (Phase 8), which is VS's own standard mechanism
for live theme propagation and is not something this extension's code controls either way - but
"very likely to work, by design" and "watched happening" are different claims, and this pass could
not convert the first into the second.

## B5 - the rewind picker, partially settled

`docs/comparison-audit/screenshots/our-extension/phase-l/L06-rewind-picker.png`: the card itself -
title, close glyph, centred message, `↑↓ to navigate · Enter to select · Esc to close` footer - is
clean at the tool window's default dock width. The scratch session used for this pass had never
had a message sent in it (deliberately, to spend no quota), so only `RewindEmptyStateText`'s "No
messages to rewind to yet." was seen. A populated picker with several real rewind points, and the
narrow-dock-width case specifically, remain open.

## What's still open, and why it stays that way

- **B2, B3** - narrow dock-width layout checks. Resizing a docked VS tool window has no UIA
  affordance anywhere in this project's toolkit; not attempted this pass.
- **C1-C4** - speaking into a real microphone, the hold gesture, a real `Ctrl+D` keystroke, and
  dictating mid-message. All four need real physical input by definition; automating any of them
  would violate the standing background-safe rule, not merely stretch it.
- **D1** - a real usage-limit model fallback. Still needs a genuine overload, refusal, or credit
  boundary to arrive from the live API; not something a scripted pass can provoke on demand.
- **D2** - a rewind that must refuse a symlinked or moved path. Not attempted this pass - it needs
  a real conversation turn (to generate file-history records) plus a deliberately constructed
  symlink scratch scenario, which was judged not worth the quota for this pass given how much of
  the rest of the checklist was reachable without it. Still open.
- **E1-E3** - a real cloud session hand-off, the Running tab's terminal buttons, and the Customize
  hand-off cards. All three launch a real terminal or touch a real external account, and stay
  deliberately unclicked per the same reasoning recorded when they were first flagged in Phase 16.

## Backlog corrections made along the way

Two stale-state items were found and fixed in `implementation-backlog.md` while updating it for
this pass, both pre-dating this phase:

- **TEST-1 and the xUnit-conversion row still said "not started"**, despite both having shipped in
  Phase 17. Corrected, and the FEAT-4/FEAT-5/UX-9 status-table rows (which depended on TEST-1/A4-A5)
  updated from "built, unverified"/"handlers undriven" to done, with this phase cited as evidence.
- **Category A's own rows (A1-A5)** were rewritten with strikethrough and resolution notes rather
  than deleted outright, per this repo's established convention (see Phase 9's own correction) of
  leaving a visible trail rather than quietly editing history.

## Verification

Live, against a real `0.4.0` build in a fresh VS 18 Experimental instance. Six screenshots saved
under `docs/comparison-audit/screenshots/our-extension/phase-l/`. The Exp instance was closed
cleanly at the end of the pass; no setting was left changed.
