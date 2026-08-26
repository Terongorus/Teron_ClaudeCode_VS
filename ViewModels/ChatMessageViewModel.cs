using System.Collections.ObjectModel;

namespace TeronClaudeCodeVS.ViewModels
{
    public enum ChatRole
    {
        User,
        Assistant
    }

    /// <summary>One turn in the conversation - a user prompt, or an assistant turn made up of content blocks.</summary>
    public sealed class ChatMessageViewModel : ObservableObject
    {
        public ChatRole Role { get; }

        public ObservableCollection<ContentBlockViewModel> Blocks { get; } = new ObservableCollection<ContentBlockViewModel>();

        public ChatMessageViewModel(ChatRole role)
        {
            Role = role;
        }
    }
}
