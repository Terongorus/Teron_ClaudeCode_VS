# Phase 2 - Core CLI Flag Parity

**Date:** 2026-08-26

Second implementation pass of the [Full CLI Parity Roadmap](Phase%200%20-%20Full%20CLI%20Parity%20Roadmap.md),
following [Phase 1's](Phase%201%20-%20Correctness%20Fixes.md) correctness fixes. Adds the CLI flags
the extension didn't expose at all yet, using the exact same Options-page-driven pattern already
established for `--model`/`--permission-mode`/`--effort`.

## Findings and fixes

- **`--add-dir <directories...>`**, **`--allowedTools`/`--disallowedTools`**,
  **`--append-system-prompt`/`--system-prompt`**, and **`--mcp-config <configs...>` +
  `--strict-mcp-config`** are now all Options-page settings (`Additional Allowed Directories` under
  Defaults; `Allowed Tools`/`Disallowed Tools` under a new Tools category; `Append System
  Prompt`/`System Prompt (replace)`/`MCP Config Files`/`Strict MCP Config` under a new Advanced
  category), each using a `MultilineStringEditor` for the multi-value ones (already available via
  the project's existing `System.Design` reference, no new dependency).
- **`ClaudeCodeSession.Start` grew a `ClaudeSessionStartOptions` bundle parameter** rather than
  six more positional parameters on top of the six it already had - model/permission-mode/effort/
  resume stay direct parameters since those are the ones actually switchable live from the chat
  popup; the new settings aren't (Options-page-only, read once at startup).
  Directory/file-path lists split on newline only (a path can contain spaces); tool-name lists
  split on any whitespace, matching the CLI's own "comma or space-separated" acceptance.
- **Explicitly deferred** (per the roadmap): `--dangerously-skip-permissions`/
  `--allow-dangerously-skip-permissions` (redundant with the already-correct `--permission-mode
  bypassPermissions` for this always-headless extension), `--setting-sources` (belongs with a
  later full config-management phase), `-c`/`--continue` (the extension's own session-history-based
  resume is strictly more capable).

## Verification

`dotnet build TeronClaudeCodeVS.csproj` - 0 warnings, 0 errors.

Live-verified against the real `claude.exe`, all flags together in one process (not just
individually, to catch any interaction issues):
- `--append-system-prompt "...respond with the exact phrase CUSTOM_PROMPT_ACTIVE"` - the model's
  actual reply was exactly `CUSTOM_PROMPT_ACTIVE`, confirming the flag is honored.
- `--disallowedTools Bash` - the `init` message's reported `tools` array (27 entries) had `Bash`
  removed entirely (present in every other test run in this session's verification work); confirmed
  by direct comparison, not just "no error was thrown."
- `--add-dir .`, `--allowedTools Read Glob`, `--mcp-config empty_mcp.json --strict-mcp-config` (a
  real, if trivial, `{"mcpServers":{}}` file) - all accepted with a clean `init` and a normal
  successful turn, no startup errors.
