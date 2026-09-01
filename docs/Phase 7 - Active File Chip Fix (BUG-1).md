# Phase 7 - Active File Chip Fix (BUG-1)

**Date:** 2026-08-29

First implementation pass of the plan that followed [Phase 6](Phase%206%20-%20Live%20Comparison%20Audit.md)'s
live comparison audit: bringing this extension up to parity with all 33 items in
`docs/comparison-audit/implementation-backlog.md`, in dependency order, one commit per phase on
`dev`. This phase fixes the one real functional bug the audit found (BUG-1) before any of the
33 backlog items - a defect in code the audit exercised live, not a parity gap.

## The bug

The "Active File" chip did nothing at all when the active tab was a Markdown Preview tab - no
reference inserted, no context sent to Claude, no error shown. Found by live verification against
the official extension on 2026-08-28 (Phase 6).

## Root cause

`ClaudeCodeChatControl.xaml.cs`'s `OnAddSelectionClicked`/active-file resolution path called
`VS.Documents.GetActiveDocumentViewAsync()`, which only resolves tabs backed by a real text view.
It does not describe the tab actually on screen. On a Preview tab it returns either `null`, or -
worse - whichever *text* document happened to be active last, which is a wrong answer rather than
no answer. Live re-testing caught exactly that second variant: with two documents open, the chip
inserted a file the user was not looking at.

## Fix

Resolve the active document through the shell's `SEID_DocumentFrame` instead, falling back to the
document view and then EnvDTE's `ActiveDocument.FullName`. `SEID_DocumentFrame` is the
authoritative source and, unlike `SEID_WindowFrame`, is unaffected by the Claude Code tool window
taking focus when the chip is clicked. When nothing resolves, the extension now says so visibly in
the chat transcript instead of returning silently.

The Selection chip had the same class of defect: it read a selection out of a text view that
could belong to a different file than the active tab. It now cross-checks the resolved view
against the resolved path and degrades to a whole-file reference, with a visible notice, when they
disagree.

## Verification

Verified live in the experimental instance against the original failing tab, driven through UI
Automation:

- Markdown `[Preview]` tab active -> inserts that file (previously: nothing).
- Code tab active -> unchanged, still correct (regression check).
- Selection on a Preview tab -> whole-file reference plus a visible notice.

Evidence: `docs/comparison-audit/screenshots/our-extension/26-BUG1-fixed-preview-tab.png`.

**Files:** `Core/ClaudeCodeChatControl.xaml.cs`

Commit: `afbfc32` "Fix BUG-1: Active File / Selection chips resolve the wrong file, or none"
