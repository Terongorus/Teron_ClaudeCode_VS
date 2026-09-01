# Phase 13 - MCP Servers and Plugins Panels (FEAT-4, FEAT-5)

**Date:** 2026-08-30

Seventh phase of the baseline-parity implementation (continues [Phase 12](Phase%2012%20-%20Generated%20Session%20Titles%20%28FEAT-3%29.md)).
Implements FEAT-4 and FEAT-5 - the two entries in baseline's Customize section that open a real GUI
panel instead of handing off to a terminal. Neither panel owns any state; both are windows onto the
CLI, through a new shared runner.

## FEAT-4 - MCP servers panel

Backed by `claude mcp list`. That command has no `--json` flag (its only option is `-h`), so the
panel parses text - and the format was read out of the shipped binary's own renderer rather than
guessed, along with its closed nine-string status vocabulary. Two defects that only that vocabulary
could reveal were found by the harness and fixed:

- `"Rejected (see disabledMcpjsonServers in settings)"` was classified as **Disabled** because the
  status names the setting that caused it - actually an error state, since the server is refused,
  not turned off.
- `"- Not configured"` was splitting one character late, because that status begins with the
  parser's own field separator's characters.

## FEAT-5 - Manage plugins panel

Baseline's Plugins / Marketplaces tab strip, backed by `claude plugin list --json --available` and
`claude plugin marketplace list --json`. Both JSON shapes the CLI can return (object with
`installed`/`available` keys, or a bare array) are accepted.

The MCP empty state is the sentence the CLI itself printed, not a copy, so the two cannot drift.
The plugins empty state diverges deliberately: baseline's "add a marketplace to discover plugins"
is right only when no marketplace exists, so the CLI's own "no plugins installed" is shown once one
does.

## Shared infrastructure

`Core/ClaudeCliQuery.cs` is the shared run-and-capture helper the plan called for;
`AccountUsageViewModel`'s private copy now delegates to it. It adds three things that copy lacked:
a working directory (`mcp list` resolves project-scoped servers relative to it, so without this a
solution's own servers silently vanish), both pipes drained concurrently, and UTF-8 on both - the
statuses are glyphs.

## Verification

141 checks across `phase-g-unit.ps1` (99) and `phase-g-vm.ps1` (42), with no Visual Studio
instance: the parsers against real captured output and every status in the vocabulary, every
`{Binding}` path the new panels declare resolved against the real view-model types, and the view
models driving the real CLI - including two servers found in one directory and none in the
directory next door, which is the only way to prove the working directory is actually plumbed
through. Plugin state was exercised under a throwaway `CLAUDE_CONFIG_DIR`, with the user's own
configuration asserted unchanged before and after. Build clean: 0 warnings, 0 errors.

**Not covered:** the rendered XAML and the six Click handlers, deferred to TEST-1 (Phase 17).

**Files:** `Core/ClaudeCodeChatControl.xaml(.cs)`, `Core/ClaudeCliQuery.cs` (new),
`ViewModels/McpServersViewModel.cs`, `ViewModels/PluginsViewModel.cs` (new),
`docs/comparison-audit/scripts/phase-g-unit.ps1`, `phase-g-vm.ps1` (new)

Commit: `801b4b8` "Phase G: MCP servers and plugins panels (FEAT-4, FEAT-5)"
