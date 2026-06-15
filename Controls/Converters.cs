using ClaudeCodeVS.ViewModels;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ClaudeCodeVS.Controls
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
}
