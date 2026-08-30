using TeronClaudeCodeVS.ViewModels;
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TeronClaudeCodeVS.Controls
{
    /// <summary>Bool -> Visibility. Pass ConverterParameter="Invert" to flip the mapping.</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool b = value is bool v && v;
            if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
                b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Null -> Collapsed, non-null -> Visible. Pass ConverterParameter="Invert" to flip the mapping.</summary>
    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isVisible = value != null;
            if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
                isVisible = !isVisible;
            return isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// UX-11: zero -> Visible, anything else -> Collapsed. Drives the new-session empty state off
    /// Messages.Count, which ObservableCollection already raises PropertyChanged for, so no extra
    /// view-model plumbing is needed to keep it in sync. Pass ConverterParameter="Invert" to flip.
    /// </summary>
    public sealed class CountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isEmpty = value is int count && count == 0;
            if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
                isEmpty = !isEmpty;
            return isEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Visible when the two bound values are equal - used to mark the active item in the command menu.</summary>
    public sealed class EqualityToVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2)
                return Visibility.Collapsed;

            return Equals(values[0], values[1]) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>TranscriptViewMode -> Visibility, Collapsed only for Summary - hides thinking blocks entirely in Summary mode (as opposed to Normal, where they're merely collapsed but still present).</summary>
    public sealed class HiddenInSummaryModeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is TranscriptViewMode.Summary ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>(bool HasDetail, TranscriptViewMode mode) -> bool. A tool-call card's expand affordance is disabled entirely in Summary mode, even if it has detail to show.</summary>
    public sealed class ToolCallExpandableConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool hasDetail = values.Length > 0 && values[0] is true;
            bool isSummaryMode = values.Length > 1 && values[1] is TranscriptViewMode.Summary;
            return hasDetail && !isSummaryMode;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Composes the live status strip: while busy, "11m0s · 6.2k tokens · 2 running tasks · Working…";
    /// idle, just the plain status text. No such persistent status line exists in the real VS Code
    /// extension (confirmed via direct research against the installed bundle, 2026-08-27) - this is
    /// an original design, not parity work. Values: [0]=IsBusy, [1]=ElapsedText, [2]=SessionTokensShortText,
    /// [3]=RunningTaskCount, [4]=StatusText.
    /// </summary>
    public sealed class StatusLineConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 5 || values[4] is not string statusText)
                return "";

            bool isBusy = values[0] is true;
            if (!isBusy)
                return statusText;

            string elapsed = values[1] as string ?? "";
            string tokens = values[2] as string ?? "";
            int runningTasks = values[3] is int n ? n : 0;

            var parts = new System.Collections.Generic.List<string> { elapsed, tokens };
            if (runningTasks > 0)
                parts.Add(runningTasks == 1 ? "1 running task" : $"{runningTasks} running tasks");
            parts.Add(statusText);

            return string.Join(" · ", parts);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Returns just the file name part of a relative path string (for the @ file picker).</summary>
    public sealed class FilePathToNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string s ? Path.GetFileName(s) : "";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Returns just the directory portion of a relative path string, with a trailing slash.</summary>
    public sealed class FilePathToDirConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string s) return "";
            string dir = Path.GetDirectoryName(s)?.Replace('\\', '/') ?? "";
            return dir.Length > 0 ? dir + "/" : "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Maps a <see cref="ToolCallStatus"/> to an indicator brush.</summary>
    public sealed class ToolStatusToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Running = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        private static readonly SolidColorBrush Awaiting = new SolidColorBrush(Color.FromRgb(0xE5, 0xA5, 0x4B));
        private static readonly SolidColorBrush Done = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        private static readonly SolidColorBrush Error = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

        static ToolStatusToBrushConverter()
        {
            Running.Freeze();
            Awaiting.Freeze();
            Done.Freeze();
            Error.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                ToolCallStatus.Running => Running,
                ToolCallStatus.AwaitingApproval => Awaiting,
                ToolCallStatus.Done => Done,
                ToolCallStatus.Error => Error,
                _ => Running
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// FEAT-4. Colours an MCP server's status dot. The palette is deliberately the same four
    /// colours ToolStatusToBrushConverter already uses, so "green means fine, amber means it needs
    /// you, red means broken" reads identically wherever it appears in the tool window.
    /// </summary>
    public sealed class McpStatusToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Neutral = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        private static readonly SolidColorBrush Warning = new SolidColorBrush(Color.FromRgb(0xE5, 0xA5, 0x4B));
        private static readonly SolidColorBrush Ok = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
        private static readonly SolidColorBrush Bad = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

        static McpStatusToBrushConverter()
        {
            Neutral.Freeze();
            Warning.Freeze();
            Ok.Freeze();
            Bad.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value switch
            {
                McpStatusKind.Connected => Ok,
                McpStatusKind.Warning => Warning,
                McpStatusKind.Pending => Warning,
                McpStatusKind.Error => Bad,
                McpStatusKind.Disabled => Neutral,
                _ => Neutral
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
