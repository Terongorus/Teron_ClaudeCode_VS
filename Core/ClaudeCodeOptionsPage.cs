using Microsoft.VisualStudio.Shell;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClaudeCodeVS.Core
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
        [Description("Permission mode used when starting a new session.")]
        [TypeConverter(typeof(PermissionModeConverter))]
        public string DefaultPermissionMode { get; set; } = "default";

        [Category("Defaults")]
        [DisplayName("Default Effort Level")]
        [Description("Reasoning effort level passed to --effort when starting a session. Blank = CLI default.")]
        [TypeConverter(typeof(EffortConverter))]
        public string DefaultEffortLevel { get; set; } = "";

        // ─── Input ──────────────────────────────────────────────────────────────

        [Category("Input")]
        [DisplayName("Send on Ctrl+Enter")]
        [Description("When true, Ctrl+Enter sends a message; Enter inserts a newline. When false (default), Enter sends and Shift+Enter inserts a newline.")]
        public bool SendOnCtrlEnter { get; set; } = false;

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
                => new StandardValuesCollection(new[] { "default", "acceptEdits", "plan", "auto", "bypassPermissions" });
        }

        private sealed class EffortConverter : TypeConverter
        {
            public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
            public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;
            public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
                => new StandardValuesCollection(new[] { "", "low", "medium", "high", "max" });
        }
    }
}
