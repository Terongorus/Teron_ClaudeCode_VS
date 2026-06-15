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
        [Category("Claude Code")]
        [DisplayName("Claude CLI Path")]
        [Description("Optional. Full path to claude.exe (or a folder containing it). Leave blank to auto-detect from PATH, ~/.claude/local, or the Claude Code VS Code extension install.")]
        public string ClaudeExecutablePath { get; set; } = "";

        [Category("Claude Code")]
        [DisplayName("Default Model")]
        [Description("Model used when starting a new session: blank (CLI default), sonnet, opus, haiku, or fable.")]
        [TypeConverter(typeof(ModelConverter))]
        public string DefaultModel { get; set; } = "";

        [Category("Claude Code")]
        [DisplayName("Default Permission Mode")]
        [Description("Permission mode used when starting a new session.")]
        [TypeConverter(typeof(PermissionModeConverter))]
        public string DefaultPermissionMode { get; set; } = "default";

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
                => new StandardValuesCollection(new[] { "default", "acceptEdits", "plan", "bypassPermissions" });
        }
    }
}
