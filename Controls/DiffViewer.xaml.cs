using Microsoft.VisualStudio.Shell;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeCodeGUI.Controls
{
    /// <summary>
    /// Renders a raw unified-diff string (lines prefixed with "+ " or "- ") as a code viewer
    /// with full-width colored row backgrounds matching VS Code's diff editor look.
    /// </summary>
    public partial class DiffViewer : UserControl
    {
        private static readonly SolidColorBrush s_addBg      = Frozen(Color.FromArgb(0x28, 0x3F, 0xB9, 0x50));
        private static readonly SolidColorBrush s_remBg      = Frozen(Color.FromArgb(0x28, 0xE5, 0x48, 0x4D));
        private static readonly SolidColorBrush s_hunkBg     = Frozen(Color.FromArgb(0x18, 0x79, 0xB8, 0xFF));
        private static readonly SolidColorBrush s_addGutter  = Frozen(Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50));
        private static readonly SolidColorBrush s_remGutter  = Frozen(Color.FromArgb(0xFF, 0xE5, 0x48, 0x4D));
        private static readonly SolidColorBrush s_hunkGutter = Frozen(Color.FromArgb(0xFF, 0x79, 0xB8, 0xFF));
        private static readonly FontFamily s_mono = new FontFamily("Consolas");

        public static readonly DependencyProperty RawDiffProperty =
            DependencyProperty.Register(
                nameof(RawDiff), typeof(string), typeof(DiffViewer),
                new PropertyMetadata(null, (d, e) => ((DiffViewer)d).Rebuild()));

        public string? RawDiff
        {
            get => (string?)GetValue(RawDiffProperty);
            set => SetValue(RawDiffProperty, value);
        }

        public DiffViewer()
        {
            InitializeComponent();
            // Re-raise mouse-wheel so it bubbles to the outer ChatScrollViewer,
            // same pattern used by MarkdownViewer.
            Scroller.AddHandler(
                MouseWheelEvent,
                new MouseWheelEventHandler(OnScrollerMouseWheel),
                handledEventsToo: true);
        }

        private void OnScrollerMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0) return;
            e.Handled = true;
            var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                { RoutedEvent = MouseWheelEvent };
            RaiseEvent(args);
        }

        private void Rebuild()
        {
            LinesPanel.Children.Clear();
            if (string.IsNullOrEmpty(RawDiff)) return;

            foreach (string rawLine in RawDiff!.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                DiffLineType type;
                string display;

                if (line.StartsWith("+++ ", StringComparison.Ordinal) ||
                    line.StartsWith("--- ", StringComparison.Ordinal) ||
                    line.StartsWith("@@",  StringComparison.Ordinal))
                { type = DiffLineType.Hunk;    display = line; }
                else if (line.StartsWith("+ ", StringComparison.Ordinal) || line == "+")
                { type = DiffLineType.Added;   display = line.Length > 2 ? line.Substring(2) : ""; }
                else if (line.StartsWith("- ", StringComparison.Ordinal) || line == "-")
                { type = DiffLineType.Removed; display = line.Length > 2 ? line.Substring(2) : ""; }
                else
                { type = DiffLineType.Context; display = line; }

                LinesPanel.Children.Add(MakeLine(type, display));
            }
        }

        private UIElement MakeLine(DiffLineType type, string text)
        {
            SolidColorBrush lineBg = type switch
            {
                DiffLineType.Added   => s_addBg,
                DiffLineType.Removed => s_remBg,
                DiffLineType.Hunk    => s_hunkBg,
                _                    => Brushes.Transparent,
            };
            SolidColorBrush gutterBg = type switch
            {
                DiffLineType.Added   => s_addGutter,
                DiffLineType.Removed => s_remGutter,
                DiffLineType.Hunk    => s_hunkGutter,
                _                    => Brushes.Transparent,
            };

            var gutter = new Border { Background = gutterBg, Width = 3 };

            var tb = new TextBlock
            {
                Text = text,
                FontFamily = s_mono,
                FontSize = 11,
                Padding = new Thickness(6, 1, 6, 1),
                TextWrapping = TextWrapping.NoWrap,
            };
            tb.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(gutter, 0);
            Grid.SetColumn(tb, 1);
            row.Children.Add(gutter);
            row.Children.Add(tb);

            return new Border { Background = lineBg, Child = row };
        }

        private enum DiffLineType { Context, Added, Removed, Hunk }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
