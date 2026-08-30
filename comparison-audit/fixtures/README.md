# Captured CLI output used as test fixtures

Real output from the real Claude Code CLI, kept verbatim so the harnesses under `../scripts` can be
re-run later without needing the session that produced it to still exist.

## `rewind-session-original.jsonl` / `rewind-session-forked.jsonl`

Captured 2026-08-30, CLI v2.1.251, while establishing FEAT-1's mechanisms before any of it was
built. A throwaway session in a scratch directory was given two turns:

1. *"Create a file note.txt … whose entire contents are the single word ALPHA."*
2. *"Now change note.txt so its entire contents are the single word BETA."*

`rewind-session-original.jsonl` is that session's transcript. It is the fixture for
`SessionCheckpointStore.ReadRewindPoints`, and it is worth having precisely because of what is
*not* obvious in it:

* it holds **four** `user` records but only **two** real prompts — the other two are tool-result
  relays, which a naive reader lists as rewind points the user never typed;
* the second turn edits a file the CLI was **already** tracking, so it has **no
  `file-history-delta` of its own** — the case that made an earlier, delta-only reading of this
  store return the right file from the wrong point in its history;
* the second prompt's preceding chain entry is an `assistant` record, which is the id
  `--resume-session-at` needs, and is not always the record's own `parentUuid`.

`rewind-session-forked.jsonl` is what
`--resume <id> --fork-session --resume-session-at <that assistant uuid>` produced from it, plus one
further trivial turn. It is the evidence for the fork half: a different session id, the first turn
kept intact, the BETA turn gone, and the original left untouched.
