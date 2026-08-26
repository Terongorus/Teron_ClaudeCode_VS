using System.Collections.ObjectModel;

namespace TeronClaudeCodeVS.ViewModels
{
    public enum ChatRole
    {
        User,
        Assistant,

        /// <summary>A local/CLI-generated notice not attributed to either party - e.g. a /compact result.</summary>
        System
    }

    /// <summary>One turn in the conversation - a user prompt, or an assistant turn made up of content blocks.</summary>
    public sealed class ChatMessageViewModel : ObservableObject
    {
        public ChatRole Role { get; }

        public ObservableCollection<ContentBlockViewModel> Blocks { get; } = [];

        public ChatMessageViewModel(ChatRole role)
        {
            Role = role;
        }
    }
}
