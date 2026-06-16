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

            // FlowDocumentScrollViewer marks MouseWheel as handled even when its scroll is
            // disabled, which prevents the event from reaching the outer ChatScrollViewer.
            // Re-raise on ourselves so it bubbles up normally.
            Viewer.AddHandler(
                System.Windows.UIElement.MouseWheelEvent,
                new System.Windows.Input.MouseWheelEventHandler(OnViewerMouseWheel),
                handledEventsToo: true);
        }

        private void OnViewerMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (e.Delta == 0) return;
            e.Handled = true;
            var args = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = System.Windows.UIElement.MouseWheelEvent
            };
            RaiseEvent(args);
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
