# Phase 5 - Chat UX Batch

**Date:** 2026-08-27 – 2026-08-28

Fifth implementation pass of the [Full CLI Parity Roadmap](Phase%200%20-%20Full%20CLI%20Parity%20Roadmap.md).
The user requested a batch of chat UX features in one go: paste screenshot, background tasks, a
transcript view mode toggle (Normal/Thinking/Verbose/Summary), a treeview of chat sessions, and a
live session status line. A later follow-up in the same session added full drag-and-drop import.

## Investigation

Per explicit user request, every feature was grounded in what the real, currently-installed
official VS Code extension (`anthropic.claude-code-2.1.247`,
`C:\Users\kkole\.vscode\extensions\`) actually does, read directly from its minified
`extension.js`/`webview/index.js` bundles (`grep -oE` context-window extraction, since these are
enormous single-line-per-chunk files that can't be read normally) - not guessed.

**Confirmed real, with exact mechanisms extracted from the bundle:**

- **Paste screenshot / drag-and-drop.** Clipboard/drop file handling encodes to a real Anthropic
  Messages API content block:
  - Image: `{"type":"image","source":{"type":"base64","media_type":"image/png","data":"..."}}`
  - Text/code document: `{"type":"document","source":{"type":"text","media_type":"text/plain",
    "data":"<raw decoded text, NOT base64>"},"title":"<filename>"}`
  - PDF document: `{"type":"document","source":{"type":"base64","media_type":"application/pdf",
    "data":"..."},"title":"<filename>"}`
  - The bundle's own file-type classifier (`IK1`/`hX0`/`bX0`/`kX0`/`EK1`, minified names) was
    ported verbatim into this codebase as C# `HashSet<string>`s: an image MIME/extension
    allowlist, and a large (~140-entry) text-file extension allowlist plus a small set of
    extensionless filenames treated as text (`LICENSE`, `README`, `Makefile`, `Dockerfile`, etc.),
    since a local file drop only exposes a path/extension, not a browser MIME type.
- **Concurrent multi-session + sessions treeview.** The real extension keeps a `sessions` list
  plus `sessionGroups` (user-created folders you drag sessions into) and supports multiple
  simultaneously-open sessions. **Dropped from this pass** - per the user's own correction after
  reviewing this finding: *"This is a feature taken from Claude Code for desktop and not Claude
  Code for Visual Studio... we don't need multi-session support and grouping, because our
  extension already groups sessions per-workspace (each opened solution/project has its own
  'domain')."* A desktop app has no natural project/workspace boundary and has to build its own
  session-switcher for that; this extension doesn't have that problem - each opened VS
  solution/instance is already its own separate domain (its own tool window, own process), a
  structural difference from Desktop, not a gap to fill. The existing flat resume-history picker
  (`SessionHistory`/`IsSessionHistoryVisible`, `ChatSessionViewModel.cs`) is untouched by this
  phase.

**Not found anywhere in the real extension - these are novel asks, not parity work:**

- No background-tasks panel or list exists. The only related code is an internal protocol
  handshake (`backgroundTasks(tool_use_id)` request, subtype `"background_tasks"`, auto-approving
  backgrounding a long-running tool; `stopTask(task_id)` for cleanup) - zero user-facing UI.
- No transcript verbosity/display-density toggle exists. The only "thinking"-adjacent concept is
  `thinkingLevel`/`thinkingLevelOverride`, the extended-thinking effort/budget selector this
  extension already ships (Phase 1) - unrelated to display density.
- No persistent live status line (elapsed time / token count / running-task count) exists; only a
  static "Thinking..." busy-state string.

Given this evidence, the user confirmed: still build a dedicated running-tasks panel and the
transcript-mode toggle despite having no reference implementation to match, using a self-defined
"progressive detail levels" semantics -
**Summary** = final text + result footer only, tool calls collapsed with no expand affordance,
thinking hidden entirely. **Normal** = existing default behavior (thinking/tool-calls collapsed
but user-expandable). **Thinking** = like Normal but thinking blocks default to expanded.
**Verbose** = like Thinking, plus tool-call detail also defaults to expanded.

A CLI check (`claude --help`) also confirmed there is no `models list`-style subcommand for an
older-model-version submenu on the new Model button (5e below) - `--model` just accepts a
latest-alias or a freeform dated string, and the dated version strings visible in the bundle are
Anthropic SDK-internal deprecation metadata, not a real menu to copy. No older-version submenu was
built this pass.

## Design and implementation

### 5a. Paste screenshot + drag-and-drop import

- `Core/ClaudeCodeChatControl.xaml.cs`: `DataObject.Pasting` handler on `InputBox` for
  `Clipboard.ContainsImage()` (`PngBitmapEncoder` to base64 PNG), plus a full WPF drag-and-drop
  handler set (`DragEnter`/`PreviewDragOver`/`DragLeave`/`Drop`) on the input area border -
  `DataFormats.FileDrop` for real files (classified image / text / PDF via the ported allowlists
  above) and `DataFormats.Bitmap` for a dropped image (e.g. from a browser), sharing the same PNG
  encode path as paste. The border highlights with the accent brush while a valid drag is over it.
- `ViewModels/ChatSessionViewModel.cs`: `PendingImageAttachment` (base64 PNG + thumbnail) and
  `PendingFileAttachment` (title, `IsPdf`, raw content) staged in `PendingImages`/`PendingFiles`
  until send, each removable via a chip "x" above the input box.
- `Core/ClaudeCodeSession.cs`: `SendUserMessageAsync` extended with optional image/file lists,
  appending the exact content-block shapes confirmed above to the existing outgoing `content`
  `JArray` alongside the text block.
- Rendering: `ImageAttachmentViewModel`/`FileAttachmentViewModel` added to
  `ViewModels/ContentBlocks.cs` so a sent user message's bubble shows the pasted/dropped
  image thumbnail or a 📄 file chip, via new templates in `Controls/TemplateSelectors.cs` /
  `ClaudeCodeChatControl.xaml`.

### 5b. Transcript view modes

- `enum TranscriptViewMode { Summary, Normal, Thinking, Verbose }` and `CurrentTranscriptMode` on
  `ChatSessionViewModel`, exposed via a new header dropdown next to the Raw-output toggle, default
  `Normal`. Block-creation (`OnBlockStarted`) sets a new thinking/tool-call block's initial
  `IsExpanded` from the current mode instead of always `false`; changing the mode re-applies
  `IsExpanded` across every block already in `Messages` (`ReapplyTranscriptMode`) so toggling
  mid-transcript feels consistent, not just prospective.
- `Summary` mode additionally hides thinking blocks entirely and disables the tool-call expand
  affordance, via two new converters in `Controls/Converters.cs`
  (`HiddenInSummaryModeConverter`, `ToolCallExpandableConverter`) bound with
  `RelativeSource AncestorType=ItemsControl, AncestorLevel=2` - block DataTemplates are nested two
  `ItemsControl`s deep (outer session `Messages`, inner per-message `Blocks`), so reaching the
  session-level `ChatSessionViewModel` from inside a block template means skipping the inner one.

### 5c. Live session status line

- A `DispatcherTimer` (1s tick) on `ChatSessionViewModel`, running only while `IsBusy`, drives
  `ElapsedText` (e.g. `"11m0s"`) - there was no existing stopwatch for an in-flight turn
  (`DurationMs` only arrives after completion, in `ResultMessage`).
- `RunningToolCalls` (`ObservableCollection<ToolCallViewModel>`), maintained by hooking each new
  tool call's `PropertyChanged` and adding/removing as `Status` transitions to/from `Running`
  (`OnToolCallStatusChanged`) rather than re-scanning `Messages`. `RunningTaskCount`/
  `HasRunningTasks` derive from it and are reused directly by 5d.
- `StatusLineConverter` (new `IMultiValueConverter`) composes `ElapsedText`,
  `SessionTokensShortText` (new short form of the existing cumulative-token `SessionUsageText`),
  `RunningTaskCount`, and `StatusText` into one line in the header status strip, falling back to
  plain `StatusText` when idle.

### 5d. Background/running-tasks panel

- New collapsible panel (`Grid.Row="2"`, between the status strip and the transcript) bound
  directly to 5c's `RunningToolCalls` - icon, display name, elapsed time per task, and a click
  handler (`OnJumpToRunningTaskClicked`, via
  `MessageList.ItemContainerGenerator.ContainerFromItem` + `BringIntoView()`) that scrolls the
  transcript to that tool call's card. Scoped per-session, matching this extension's existing
  per-workspace model - no cross-session aggregation needed.

### 5e. Split the combined command menu into four buttons

The old single `CommandMenuButton` (label like "/ Sonnet · Default") opening one stacked popup
(Model / Permission Mode / Effort / usage summary / slash commands) was replaced with four
independent chip buttons - a command-palette button (fixed "/" glyph), Model, Permission Mode,
and Effort - each anchoring its own `Popup`. All underlying bindings/templates/checkmark pattern
(`EqualityToVisibilityConverter` against `SelectedModel`/`SelectedPermissionMode`/
`SelectedThinkingLevel`) were reused as-is, just re-hosted; the palette popup keeps the "THIS
SESSION" usage summary, "Account & Usage ->" link, and `COMMANDS` section. Code-behind gained one
`CloseAllMenuPopups()` helper (closing all four plus `AccountUsagePopup`) called before opening
any target popup, replacing the scattered ad hoc `<X>Popup.IsOpen = false` lines that existed per
handler - same mutual-exclusion behavior, less duplication.

## Bugs found and fixed during implementation

1. **Duplicate `FormatTokenCount` method (CS0111).** A new `SessionTokensShortText` property was
   written against a newly-added `FormatTokenCount(long)` helper without noticing an
   identical-signature one already existed later in the file (used by result footers). Fixed by
   deleting the duplicate and reusing the existing one.
2. **XAML tag mismatch during the 5e split.** Replacing the outer `<Grid Grid.Row="3">` with a
   `<StackPanel>` broke the file because the pre-existing `SendButton`/`StopButton` further down
   still used `Grid.Column="1"` and needed the outer element to remain a `Grid`. Caught immediately
   via an IDE `ETagRequired` diagnostic; fixed by keeping the outer `Grid` (two columns) and
   wrapping only the four new chip buttons in an inner `StackPanel Grid.Column="0"`.
3. **Stale `CommandMenuPopup` references (CS0103/CS1061).** Seven code-behind call sites and four
   new XAML click-handler bindings referenced the removed combined button/popup after the 5e
   split. Fixed by rewriting the popup-management block around the new
   `CloseAllMenuPopups()` helper and updating every option-click handler to close its own specific
   popup.
4. **CS0104 ambiguous `Debugger` reference.** `Debugger.Log(...)` in the new
   `ImportDroppedFileAsync` catch block was ambiguous between
   `Community.VisualStudio.Toolkit.Debugger` (already `using`'d in this file) and
   `System.Diagnostics.Debugger`. Fixed by fully qualifying as `System.Diagnostics.Debugger.Log(...)`.
5. **Send guard didn't account for file-only drops.** `SendCurrentInputAsync`'s guard was updated
   for `HasPendingImages` (5a) when it landed, but not for `HasPendingFiles` once drag-and-drop
   added file attachments - a message consisting only of a dropped text/PDF file with no text and
   no image would have been silently blocked from sending. Fixed by adding
   `&& !_vm.HasPendingFiles` to the guard.
6. **Unrelated stray `PackageReference` for `MessagePack`** was found in `TeronClaudeCodeVS.csproj`
   during a pre-commit review pass - not used anywhere in the diff or the codebase, and not part
   of any feature in this phase. Removed; a clean rebuild confirmed nothing depended on it.

## Verification

`dotnet build TeronClaudeCodeVS.csproj` - 0 warnings, 0 errors, after every sub-feature (5a-5e)
and after the drag-and-drop follow-up and the guard fix above.

**Not yet live-verified.** Unlike Phases 1-4, no F5 pass has been run against any of this phase's
work yet - paste, all four transcript modes, the status line's live ticking/running-task count,
the running-tasks panel, the four split popups' mutual exclusion, and drag-and-drop's
classification/visual feedback are all real runtime-only concerns (WPF `RelativeSource`/
`MultiBinding` paths in particular fail silently at runtime, not at compile time) that a clean
build cannot confirm. This is flagged here deliberately rather than claimed as done - the next
live pass on this extension should exercise all of Phase 5 before it's considered verified,
consistent with how every prior phase's real bugs (see Phase 1-4 logs) were only found by
actually running it.
