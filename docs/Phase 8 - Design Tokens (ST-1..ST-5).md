# Phase 8 - Design Tokens (ST-1..ST-5)

**Date:** 2026-08-29

Second phase of the baseline-parity implementation (see [Phase 7](Phase%207%20-%20Active%20File%20Chip%20Fix%20%28BUG-1%29.md)
for the plan this continues). Implements the five style-token items (ST-1..ST-5) from
`docs/comparison-audit/implementation-backlog.md`, sizes taken from the 2026-08-28 live audit
against the official VS Code extension, not from taste.

## What was done

- **ST-1** — new `Core/ChatTheme.xaml` `ResourceDictionary`, merged into `UserControl.Resources`,
  the single source of truth for the chat UI's visual tokens. Its header states the rules so the
  literals it replaces cannot creep back in. 103 literals in `ClaudeCodeChatControl.xaml` route
  through it.
- **ST-2** — type scale collapsed from nine sizes (9, 10, 10.5, 11, 11.5, 12, 12.5, 13, 14) to two:
  `FontSizeBody` 13 and `FontSizeChrome` 11, matching the measured baseline. Emphasis now comes
  from `FontWeight` and `Opacity`, not from a third size.
- **ST-3** — corner radii collapsed from eight values (3, 4, 5, 6, 8, 10, 11, 15) to two:
  `RadiusControl` 5 and `RadiusCard` 6.
- **ST-4** — accent discipline. Every `ClaudeAccentBrush` use was audited; the permitted surfaces
  are enumerated in `ChatTheme.xaml`. Nothing gratuitous was found - the accent was already
  confined to the send button, focus ring, selection tint, current-selection affordances, and the
  user bubble.
- **ST-5** — the terracotta user bubble is **kept**, deliberately, against the measured baseline
  (which has no bubble at all) - per the user's standing decision from planning. The reasoning is
  recorded in a comment at `UserMessageTemplate` so a later pass does not "fix" it. Its fill and
  `10,10,2,10` tail stay literal there rather than tokenised, so a change to `ChatTheme.xaml`
  cannot alter them by accident.

Two values in each scale were deliberately **not** folded in: `GlyphSize`/`GlyphSizeSmall` size
icon characters inside fixed-size buttons, and `RadiusCircle`/`RadiusPill` round an element from
its own height. Those are geometry, not typography or corner treatment, and naming them separately
keeps the two-value rule a real constraint rather than a four-value one.

## Verification

Verified live in the VS 18 Experimental instance, since a missing `StaticResource` key is a
runtime `XamlParseException`, not a build error - a clean build proves nothing here. The control
was instantiated for real and its full visual tree enumerated. ST-4 was checked by sampling pixels
rather than by eye:

| surface | light | dark |
|---|---|---|
| chat panel background | `#F9F9F9` | `#282828` |
| Solution Explorer background | `#F9F9F9` | `#282828` |
| chat input area background | `#EFEFEF` | `#2F2F2F` |
| send button fill (accent) | `#D97757` | `#D97757` |

The panel tracks a stock VS tool window exactly in both themes, and the accent is byte-identical
across them.

Also adds three reusable, background-safe harness scripts (UIA + `PrintWindow` only, no focus
stealing, no physical input): `vs-menu.ps1` drives the real VS menu bar, `sample-pixels.ps1` reads
the on-screen colour of named elements, and `phase-b-verify.ps1` is the load check.

**Files:** `Core/ChatTheme.xaml` (new), `Core/ClaudeCodeChatControl.xaml` (~140 literal swaps),
`docs/comparison-audit/scripts/vs-menu.ps1`, `sample-pixels.ps1`, `phase-b-verify.ps1` (new)

Commit: `41e15e2` "Phase B: design tokens (ST-1..ST-5)"
