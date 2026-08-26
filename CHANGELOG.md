# Change Log

All notable changes to the **Claude Code for Visual Studio** extension will be documented in
this file.

## [0.2.0] - 2026-08-26

* **Renamed the extension's underlying identity.** The GitHub repo (and this project's own
  folder) is now `Teron_ClaudeCode_VS` (was `ClaudeCode_CLI_GUI`), and the technical identity
  (namespace, `AssemblyName`/`RootNamespace`, VSIX `Identity Id`/`Publisher`) is now
  `TeronClaudeCodeVS` (was `ClaudeCodeGUI`). The **Claude Code for Visual Studio** display name
  and all features are unchanged. Pre-1.0 with no prior releases, so there's no existing-install
  migration concern.
* **Relicensed from Apache-2.0 to GPL-3.0**, matching the license used across the rest of this
  developer's public extensions and applications.
* **New: Self-Update via GitHub Releases.** The extension now checks its own GitHub Releases
  (never the VS Marketplace) once a day for a newer version and offers to download and install
  it via an in-IDE notification — **Tools → Claude Code: Check for Updates** runs this check on
  demand. No VS Marketplace publishing is used or planned.
* Repository branch model is `dev`/`release` (unchanged from before this rename).

## [0.1.0] - 2026-06-28

* **Initial feature set**: streaming chat with Markdown rendering; tool call visualization for
  `Read`/`Edit`/`Write`/`Bash`/`Grep`/`Glob`/`WebFetch`/`Task`/`TodoWrite`/MCP tools; inline
  Allow/Deny permission prompts; a consolidated `/` command menu for model, permission mode, and
  thinking budget, plus live session usage; model switching (Default/Sonnet/Opus/Haiku/Fable);
  permission mode switching (Default/Accept Edits/Plan Mode/Bypass Permissions); thinking budget
  control; "Add Active File"/"Add Selection" context references; slash command autocomplete;
  session persistence with crash-recovery `--resume`; a raw NDJSON output panel for debugging.
