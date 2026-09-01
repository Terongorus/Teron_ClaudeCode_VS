# Phase 12 - Generated Session Titles (FEAT-3)

**Date:** 2026-08-30

Sixth phase of the baseline-parity implementation (continues [Phase 11](Phase%2011%20-%20Native%20Side-by-Side%20Diff%20Tab%20%28FEAT-2%29.md)).
Implements FEAT-3.

## What was done

Session history rows previously showed the truncated first message ("Use the Edit tool to replace
the word ALPHA with BRAVO in…"). The CLI already names its own sessions and persists the result in
the transcript, so this is a **read**, not a generation problem: 24 of the 26 rows in the real
history file on this machine got a better title on the next history open.

`ViewModels/SessionTitleReader.cs` reads `ai-title` / `custom-title` records, written against 99
real transcripts rather than an assumed format:

- Both record types are re-emitted every turn - one transcript holds 236 of them - and the
  generated title is genuinely revised along the way, so the **last** record of a kind is the
  current one.
- The last title record in the file is not automatically the answer. A user's `custom-title` is
  routinely followed by a later `ai-title`, and the real client still shows the custom one. Last
  custom wins outright; `ai-title` answers only when no custom title was ever set.
- Field order varies across records, so they're parsed as JSON, not matched positionally.

Transcripts on this machine reach 45 MB and history holds 100 rows, so the reader takes a 1 MB
window off the end of the file and falls back to a full scan only when that window holds no title
(9 ms vs. 202 ms measured on the 45 MB file). Each row records the transcript's size and write
time, so an unchanged file is never read twice, and the refresh runs off the UI thread and is
applied back on the dispatcher.

A rename typed in the extension's own history overlay wins permanently:
`CommitSessionEntryTitle` sets a persisted `HasUserTitle` flag, honoured even when the rename lands
while a background refresh is already in flight (a real race, made deterministic and verified -
see below).

## Verification

Verified by two scripts, 52 checks, neither needing Visual Studio:

- `phase-f-unit.ps1` (36) — the reader against real transcripts in every shape that occurs, plus
  the store's skip/stamp branches.
- `phase-f-vm.ps1` (16) — drives the real view model on an STA thread and pumps the dispatcher
  itself, which makes the rename-during-refresh race deterministic rather than a coin flip.

Both harnesses first reported failures that turned out to be their own marshalling bugs, including
two checks that passed vacuously - written up in the backlog's Phase F notes along with the format
findings. (These same two scripts were later ported to real xUnit tests - `SessionTitleTests` and
`SessionTitleRefreshTests` - as part of Phase 17.)

**Files:** `ViewModels/SessionHistoryStore.cs`, `ViewModels/SessionHistoryEntry.cs`,
`ViewModels/SessionTitleReader.cs` (new),
`docs/comparison-audit/scripts/phase-f-unit.ps1`, `phase-f-vm.ps1` (new)

Commit: `9cd5bc2` "Phase F: generated session titles (FEAT-3)"
