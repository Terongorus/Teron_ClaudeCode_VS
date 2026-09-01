# Phase 11 - Native Side-by-Side Diff Tab (FEAT-2)

**Date:** 2026-08-29

Fifth phase of the baseline-parity implementation (continues [Phase 10](Phase%2010%20-%20Terminal%20Hand-off%20and%20Injected%20Commands%20%28GAP-1..GAP-3%29.md)).
Implements FEAT-2.

## What was done

Opens a real VS diff tab titled `[Claude Code] <file>` alongside the existing inline chat card,
automatically when Claude asks permission to edit or write a file, and on demand from either card.

VS supplies the navigation half of baseline's five toolbar buttons for free (Previous/Next
difference, plus the view-mode switch), read out of the live UIA tree rather than assumed. Accept
and revert cannot be had this way: `IVsDifferenceService` is read-only browsing UI that takes no
custom commands - already established when the MCP `openDiff` path was built (Phase 3). They stay
on the chat card - the tab is the *view*, the card is the *control*. Recorded as a deliberate
deviation, not a gap.

Both sides are read-only temp files, so a viewer who types into a pane cannot end up "editing"
scratch while believing they edited their real file. Cleanup is done by the extension rather than
VS, since `File.Delete` throws on a read-only file.

## A wrong-but-plausible first version, caught by a live run

The "before" side of an already-applied edit cannot come from the tool input - a `Write` call
carries no record of what it overwrote - so `ViewModels/SessionCheckpointStore.cs` reads the CLI's
own checkpoint store instead. Its first version was wrong in a way that looked right: the CLI
writes a `file-history-delta` only the **first** time it backs up a file, and afterwards carries it
forward in each turn's `file-history-snapshot`. Reading deltas alone returns a real backup of the
right file from the wrong point in its history - a plausible wrong diff, worse than no diff at all.
Caught by a live run, corrected, and re-verified with a `Write`, which cannot be
reverse-reconstructed by design (so it proves the store is reading a real snapshot, not
reconstructing one).

## Verification

52 checks across three scripts, all passing:

- `phase-e-verify.ps1` (21) — the Edit path end to end in VS 18 Exp.
- `phase-e-verify-write.ps1` (7) — proves the applied "before" comes from the CLI store and not
  from reconstruction.
- `phase-e-unit.ps1` (24) — the branches a live session cannot reach (`ReverseApply`,
  `replace_all`, file creation, stale-temp sweep, every refusal string), driven against the real
  built assembly by reflection, with no IDE and no focus stolen.

`uia-lib.ps1` gains `Expand-UiaByLabel` and `Find-InvokableByName`; six harness traps that each
first presented as a product bug are recorded in the backlog's Phase E notes.

**Files:** `Core/ClaudeCodeChatControl.xaml.cs`, `Controls/DiffViewer.xaml.cs`,
`ViewModels/SessionCheckpointStore.cs` (new),
`docs/comparison-audit/scripts/phase-e-verify.ps1`, `phase-e-verify-write.ps1`, `phase-e-unit.ps1` (new)

Commit: `aae69ab` "Phase E: native side-by-side diff tab (FEAT-2)"
