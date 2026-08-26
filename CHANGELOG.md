# Change Log

All notable changes to the **Claude Code for Visual Studio** extension will be documented in
this file.

## [0.3.0] - 2026-08-26

* **New: IDE companion server.** The extension now runs a local companion server the CLI can
  connect to, the same way the official VS Code extension does — giving Claude live diagnostics
  from Visual Studio's own Error List, awareness of your open editors/active file/selection, and
  a real inline diff review flow (a proposed edit opens in a native VS diff window with
  Accept/Reject) instead of only ever rendering inside the chat.
* **New: answerable questions.** The `AskUserQuestion` tool now renders as real radio buttons or
  checkboxes (single- and multi-select) you can actually answer, instead of a dead-end Allow/Deny
  card.
* **Fixed: permission prompts for built-in tools (Edit/Write/Bash/…) not appearing at all.** Root
  cause was a missing CLI flag; this also fixes the inline diff/permission flow for proposed edits.
  Diff previews inside permission cards are now a real line-level diff instead of a raw dump.
* **New: `/compact` support.** Shows a "Compacting…" status and a "Compacted chat · N tokens
  freed" result, instead of silently doing nothing useful.
* **New: message queuing.** You can keep typing while Claude is still working — each message
  queues and runs in order, matching the official extension, instead of the input being blocked.
* **New: retry on failure.** If a turn fails or the CLI process exits unexpectedly (including
  hitting a usage limit), a "Try again" resends your exact message once you're ready.
* **New: a real Account & Usage panel** — shows your actual account/subscription info and live
  5-hour/weekly rate-limit usage, instead of the empty placeholder it showed before.
* **Fixed: inline code in chat responses** rendering as a harsh, theme-incorrect solid block
  regardless of your IDE theme.
* **Fixed: Stop now sends a real interrupt** instead of killing and restarting the CLI process;
  resuming a past session now restores the full visible transcript, not just the session ID.
* **Fixed: an invalid default permission-mode value** the CLI would have rejected; the permission
  mode list now matches the CLI's real options (added Manual and Don't Ask, which were missing).
* **New: additional CLI flag coverage** under Tools → Options → Claude Code — additional allowed
  directories, allowed/disallowed tools, system prompt append/replace, and MCP config file
  loading.
* **New: Extra High** added to the thinking/effort level options.
* Slash commands picked from the `/` menu now run immediately instead of requiring a manual
  Enter; `/usage` now opens the usage panel locally instead of sending a wasted message to Claude.

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
