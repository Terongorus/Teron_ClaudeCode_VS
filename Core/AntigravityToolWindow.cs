using Microsoft.VisualStudio.Shell;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Antigravity_CLI_GUI
{
    public class AntigravityToolWindow : ToolWindowPane
    {
        public AntigravityToolWindow() : base(null)
        {
            Caption = "Antigravity";
            Content = new AntigravityToolWindowControl();
        }
    }
}
