using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Antigravity_CLI_GUI.Utilities
{
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _text = "";
        private string _pending = "";
        private bool _isPermissionRequest = false;
        private bool _isActionable = true;

        public string Role { get; set; } = "";   // "user", "assistant", "system", "warning", "error"

        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        // Buffer for streaming tokens before animation
        public string Pending
        {
            get => _pending;
            set
            {
                _pending = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Pending)));
            }
        }

        public bool IsPermissionRequest
        {
            get => _isPermissionRequest;
            set
            {
                _isPermissionRequest = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPermissionRequest)));
            }
        }

        public bool IsActionable
        {
            get => _isActionable;
            set
            {
                _isActionable = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActionable)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ChatViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<ChatMessage> Messages { get; } = new();

        public string[] Models { get; } =
        {
            "gemini-2.5-flash",
            "gemini-2.5-pro",
            "gemini-2.0-flash",
            "gemini-2.0-pro",
            "gemini-1.5-flash",
            "gemini-1.5-pro"
        };

        private string _selectedModel = "gemini-2.5-flash";
        public string SelectedModel
        {
            get => _selectedModel;
            set
            {
                _selectedModel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedModel)));
            }
        }

        private bool _isTerminalVisible = false;
        public bool IsTerminalVisible
        {
            get => _isTerminalVisible;
            set
            {
                _isTerminalVisible = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTerminalVisible)));
            }
        }

        public void AddUserMessage(string text)
        {
            Messages.Add(new ChatMessage { Role = "user", Text = text });
            OnPropertyChanged(nameof(Messages));
        }

        public void AddAssistantMessage(string text)
        {
            Messages.Add(new ChatMessage { Role = "assistant", Text = text });
            OnPropertyChanged(nameof(Messages));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public partial class ChatTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? UserTemplate { get; set; }
        public DataTemplate? AssistantTemplate { get; set; }
        public DataTemplate? SystemTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is ChatMessage msg)
            {
                if (msg.Role == "user") return UserTemplate!;
                if (msg.Role == "assistant") return AssistantTemplate!;
                return SystemTemplate!;
            }

            return base.SelectTemplate(item, container);
        }
    }

    public class InverseBooleanConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool b)
                return !b;
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool b)
                return !b;
            return true;
        }
    }
}
