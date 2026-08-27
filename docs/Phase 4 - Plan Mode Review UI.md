# Phase 4 - Plan Mode Review UI

**Date:** 2026-08-27

Fourth implementation pass of the [Full CLI Parity Roadmap](Phase%200%20-%20Full%20CLI%20Parity%20Roadmap.md),
replacing the generic Allow/Allow-for-Session/Deny permission card previously shown for the
built-in `ExitPlanMode` tool call with a dedicated Plan Mode review UI matching the real official
Claude Code extension's behavior.

## Investigation

Confirmed via a user screenshot that `ExitPlanMode` was rendering through the same generic
permission card used for every other tool - the real extension instead opens the plan as a
separate native editor tab and shows a distinct "Accept this plan?" prompt with three choices
(auto-accept future edits / manually approve edits / keep planning with free-text feedback), not a
generic Allow/Deny. Two live investigations grounded the plan before any code was written:

- An Explore agent confirmed `ExitPlanMode` had no special-case handling anywhere outside the
  generic permission-card path, and that the repo had no existing infrastructure for hosting
  arbitrary content in a VS document tab - only the existing `OpenFileAsync`
  (`Core/VsIdeToolHandlers.cs`), which opens a real file on disk via the Community Toolkit.
- A live capture against the real `claude.exe` (`--permission-mode plan`) confirmed the
  `can_use_tool` control_request for `ExitPlanMode` carries `input.plan` (markdown) **and**
  `input.planFilePath` - a real `.md` file the CLI has already written to `~/.claude/plans/` - plus
  `requires_user_interaction: true`. There is no `updatedPermissions`/suggestions field on the wire;
  any auto-accept-after-approval behavior has to be synthesized client-side.

The "select text to add inline comment" mechanism was then demonstrated live end-to-end against
this very plan file: selecting a span of plan text surfaces a floating "Add Comment" button;
submitting a comment stages it in the same chat card, which swaps its primary action for "Send
feedback and keep planning"; the resulting rejection was confirmed to be a pure client-side
construct (`[Re: "<quoted excerpt>"] <comment text>`, joined per comment), not a CLI wire feature -
delivered through the existing documented `deny` + `message` shape.

## Design and implementation

- **`ViewModels/ContentBlocks.cs`** - new `PlanApprovalViewModel`: `PlanMarkdown`, `PlanFilePath`,
  bindable `FeedbackText`, `AutoAcceptCommand`/`ManuallyApproveCommand`/`KeepPlanningCommand`, an
  `ObservableCollection<PlanCommentEntry>` `Comments`, and a computed `HasComments` that swaps the
  UI to a single `SendFeedbackCommand` once any comment is staged.
- **`ViewModels/ChatSessionViewModel.cs`** - new `OnExitPlanModeRequested` branch ahead of the
  generic fallback (mirroring the existing `AskUserQuestion` special-case): opens `planFilePath` as
  a real VS tab immediately, registers it in a file-path-keyed lookup so submitted comments route
  back to the right chat card, and adds the `PlanApprovalViewModel` block. Response semantics reuse
  the existing `RespondToPermissionAsync` plumbing unchanged - "auto-accept" adds `Edit`/`Write`/
  `NotebookEdit`/`MultiEdit` to the existing session-scoped auto-allow set (the same mechanism
  "Allow for Session" already uses) rather than relying on the unconfirmed wire-level
  `updatedPermissions` field.
- **`ViewModels/PlanCommentRegistry.cs`** (new) - a plain static pub/sub bridge
  (`RegisterActivePlan`/`UnregisterActivePlan`/`IsActivePlanFile`/`SubmitComment` +
  `CommentSubmitted` event) decoupling the MEF-hosted editor component from `ChatSessionViewModel`,
  keeping `ViewModels/` free of VS SDK references per this project's existing convention.
- **`Controls/PlanCommentAdornment.cs`** (new) - the text-selection "Add Comment" affordance:
  - `PlanCommentTextViewListener`, a MEF-exported `IWpfTextViewCreationListener` - the first MEF
    editor-extensibility component in this repo (previously only `IWpfTextView` for programmatic
    selection existed, in `Core/VsIdeToolHandlers.cs`). De-risked first with a no-op logging
    listener before building the rest, confirmed live via `Debugger.Log` (used instead of
    `Debug.WriteLine`/`Trace.WriteLine` since those are `[Conditional]`-stripped from Release
    builds) that VS actually instantiates it for this VSPackage-style extension.
  - `PlanCommentAdornmentManager` - per-view controller: shows a floating "Add Comment" `Button` on
    `ITextView.Selection.SelectionChanged` (only while `PlanCommentRegistry` confirms the view's
    file is a pending plan approval), a small `Popup` composer on click, and a persistent
    highlight `Border` adornment per submitted comment so multiple simultaneous comments across the
    document stay visually distinguishable.

## Bugs found and fixed via live verification

1. **"Add Comment" button clipped at the top of the viewport.** Originally anchored above the
   selected line (`bounds.Top - 26`); when the selection was on/near the first visible line there
   was no room to render above it. Fixed by anchoring below the line instead
   (`bounds.Bottom + 4`), per explicit user feedback from two screenshots.
2. **Button rendering behind the editor text.** The adornment layer was only ordered
   `[Order(After = PredefinedAdornmentLayers.Selection)]`, which wasn't sufficient to guarantee
   painting above the actual text glyphs. Added a second
   `[Order(After = PredefinedAdornmentLayers.Text)]` constraint (multiple `[Order]` attributes
   stack as separate constraints) so the layer explicitly paints above the text layer.
3. **"Tell Claude what to do instead" mispositioned.** Originally rendered between the staged
   comments list and the action button(s); the real extension renders it below the last button.
   Moved the `FeedbackText` `TextBox` block to after the action-button `StackPanel` in
   `Core/ClaudeCodeChatControl.xaml`'s `PlanApprovalTemplate`.
4. **Button/popup used hardcoded white/black colors**, ignoring the user's VS theme entirely. Since
   this UI is built in code inside a MEF editor component (no access to the chat control's own XAML
   `StaticResource` dictionary, which is scoped to that UserControl's visual tree), replaced the
   hardcoded brushes with `Microsoft.VisualStudio.Shell.VsBrushes.ToolWindowBackgroundKey`/
   `ToolWindowTextKey`/`ToolWindowBorderKey` applied via `SetResourceReference` (same pattern
   already used in `Controls/DiffViewer.xaml.cs`), so the UI now matches the active theme and
   repaints live on theme change.
5. **Button and composer text too small** - increased the "Add Comment" button to 14pt with
   larger padding, the composer textbox to 340x100 at 14pt, and its Cancel/Add Comment buttons to
   13pt, per direct user request after the first live pass.

## A dead end: native Markdown preview split view

Per an explicit user choice (overriding this plan's original "out of scope for this pass" note),
a `Core/MarkdownPreviewOpener.cs` was built to open the plan file directly into VS 18's native
Markdown editor's rendered "preview" split logical view, via
`IVsUIShellOpenDocument.OpenStandardEditor` with the real `MarkdownPreview` logical-view GUID
(`fe087456-8dab-4b46-a458-8b53eb480717`, pulled directly from
`Microsoft.VisualStudio.Platform.Markdown.pkgdef`, not guessed - the Community Toolkit has no
wrapper for logical-view-aware opening, confirmed via a research agent).

It worked in the user's first live pass (confirming the preview split view still exposes a real
`ITextView`, so the comment adornment worked there too), but crashed VS on a later run with an
**`AccessViolationException` originating inside the `OpenStandardEditor` interop call itself** -
a corrupted-state exception, uncatchable by a normal `try`/`catch`, which is why the existing
fallback-on-failure logic never got a chance to run. The `MarkdownPreview` logical view is
apparently not safe to open this way for VS 18's own Markdown editor factory (most likely a native
null-deref given `pHier`/`psp` are passed as null), regardless of how correct the GUID/registration
data was.

Reverted rather than patched: `MarkdownPreviewOpener.cs` was deleted and
`OnPlanFileReadyToOpen` (`Core/ClaudeCodeChatControl.xaml.cs`) now calls the plain
`VS.Documents.OpenInPreviewTabAsync` unconditionally - the same safe path used everywhere else in
this codebase, and this plan's original in-scope call. A rendered Markdown preview for the plan
tab remains a real, not-yet-solved gap for a future pass; any retry needs a different API (e.g.
`OpenSpecificEditor` with the editor factory GUID, or investigating why `OpenStandardEditor`
crashes for this specific logical view) rather than reusing this approach as-is.

## Verification

`dotnet build TeronClaudeCodeVS.csproj` - 0 warnings, 0 errors, after every change in this phase.

Live F5-verified by the user across several passes: plan file opens as a separate native VS tab;
selecting text shows the "Add Comment" button (now correctly anchored below the selection and
layered above the text); submitting one or more comments stages them correctly in the chat card
(quoted excerpt + text, correct count, correct `[Re: "..."] ...` deny-message format on send);
"Send feedback and keep planning" round-trips correctly and the model continues planning using the
feedback; the button/popup now follow the active VS theme instead of hardcoded white. Not yet
separately re-verified in this pass: "auto-accept" skipping subsequent Edit/Write prompts, and
"manually approve edits" still prompting per edit - both reuse pre-existing, previously-verified
session-permission mechanisms and were not flagged as broken by the user.
