using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeGUI.Core
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
