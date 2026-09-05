using Microsoft.VisualStudio.Shell;

namespace TeronClaudeCodeVS.Core
{
    public class ClaudeCodeToolWindow : ToolWindowPane
    {
        public ClaudeCodeToolWindow() : base(null)
        {
            Caption = "Claude Code";
            Content = new ClaudeCodeChatControl();
        }

        protected override void Dispose(bool disposing)
        {
            // The real close/teardown point - WPF's own Unloaded fires on every shared-pane tab
            // switch too, so the running CLI session must only be torn down here, not there.
            if (disposing)
                (Content as ClaudeCodeChatControl)?.DisposeSession();
            base.Dispose(disposing);
        }
    }
}
