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

- **Streaming chat** with full Markdown rendering (code blocks, tables, lists, etc.)
- **Tool call visualization** — expandable cards for `Read`, `Edit`, `Write`, `Bash`, `Grep`,
  `Glob`, `WebFetch`, `Task`, `TodoWrite`, MCP tools, and more, each with an icon, a one-line
  summary, and full input/output/diff detail on demand
- **Inline permission prompts** — Allow/Deny `can_use_tool` requests right in the chat
- **Consolidated `/` command menu** (matching the VS Code extension) for switching the model,
  permission mode, and thinking budget, viewing live session usage (turns/cost/tokens), and
  running slash commands
- **Model switching** — Default, Sonnet, Opus, Haiku, Fable
- **Permission mode switching** — Default, Accept Edits, Plan Mode, Bypass Permissions
- **Thinking budget control** — Standard, Low, Medium, High, Max, applied via
  `MAX_THINKING_TOKENS`
- **File & selection context** — "Add Active File" / "Add Selection" insert `@path[#Lstart-Lend]`
  references relative to your solution
- **Slash command autocomplete**, sourced live from the running session
- **Session persistence** — model/permission/thinking changes and crash recovery transparently
  `--resume` your conversation
- **Raw output panel** for debugging the underlying NDJSON protocol

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

- No inline diff view in the text editor yet — proposed edits render as diffs inside the chat,
  and the Allow/Deny decision is the accept/reject step.

## Building from source

```bash
dotnet build ClaudeCodeGUI.csproj
```

Press F5 in Visual Studio to launch an experimental instance with the extension loaded, or
double-click the generated `bin/Debug/net481/ClaudeCodeGUI.vsix` to install it into your main
Visual Studio.
