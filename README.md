# Claude Code for Visual Studio

A Visual Studio extension that brings [Claude Code](https://docs.claude.com/en/docs/claude-code) into a
native tool window — chat with Claude, watch it read/edit files and run commands in your
project, and approve or deny each action, all without leaving the IDE.

This extension does not reimplement Claude Code. It is a thin, modern UI around the official
`claude` CLI, driven in its headless `stream-json` mode. Authentication, model access, and
billing are all handled by the CLI exactly as they would be for the
[official VS Code extension](https://code.claude.com/docs/en/vs-code) or the terminal —
installing/logging in once via `claude` makes it available here too.

## Features

- **Streaming chat** with full Markdown rendering (code blocks, tables, lists, inline code that
  respects your IDE theme, etc.)
- **Tool call visualization** — expandable cards for `Read`, `Edit`, `Write`, `Bash`, `Grep`,
  `Glob`, `WebFetch`, `Task`, `TodoWrite`, MCP tools, and more, each with an icon, a one-line
  summary, and full input/output/diff detail on demand
- **Inline permission prompts** — Allow/Deny `can_use_tool` requests right in the chat, with a
  real line-level diff preview for file edits
- **Answerable questions** — the `AskUserQuestion` tool renders as real radio buttons or
  checkboxes (single- and multi-select) instead of a dead-end approval card
- **IDE companion server** — gives the CLI live diagnostics from Visual Studio's own Error List,
  awareness of your open editors/active file/selection, and a real inline diff review flow: a
  proposed edit opens in a native VS diff window with Accept/Reject, instead of only ever
  rendering inside the chat
- **Consolidated `/` command menu** (matching the VS Code extension) for switching the model,
  permission mode, and thinking budget, viewing live session usage (turns/cost/tokens, plus
  real account/subscription info and 5-hour/weekly rate-limit bars), and running slash commands —
  picking one runs it immediately, and `/usage` opens the usage panel locally with no API cost
- **Model switching** — Default, Sonnet, Opus, Haiku, Fable
- **Permission mode switching** — CLI Default, Accept Edits, Manual, Don't Ask, Plan Mode, Auto,
  Bypass Permissions
- **Thinking/effort control** — Standard, Low, Medium, High, Max, Extra High, applied via
  `--effort`
- **File & selection context** — "Add Active File" / "Add Selection" insert `@path[#Lstart-Lend]`
  references relative to your solution
- **Slash command autocomplete**, sourced live from the running session
- **Message queuing** — you can keep typing while Claude is still working; each message queues
  and runs in order, exactly like the official extension
- **`/compact` support** — shows a "Compacting…" status and a "Compacted chat · N tokens freed"
  result, instead of a silent no-op
- **Retry on failure** — if a turn fails or the CLI process exits unexpectedly (including hitting
  a usage limit), a "Try again" resends your exact message once you're ready
- **Session persistence** — model/permission/thinking changes and crash recovery transparently
  `--resume` your conversation, restoring the full visible transcript from the CLI's own history
- **Extra CLI flag coverage** under **Tools → Options → Claude Code** — additional allowed
  directories, allowed/disallowed tools, system prompt append/replace, and MCP config file
  loading, on top of the CLI/model/permission/effort settings already there
- **Raw output panel** for debugging the underlying NDJSON protocol
- **Self-Update via GitHub Releases:** checks [GitHub releases](https://github.com/Terongorus/Teron_ClaudeCode_VS/releases)
  (not the VS Marketplace) once a day and offers to download and install the latest `.vsix`
  directly — **Tools → Claude Code: Check for Updates** triggers this on demand.

## Requirements

- Visual Studio 2022
- The [Claude Code CLI](https://docs.claude.com/en/docs/claude-code) installed and logged in
  (`claude` on your `PATH`, or installed via the official VS Code extension / `~/.claude/local`)

If the CLI can't be found automatically, set an explicit path under
**Tools → Options → Claude Code**.

## Usage

1. Open the tool window: **View → Other Windows → Claude Code** (or use the toolbar/Solution
   Explorer entry points).
2. Type a message and press **Enter** to send (**Shift+Enter** for a new line).
3. Use the **/** menu (bottom toolbar) to switch model, permission mode, or thinking budget,
   view session usage, or run a slash command. Use the **✚** header button to start a
   **New Session**.
4. When Claude wants to run a tool that requires approval, an inline card appears with
   **Allow** / **Deny** buttons.
5. Toggle **Raw** to see the underlying CLI event stream.

## Known limitations

- Plan Mode still shows the plan inline in chat rather than as a dedicated reviewable document.
- No UI yet for checkpoints/rewind, in-app MCP server management, worktrees, remote control/cloud
  sessions, the plugin marketplace, or voice input — all CLI features not yet surfaced natively.

## Building from source

```bash
dotnet build TeronClaudeCodeVS.csproj
```

Press F5 in Visual Studio to launch an experimental instance with the extension loaded, or
double-click the generated `bin/Debug/net481/TeronClaudeCodeVS.vsix` to install it into your main
Visual Studio.
