# Phase 0 - Full CLI Parity Roadmap

**Date:** 2026-08-26

The user asked for **full native support for the `claude` CLI binary** in this extension -
everything available in the official Claude Code VS Code extension, replicated as native VS
UI/commands/settings rather than a thin chat box. Before touching any code, this phase surveyed
both sides of the gap and produced an approved implementation roadmap.

## Starting-state findings

**This extension** (`Teron_ClaudeCode_VS`, v0.2.0 at the time) wraps `claude` as a headless
`-p --input-format stream-json --output-format stream-json --include-partial-messages --verbose`
subprocess behind a single WPF tool window. One chat surface, no editor integration, minimal
Options page (CLI path, default model/permission-mode/effort, one input toggle), five VS commands
that all just open the same window.

**The official VS Code extension** (`anthropic.claude-code-2.1.246`) is a thin shell on top of the
CLI's own capability set - ~23 commands, a dozen settings, 3 webview views - but the CLI's bundled
`claude-code-settings.schema.json` (4657 lines) reveals the real feature surface: permission modes,
MCP, a 29-event-type hooks system, checkpoints/rewind, plan mode, subagents/teammates, worktrees,
remote control/cloud sessions, plugins/marketplace, voice, output styles, and more. The single
biggest "native" gap: VS Code has a hidden local `ide` MCP server (loopback WebSocket, lockfile
token handshake) giving the CLI live diagnostics, active-file/selection context, and inline-diff
editing - this extension has no equivalent; diffs render chat-only today.

Two real correctness bugs also turned up during the survey (see Phase 1's log for the fixes):
the permission-mode dropdown could send the CLI an invalid `--permission-mode default` value (the
real enum has no `default`), and "Stop" killed the whole process instead of interrupting it.

## Decisions (user-approved)

- Scope = everything the CLI can do, with a VS-native UI/equivalent even for VS Code's
  infra-heavy features (voice, remote control, marketplace) - not skipped.
- The IDE companion server (diagnostics + inline diff) is high-value, tackled early rather than
  deferred to the end.
- Correctness bugs fixed first, before any new feature work.

## The roadmap

Full detail lives in the approved plan (`C:\Users\kkole\.claude\plans\precious-zooming-spring.md`)
and is being executed as dated `/docs` phases from here on. Summary:

- **Correctness fixes** - invalid `--permission-mode default`, Stop=kill-not-interrupt, resumed
  sessions not restoring the visible transcript, a dead `MessageStopEvent` branch, a dead
  `SendOnCtrlEnter` setting, a missing `xhigh` effort level.
- **Core CLI flag parity** - `--add-dir`, `--allowedTools`/`--disallowedTools`,
  `--append-system-prompt`/`--system-prompt`, `--mcp-config`/`--strict-mcp-config`, following the
  extension's existing Options-page-driven argument pattern.
- **IDE companion server** - loopback WebSocket MCP server, VS error-list diagnostics bridge,
  inline editor diff apply/accept/reject.
- **Session & protocol depth** - MCP server management UI (wrapping `claude mcp`), a
  [Plan Mode document view](Phase%204%20-%20Plan%20Mode%20Review%20UI.md), checkpoints/rewind,
  richer subagent/background-agent visibility. Paste/drag-and-drop image and file import,
  transcript view modes, a live session status line, and a running-tasks panel landed as a
  [Chat UX batch](Phase%205%20-%20Chat%20UX%20Batch.md), followed by a full automated live
  [comparison audit](Phase%206%20-%20Live%20Comparison%20Audit.md) against the real extension
  covering functional, visual, and stability parity.
- **Editor & VS-native integration** - Solution Explorer/editor context actions, keybindings,
  multi-session tabs, `@`-mention enhancements, an accessibility audit pass.
- **Long-tail parity** - worktrees, remote control/cloud sessions, plugins/marketplace (wrapping
  `claude plugin`), voice/dictation (`System.Speech.Recognition`), output styles/status line.

Each phase gets its own detailed design pass (and, where the CLI's protocol behavior isn't already
documented, live verification against the real binary) when it's actually started, rather than
committing to unverified wire-protocol details this far ahead.

## Verification

N/A - this phase is research and planning only, no code changed. See `Phase 1` for the first
implementation pass and its verification.
