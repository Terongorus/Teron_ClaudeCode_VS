using System.Windows.Controls;

namespace Antigravity_CLI_GUI.Core
{
    public partial class MarkdownViewer : UserControl
    {
        public MarkdownViewer()
        {
            InitializeComponent();
        }

        public void SetMarkdown(string text)
        {
            Viewer.Document = MarkdownRenderer.Render(text);
        }
    }
}
