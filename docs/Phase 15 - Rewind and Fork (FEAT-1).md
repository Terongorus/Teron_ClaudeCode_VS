# Phase 15 - Rewind and Fork (FEAT-1)

**Date:** 2026-08-30

Ninth phase of the baseline-parity implementation (continues [Phase 14](Phase%2014%20-%20Add%20Menu%20and%20Automatic%20Model%20Fallback%20%28FEAT-6%2C%20FEAT-7%29.md)).
Implements FEAT-1 - the backlog's one XL item, and far smaller than that once it was clear the CLI
already does all three parts itself. Everything below was measured against the real binary
(v2.1.251) before any of it was built, using a throwaway session that wrote `ALPHA` to a file and
then changed it to `BETA`.

## What the CLI already does

- **Rewind code** — the `rewind_files` control request, on the same stdin/stdout channel this
  extension already used for interrupt. Takes a user-message uuid, answers with the files it would
  restore and the insertion/deletion counts.
- **The confirmation** — the same request's `dry_run`. It returned the real file list and `+1 -1`,
  and the file on disk was still `BETA` afterwards; the same call with `dry_run: false` put it back
  to `ALPHA`.
- **Fork** — `--fork-session` plus the hidden `--resume-session-at`. Forking the two-turn session
  at the first turn's last entry produced a new session id, a transcript holding turn one and the
  new prompt only, and an untouched original. That flag keeps everything up to **and including**
  the id it is given, so forking "from" a message resumes at the entry before it - the nearest
  preceding assistant/user record, which is baseline's own rule and is not always the record's
  `parentUuid`.

This changes the plan's original design deliberately: it called for walking `file-history-delta`
records and writing the CLI's backups back ourselves. `SessionCheckpointStore` (built in
Phase 11) instead stays a read, and the restore is asked of the CLI. Re-deriving its rules from
outside - which paths it refuses, what counts as already tracked, how a symlink is handled -
would be wrong the first time any of them changed, and this store's own history in this repo (see
Phase 11) is a reminder that a plausible wrong answer is worse than none.

## Surfaces

Two surfaces, three actions. Baseline's copy is carried verbatim throughout, read out of its
webview bundle. One deliberate difference: baseline's picker only ever does "restore code and
fork" together and keeps the three-way choice for the per-message menu, but FEAT-1's own
acceptance criterion is that the two are independently selectable from **both** surfaces - so a
picker row is selected first and then offered the same three actions. A fork alone writes nothing
to the working tree and runs immediately; anything that restores files stops at the confirmation,
which is where the dry run is shown.

## Verification

127 checks, 59 of them against a live experimental instance:

- `phase-i-unit.ps1` (68) — the transcript reader against a captured real session, kept as a
  fixture because it holds the two things that make a naive reader wrong: tool-result relays that
  are also `user` records, and a second edit to an already-tracked file. Plus the fork flags read
  off a really spawned command line, since `Start` spawns the process in the same breath as it
  assembles them.
- `phase-i-live.ps1` (59) — a real IDE, end to end. Two real Haiku turns create and then change a
  scratch file; the picker lists exactly the two prompts; a real dry run names the real file;
  "Never mind" leaves the disk alone; and then the file on disk goes back to `ALPHA` and a fork
  produces a different session id, a trimmed view, a prefilled composer, and a transcript holding
  the kept turn and not the dropped one.

Four harness defects were found along the way, three of them checks that **passed when they
should not have**: a `Popup` has no automation peer, so asking whether one exists asks about
something that never exists; a user message renders into a `FlowDocument`, which UIA exposes with
an empty `Name`, so a `Name` sweep cannot see it; the "sessions that existed before" list held file
names and was compared against bare ids, so it excluded nothing and the fork's id check passed
against an earlier run's transcript; and "the turn finished" passed instantly because `Ready` is
also what the status says before anything is sent. A fifth issue was not a defect: retrying a
toggle by clicking again just closes what it opened.

**One real defect** the live run found and this phase fixes: the picker's rows announced
themselves to the accessibility tree as the CLR type name, because a `ListBoxItem` with no
`AutomationProperties.Name` falls back to `ToString()`. `RewindPoint.ToString()` now returns the
prompt text.

**Not covered:** a rewind that has to refuse a path - a symlink, or a file whose directory moved -
has been run only through fixtures. Build is clean - 0 warnings, 0 errors.

**Files:** `ViewModels/SessionCheckpointStore.cs` (`ReadRewindPoints`, `RewindPoint`),
`ViewModels/ChatSessionViewModel.cs`, `Core/ClaudeCodeSession.cs`,
`Core/ClaudeCodeChatControl.xaml(.cs)`,
`docs/comparison-audit/scripts/phase-i-unit.ps1`, `phase-i-live.ps1` (new)

Commit: `c49de52` "Phase I: rewind and fork (FEAT-1)"
