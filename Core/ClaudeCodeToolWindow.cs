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
    }
}
