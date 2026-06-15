using ClaudeCodeVS.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ClaudeCodeVS.Controls
{
    /// <summary>Renders an <see cref="IMarkdownContent"/> view model's markdown, refreshing as it streams in.</summary>
    public partial class MarkdownViewer : UserControl
    {
        public MarkdownViewer()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is INotifyPropertyChanged old)
                old.PropertyChanged -= OnContentPropertyChanged;

            if (e.NewValue is IMarkdownContent content)
            {
                ((INotifyPropertyChanged)content).PropertyChanged += OnContentPropertyChanged;
                Viewer.Document = content.Document;
            }
            else
            {
                Viewer.Document = new FlowDocument();
            }
        }

        private void OnContentPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(IMarkdownContent.Document)) return;
            if (sender is IMarkdownContent content)
                Viewer.Document = content.Document;
        }
    }
}
