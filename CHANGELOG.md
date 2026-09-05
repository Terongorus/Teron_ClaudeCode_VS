# Change Log

All notable changes to the **Claude Code for Visual Studio** extension will be documented in
this file.

## [0.6.0] - 2026-09-05

A batch of fixes from real day-to-day use of the extension, plus a first accessibility pass.

* **Fixed: session history showed every session on the machine, not just this workspace's.**
  History now only lists sessions started from the currently open solution/folder.
* **Fixed: changing the model, permission mode, or thinking level while Claude was still
  responding silently dropped the change.** It's now applied as soon as the current response
  finishes, instead of only taking effect after a manual restart.
* **Fixed: switching to a different docked tab and back could interrupt an in-progress
  response.** The chat panel no longer treats a tab switch the same as actually closing it.
* **Fixed: messages sent while Claude was still responding to an earlier one could render out of
  order**, making the conversation confusing to read back. They now appear next to the response
  that actually answers them.
* **Fixed: expanding a "Thinking" or tool-call section always scrolled the chat to the bottom.**
* **Improved:** the "Try again" button is now labeled "Resend," to match what it actually does
  (resends your original message as a new turn).
* **Improved: accessibility.** Icon-only buttons throughout the chat panel (Send, Stop, New
  session, History, Settings, and every panel-close button) now have real labels for screen
  readers, instead of announcing nothing meaningful.

## [0.5.0] - 2026-09-02

Fixes found during a live manual QA pass against the 0.4.0 build, plus one small feature
requested during that same pass.

* **New: clickable file/line references.** The `@path#Lstart-Lend` references the Active File and
  Selection chips write into the composer are now live links once a message has been sent —
  clicking one opens that file and selects the referenced lines.
* **Fixed: dictation could not be stopped by clicking the mic a second time.** The click that
  starts dictation was suppressing the click meant to stop it; tap-to-toggle now works both ways.
* **Fixed: tool-call output blocks rendered with a stark white background in the dark theme.**
* **Fixed: permission and choice-card keyboard shortcuts (`1`/`2`/`3`) stopped working after a
  previous card was answered by mouse.** Keyboard focus now returns to the composer whenever a new
  card appears.
* **Improved:** the live dictation status line now wraps instead of hard-truncating to one line.

## [0.4.0] - 2026-09-01

A full pass at parity with the official "Claude Code for VS Code" extension, driven by a live
side-by-side audit against it.

* **New: rewind and fork conversations.** Pick any earlier point in a session and restore your
  code to how it looked then, continue from there in a new forked conversation, or both — with a
  preview of exactly which files will change and a confirmation before anything on disk moves.
* **New: a native side-by-side diff tab.** Alongside the existing inline diff card, a proposed
  edit now also opens in a real Visual Studio diff editor tab, with Previous/Next-difference
  navigation built in.
* **New: real session titles.** History rows now show the same generated title Claude Code itself
  assigns to a session, instead of a truncated first message. Renaming a session yourself still
  always wins.
* **New: MCP servers panel** and **New: Manage plugins panel**, both reachable from Customize,
  showing your configured MCP servers and installed/available plugins and marketplaces without
  leaving the IDE.
* **New: a `+` add menu** on the composer — upload a file from disk, insert `@` to add context, or
  ask Claude to fetch a URL or search the web.
* **New: automatic model fallback.** Optionally set a fallback model in Options; when Claude
  switches models mid-session (due to load, a refusal, or a usage-credit boundary) the transcript
  now shows a clear notice explaining why.
* **New: voice dictation.** Tap or hold the new mic button (or `Ctrl+D`) to dictate into the
  composer using Windows' own offline speech recognizer — nothing is sent anywhere for this.
* **New: a Running sessions tab** in History, listing other Claude Code sessions active on this
  machine (including background agents) with the option to open one here or in a terminal; and a
  **Cloud tab** to hand off to a cloud session by ID or link.
* **New: terminal hand-off cards** for Memory, Agents, Hooks, Output Styles, and Permissions, and
  a new **Open Claude in Terminal** command, matching the official extension's Customize menu.
* **New: `/btw`, `/feedback`, and `/remote-control`** slash commands.
* **Redesigned visual style** to match the official extension more closely — a consistent type
  scale and corner radii throughout, and the accent color confined to exactly the surfaces it
  should be (send button, focus ring, selection, and this extension's own deliberately-kept user
  message bubble).
* **Improved: a large batch of UX polish** — model and permission-mode pickers now explain what
  each option does; permission prompts show the full file path, accept number-key selection
  (`1`/`2`/`3`), and can redirect Claude with a typed reason instead of just denying; the command
  palette gained a filter box; tool-call groups summarize count and failures; code blocks in
  responses gained a copy button; attachment chips show file dimensions and type; and new sessions
  show a proper empty state instead of a blank panel.
* **Fixed: the Active File / Selection context chips** silently doing nothing — or, worse,
  attaching the wrong file — when the active tab was a Markdown Preview tab rather than a code
  editor.

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
