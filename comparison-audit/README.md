# Comparison audit — Teron_ClaudeCode_VS vs. real Claude Code for VS Code

Working folder for the live, automated, side-by-side comparison against the officially-installed
"Claude Code for VS Code" extension. Started 2026-08-28, in progress.

- **➤ Start here for planning:** [`implementation-backlog.md`](implementation-backlog.md) — the
  findings reorganised by *work item* (ID, size, evidence, acceptance criteria measured off the
  baseline), tiered by value-for-effort with a suggested sequencing. This is the reference to
  build an implementation plan from. Work-item IDs (`BUG-1`, `ST-1`, `UX-3`, `FEAT-2`, …) are
  stable — quote them in plans and commit messages.
  - [`implementation-backlog.html`](implementation-backlog.html) — the same content as a
    filterable page (tier chips, "quick wins only", keyword search). Published privately as an
    Artifact at <https://claude.ai/code/artifact/b850b357-11d7-4abc-be1a-1ed7f9999ce5>. The
    Markdown file is the source of truth; regenerate the HTML from it if they diverge.
- **Narrative + methodology + bugs found:**
  [`../docs/Phase 6 - Live Comparison Audit.md`](../docs/Phase%206%20-%20Live%20Comparison%20Audit.md)
- **Scannable checklist / audit view:** [`feature-matrix.md`](feature-matrix.md) — organised by
  feature area for *auditing* (baseline documented first, ours checked against it). Use the
  backlog above for planning; use this to check *why* a backlog item exists.
- **Screenshots:** `screenshots/our-extension/` and `screenshots/real-extension/`, numbered to
  match the narrative doc and matrix
- **Automation scripts** (background-safe — no stolen focus, no physical mouse/keyboard
  simulation): `scripts/`
  - `uia-lib.ps1` — Windows UI Automation helpers for driving our WPF tool window
    (`Get-MainWindowByPid`, `Find-ByAutomationId`, `Find-ByName`, `Invoke-UiaClick`,
    `Set-UiaValue`, `Get-UiaDoubleClick`)
  - `cdp-lib.ps1` — Chrome DevTools Protocol client (raw WebSocket, no Node/Playwright needed)
    for driving the real extension's webview, including `Get-ClaudeCodeWebviewContext` which
    handles VS Code's double-nested webview frame structure
  - `screenshot.ps1` — `PrintWindow`-based window capture (not `CopyFromScreen`) so screenshots
    work regardless of window focus/foreground state
  - `screenshot-composite.ps1` — same `PrintWindow` approach, but also finds and composites any
    open WPF `Popup` windows (they're separate top-level HWNDs, not children — a plain
    `screenshot.ps1` capture never shows an open dropdown menu at all). Use this one whenever a
    popup/dropdown needs to be in the shot.

This folder is a working audit trail, not shipped extension code — update it as later passes add
more findings rather than creating a parallel set of files.
