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

        public string Role { get; set; } = "";   // "user" or "assistant"

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

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ChatViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<ChatMessage> Messages { get; } = new();

        public string[] Models { get; } =
        {
        "gemini-1.5-flash",
        "gemini-1.5-pro",
        "gemini-1.5-ultra"
    };

        private string _selectedModel = "gemini-1.5-flash";
        public string SelectedModel
        {
            get => _selectedModel;
            set
            {
                _selectedModel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedModel)));
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

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is ChatMessage msg)
                return msg.Role == "user" ? UserTemplate! : AssistantTemplate!;

            return base.SelectTemplate(item, container);
        }
    }
}
