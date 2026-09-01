# Phase 16 - Voice Dictation and Running/Cloud Sessions (FEAT-8, FEAT-9)

**Date:** 2026-08-31

Tenth phase of the baseline-parity implementation (continues [Phase 15](Phase%2015%20-%20Rewind%20and%20Fork%20%28FEAT-1%29.md)).
Implements FEAT-8 and FEAT-9.

## FEAT-8 - voice dictation that never leaves the machine

`System.Speech.Recognition` is a .NET Framework assembly wrapping SAPI, so there is no key to
configure, nothing to ship alongside it, and no audio going anywhere - the tradeoff being that the
desktop recognizer is much weaker than a hosted model. Baseline's tooltip is carried verbatim,
"Tap or hold to record · Ctrl+D", and both halves are real: a tap toggles, a hold over 400ms
records only while held.

Two things it refuses to do quietly. A disabled mic always says why, and the two reasons are asked
at different moments - whether a recognizer is installed is probed at load without touching the
microphone (`VoiceInput.Probe()`), while whether there is a capture device can only be found out by
asking for one, so it surfaces on the first press. And it works without a mouse: the button carries
a `Click` handler alongside the mouse-down/up pair, so a keyboard, a screen reader and UI Automation
can all press it, with a flag (`_micGestureHandled`) keeping the two paths from cancelling each
other.

## FEAT-9 - three parts, and the CLI has two of them

- **Sessions on this machine** — `claude agents --json --all`, whose own help says it does not
  require a TTY, which is what makes it usable from a tool window. The field set **changes** with
  the session's state, established by watching one agent through its whole life rather than by
  reading a schema: `pid` means a process is running it right now, `id` means it is a background
  agent with the short id `attach`/`logs`/`stop` take, and `status` only ever accompanies a live
  background one. Both captures are kept as fixtures (`agents-live-background.json`,
  `agents-all.json`), because a parser that required any of those fields passes one and fails the
  other. `--all` was measured the same way: only after `claude stop` does the plain form drop an
  agent while `--all` keeps it.
- **A cloud session by id or URL** — as a terminal hand-off, because the CLI refuses in as many
  words: `"--cloud <session_id> does not support --output-format stream-json"`, and stream-json is
  the entire protocol this panel speaks. Its own validator is transcribed into the paste box to
  catch a typo before a terminal opens, but what the user typed is passed through unchanged so a
  rejection comes back in the CLI's words.
- **Listing an account's cloud sessions** — **not possible**. No command enumerates them; the only
  cloud-facing flags anywhere are `--cloud` and `--environment`. Baseline's History > Web tab talks
  to an account endpoint the CLI does not expose. The Cloud tab says exactly that on its face
  rather than showing a list it cannot build.

Every refusal in the Running tab is the CLI's own constraint rather than caution, and states its
own reason on the row: nothing joins a live interactive session, a second process on one
conversation is what `claude attach` exists to prevent, and opening a session in this panel needs
it to belong to the folder open in the IDE - which is where `--resume` looks for the transcript,
what `@`-references resolve against, and what the IDE companion server reports.

## Verification

139 checks, 45 of them against a live experimental instance:

- `phase-j-unit.ps1` (94) — the parser against two real captures of the SAME agent, alive and
  stopped, with a control that re-parses one of them as though the IDE were open on the agent's own
  folder. Plus dictation actually running: a sentence synthesised to a `.wav` and fed through
  `VoiceInput`'s real pipeline, one line different from the microphone path, with a silence control
  proving the check can fail.
- `phase-j-live.ps1` (45) — a real IDE. The mic driven through `InvokePattern`; the three history
  tabs; a Running list of real sessions with one row openable and six refusing with reasons; the
  Cloud tab's validation and its stated gap; and the must-pass - a real background agent, started
  in the solution folder, opened in the panel from its own row, with its prompt **and** its answer
  read back out of the rendered documents.

Five harness defects along the way, three of them false failures - the mirror of Phase 15's
lesson: `@($json | ConvertFrom-Json)` yields one object that *is* the array, so a later
`$_.id -eq $x` filters the array and matches every row; a `Register-ObjectEvent -Action` block does
not share the script's `$script:` scope, so a working feature reported as broken; a scriptblock
cast to a delegate and raised from a thread with no runspace took the process down with a
`StackOverflowException`; automation elements captured before a list is rebuilt are stale handles;
and a background agent inherits the shell's directory, so one was created in the wrong folder and
the check fell through to an unrelated transcript.

And one check that passed when it should not have: "the answer came back" matched the word `PONG`,
which also appears in the prompt asking for it. It now requires the word in a document that is
**not** the prompt, and the run prints both documents.

**Not covered:** the Cloud tab's button is never pressed live, because launching a terminal takes
the foreground from whatever the user is doing - the command it builds is unit-tested and that
exact command line was run directly against the CLI, which answered with a real server-side
rejection, but the click is unexercised. The "no recognizer" and "no microphone" branches are
constructed and asserted, never provoked - this machine has both. Build is clean - 0 warnings, 0
errors.

**Files:** `TeronClaudeCodeVS.csproj` (`System.Speech` reference),
`Core/ClaudeCodeChatControl.xaml(.cs)`, `Core/VoiceInput.cs` (new),
`ViewModels/AgentSessionsViewModel.cs` (new), `ViewModels/ChatSessionViewModel.cs`,
`Core/TerminalLauncher.cs`,
`docs/comparison-audit/fixtures/agents-all.json`, `agents-live-background.json` (new),
`docs/comparison-audit/scripts/phase-j-unit.ps1`, `phase-j-live.ps1` (new)

Commit: `0168936` "Phase J: voice dictation and running/cloud sessions (FEAT-8, FEAT-9)"

Two follow-up commits landed the same session, correcting the record rather than changing code:
`0756d7a` consolidated every unexercised item across all phases so far into one "needs a human"
checklist in the backlog, and `81e6944` corrected a factual error about the CLI binary appearing to
report two versions (it doesn't - the standalone CLI auto-updates under observation; the extension
runs its own pinned copy).
