using Microsoft.VisualStudio.Shell;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.Drawing.Design;
using System.Runtime.InteropServices;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>Tools &gt; Options &gt; Claude Code &gt; General.</summary>
    [ComVisible(true)]
    [Guid("739B03DA-CA11-4152-8B28-F74674AA9497")]
    public class ClaudeCodeOptionsPage : DialogPage
    {
        // ─── CLI ────────────────────────────────────────────────────────────────

        [Category("CLI")]
        [DisplayName("Claude CLI Path")]
        [Description("Full path to claude.exe (or a folder containing it). Leave blank to auto-detect from PATH, ~/.claude/local, or the Claude Code VS Code extension install.")]
        public string ClaudeExecutablePath { get; set; } = "";

        // ─── Defaults ───────────────────────────────────────────────────────────

        [Category("Defaults")]
        [DisplayName("Default Model")]
        [Description("Model used when starting a new session: blank (CLI default), sonnet, opus, haiku, or fable.")]
        [TypeConverter(typeof(ModelConverter))]
        public string DefaultModel { get; set; } = "";

        [Category("Defaults")]
        [DisplayName("Default Permission Mode")]
        [Description("Permission mode used when starting a new session. 'acceptEdits' (recommended) auto-approves file edits and asks for shell commands. 'manual' asks for everything. Blank = let the CLI use its own configured default.")]
        [TypeConverter(typeof(PermissionModeConverter))]
        public string DefaultPermissionMode { get; set; } = "acceptEdits";

        [Category("Defaults")]
        [DisplayName("Default Effort Level")]
        [Description("Reasoning effort level passed to --effort when starting a session. Blank = CLI default.")]
        [TypeConverter(typeof(EffortConverter))]
        public string DefaultEffortLevel { get; set; } = "";

        [Category("Defaults")]
        [DisplayName("Additional Allowed Directories")]
        [Description("Extra directories (one per line) the CLI is allowed to read/write outside the working directory, passed via --add-dir.")]
        [Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
        public string AdditionalDirectories { get; set; } = "";

        [Category("Defaults")]
        [DisplayName("Switch Models Automatically")]
        [Description("When the selected model is overloaded, unavailable, or refuses a turn, let the CLI continue on the fallback model below instead of failing the turn (--fallback-model). The switch is announced in the transcript. Note that the CLI has its own separate, on-by-default setting for safeguard refusals, so a switch can still be announced with this turned off.")]
        public bool SwitchModelsAutomatically { get; set; } = false;

        [Category("Defaults")]
        [DisplayName("Fallback Model")]
        [Description("Model to fall back to when 'Switch Models Automatically' is on: an alias (haiku, sonnet, opus, fable) or a full model name. Accepts a comma-separated list to try each in order. Ignored while the setting above is off.")]
        [TypeConverter(typeof(ModelConverter))]
        public string FallbackModel { get; set; } = "haiku";

        // ─── Input ──────────────────────────────────────────────────────────────

        [Category("Input")]
        [DisplayName("Send on Ctrl+Enter")]
        [Description("When true, Ctrl+Enter sends a message; Enter inserts a newline. When false (default), Enter sends and Shift+Enter inserts a newline.")]
        public bool SendOnCtrlEnter { get; set; } = false;

        // ─── Tools ──────────────────────────────────────────────────────────────

        [Category("Tools")]
        [DisplayName("Allowed Tools")]
        [Description("Tool names to allow, e.g. \"Bash(git *)\" or \"Edit\" (space/newline-separated). Passed via --allowedTools.")]
        [Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
        public string AllowedTools { get; set; } = "";

        [Category("Tools")]
        [DisplayName("Disallowed Tools")]
        [Description("Tool names to deny (space/newline-separated). Passed via --disallowedTools.")]
        [Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
        public string DisallowedTools { get; set; } = "";

        [Category("Tools")]
        [DisplayName("Open a Diff Tab for Proposed Edits")]
        [Description("When Claude asks permission to edit or write a file, also open a native side-by-side diff tab in the editor (FEAT-2). The inline diff in the chat is shown either way, and Allow/Deny always stay on the chat card - the tab is read-only.")]
        public bool OpenDiffTabForEdits { get; set; } = true;

        // ─── Advanced ───────────────────────────────────────────────────────────

        [Category("Advanced")]
        [DisplayName("Append System Prompt")]
        [Description("Text appended to the CLI's default system prompt via --append-system-prompt. Blank = don't append anything.")]
        [Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
        public string AppendSystemPrompt { get; set; } = "";

        [Category("Advanced")]
        [DisplayName("System Prompt (replace)")]
        [Description("Replaces the CLI's entire default system prompt via --system-prompt. Can break the extension's own tool-use assumptions if misused - most users want 'Append System Prompt' instead. Blank = use the CLI's default.")]
        [Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
        public string SystemPrompt { get; set; } = "";

        [Category("Advanced")]
        [DisplayName("MCP Config Files")]
        [Description("Paths to MCP server config JSON files (one per line), passed via --mcp-config. Blank = use whatever MCP configuration the CLI already has (e.g. project .mcp.json).")]
        [Editor(typeof(MultilineStringEditor), typeof(UITypeEditor))]
        public string McpConfigPaths { get; set; } = "";

        [Category("Advanced")]
        [DisplayName("Strict MCP Config")]
        [Description("When true, only use MCP servers from 'MCP Config Files' above, ignoring all other MCP configuration (--strict-mcp-config).")]
        public bool StrictMcpConfig { get; set; } = false;

        [Category("Advanced")]
        [DisplayName("Enable IDE Companion Server")]
        [Description("Runs a local loopback-only WebSocket server (like the official VS Code extension's own 'ide' MCP server) so the CLI can see live diagnostics, the active selection, and show proposed edits as a real Visual Studio diff view. Disable if you don't want any local listening socket, even loopback-only.")]
        public bool EnableIdeCompanionServer { get; set; } = true;

        // ─── Internal (not shown in Tools > Options) ───────────────────────────

        /// <summary>RESUPPLY throttle state - last self-update check, ISO 8601 UTC, empty if never checked.</summary>
        [Browsable(false)]
        public string LastUpdateCheckUtc { get; set; } = "";

        // ─── Type converters for dropdown lists ────────────────────────────────

        private sealed class ModelConverter : TypeConverter
        {
            public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
            public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;
            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
                => new StandardValuesCollection(new[] { "", "sonnet", "opus", "haiku", "fable" });
        }

        private sealed class PermissionModeConverter : TypeConverter
        {
            public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
            public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => true;
            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
                => new StandardValuesCollection(new[] { "", "manual", "acceptEdits", "dontAsk", "plan", "auto", "bypassPermissions" });
        }

        private sealed class EffortConverter : TypeConverter
        {
            public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
            public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;
            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
                => new StandardValuesCollection(new[] { "", "low", "medium", "high", "xhigh", "max" });
        }
    }
}
