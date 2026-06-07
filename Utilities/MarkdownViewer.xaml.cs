using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Antigravity_CLI_GUI.Utilities;

namespace Antigravity_CLI_GUI.Core
{
    public partial class MarkdownViewer : UserControl
    {
        public MarkdownViewer()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ChatMessage oldMsg)
            {
                oldMsg.PropertyChanged -= OnMessagePropertyChanged;
            }
            if (e.NewValue is ChatMessage newMsg)
            {
                newMsg.PropertyChanged += OnMessagePropertyChanged;
                SetMarkdown(newMsg.Text);
            }
        }

        private void OnMessagePropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatMessage.Text) && sender is ChatMessage msg)
            {
#pragma warning disable VSTHRD001
#pragma warning disable VSTHRD110
                Dispatcher.BeginInvoke(new System.Action(() => SetMarkdown(msg.Text)));
#pragma warning restore VSTHRD110
#pragma warning restore VSTHRD001
            }
        }

        public void SetMarkdown(string text)
        {
            Viewer.Document = MarkdownRenderer.Render(text);
        }
    }
}
