using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeCLIGUI.Core
{
    public class ClaudeCodeToolWindow : ToolWindowPane
    {
        public ClaudeCodeToolWindow() : base(null)
        {
            Caption = "Claude Code";
            Content = new ClaudeCodeChatControl();
        }
    }
}
