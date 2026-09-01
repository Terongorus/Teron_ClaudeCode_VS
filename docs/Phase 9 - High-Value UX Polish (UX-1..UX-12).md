# Phase 9 - High-Value UX Polish (UX-1..UX-12)

**Date:** 2026-08-29

Third phase of the baseline-parity implementation (continues [Phase 8](Phase%208%20-%20Design%20Tokens%20%28ST-1..ST-5%29.md)).
Implements all twelve Tier-2 items (UX-1..UX-12) from `docs/comparison-audit/implementation-backlog.md`,
grouped into one commit because they share Phase 8's token work.

## What was done

- **UX-1 / UX-2** — `ModelOption`/`PermissionModeOption` gain a `Description`, rendered as a second
  line in the model and permission-mode picker templates. Model subtitles are baseline's own
  wording ("~2x usage vs Sonnet", "Requires usage credits", "Fastest for quick answers"); the
  permission-mode descriptions cover all 7 modes we expose (baseline documents 5).
- **UX-2 cont.** `Shift+Tab` cycles permission modes - a new branch in `OnInputPreviewKeyDown`,
  plus a "shift + tab to switch" hint in the picker.
- **UX-3** — permission cards gain number keys (`1`/`2`/`3` + `Esc` while the input box is empty),
  state the full absolute path, and offer an inline "Tell Claude what to do instead" box that
  denies with a redirect message instead of dead-ending. `PermissionRequestViewModel` gains a
  `RedirectText` + command.
- **UX-4** — filter box at the top of `PalettePopup`, filtering the shared `SlashCommands` view so
  the palette and `/` autocomplete cannot diverge.
- **UX-5** — `SlashCommands` sorted A-Z on populate.
- **UX-6** — key-hint footers in the pickers and on the permission card, and a `ctrl esc to focus`
  input placeholder naming the real focus chord.
- **UX-7** — collapsed tool rows annotate count and failure (`N tool calls - 1 failed`); tool calls
  were already collapsed by default, only the annotation was missing.
- **UX-8** — per-code-block copy button, added in `MarkdownRenderer.PostProcess` as a
  `FlowDocument` Floater - code paragraphs are already identified by `IsLightBackground`, so the
  copy affordance attaches at the same point.
- **UX-9** — attachment chips show image pixel dimensions and a type-appropriate glyph.
  `PendingImageAttachment` already held the thumbnail needed for this.
- **UX-10** — extension version in the palette footer, read from the VSIX manifest rather than the
  assembly - those two version numbers differ in this repo, and only the manifest one is real.
- **UX-11** — a designed empty state (wordmark, one-line tip) for a new session, in place of the
  blank `MessageList`.
- **UX-12** — one shared `PopupCardStyle` behind every popup, plus shared hint/footer styles - the
  modal visual language the Plugins/Rewind/MCP surfaces built in later phases reuse.

The UX-1/UX-2 wording is not invented: the permission-mode strings come from the official
extension's own webview bundle and the model subtitles from the CLI binary's model table, so
shared entries use baseline's exact words. "Don't Ask" and "CLI Default" are ours, written from
the CLI's documented semantics - "Don't Ask" denies anything not pre-approved, which its name
implies the opposite of.

Also fixed five `FontSize` literals Phase 8's sweep missed because they were written as
`<Setter Property="FontSize" Value="11"/>` rather than as attributes, so ST-2's two-value rule now
genuinely holds across the file.

## Two VS-specific traps found and documented in code

- A changed `.vsct` does nothing until `ProvideMenuResource`'s version is bumped - VS caches the
  merged command table against it, with no error either way.
- VS silently drops a VSIX default key binding that collides with an existing one. `Ctrl+Alt+C` was
  already `Debug.CallStack`, so the binding simply never existed. `Ctrl+Alt+Y` was picked by
  querying the live command table for a chord free in every scope and not a chord prefix.

## Verification

Verified live in the VS 18 Experimental instance: 19/19 checks in the new `phase-c-verify.ps1`,
plus three driven turns covering the mid-conversation surfaces. The permission-card redirect was
exercised end to end and confirmed to write nothing.

**Not driven live:** UX-2's Shift+Tab cycle and ~~UX-9's chips are built but not driven - both need
input the background-safe harness cannot synthesise. UX-9 is deferred to TEST-1 (later, Phase 17),
which exists to build exactly that.~~ **UX-9 resolved in [Phase 17](Phase%2017%20-%20TEST-1%20and%20the%20xUnit%20Test%20Port.md):**
`AttachmentTests` drives real paste/drop/remove through the routed events in-process and asserts on
the rendered chips (paste, dimensions, glyph, and the `✕` removal). UX-2's Shift+Tab cycle is still
unverified - it needs a live keypress, which is a different gap TEST-1 was never meant to close.

**Files:** `Core/ClaudeCodeChatControl.xaml(.cs)`, `ViewModels/ChatSessionViewModel.cs`,
`ViewModels/ContentBlocks.cs`, `Controls/MarkdownRenderer.cs`,
`docs/comparison-audit/scripts/phase-c-verify.ps1` (new)

Commit: `f499aad` "Phase C: high-value UX polish (UX-1..UX-12)"
