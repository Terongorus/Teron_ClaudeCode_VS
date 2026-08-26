using TeronClaudeCodeVS.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace TeronClaudeCodeVS.Controls
{
    /// <summary>Picks the right DataTemplate for each kind of <see cref="ContentBlockViewModel"/>.</summary>
    public sealed class ContentBlockTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? TextTemplate { get; set; }
        public DataTemplate? ThinkingTemplate { get; set; }
        public DataTemplate? ToolCallTemplate { get; set; }
        public DataTemplate? PermissionTemplate { get; set; }
        public DataTemplate? AskUserQuestionTemplate { get; set; }
        public DataTemplate? ResultTemplate { get; set; }
        public DataTemplate? InterruptedTemplate { get; set; }
        public DataTemplate? RetryTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            return item switch
            {
                TextBlockViewModel => TextTemplate,
                ThinkingBlockViewModel => ThinkingTemplate,
                ToolCallViewModel => ToolCallTemplate,
                PermissionRequestViewModel => PermissionTemplate,
                AskUserQuestionViewModel => AskUserQuestionTemplate,
                ResultFooterViewModel => ResultTemplate,
                InterruptedBlockViewModel => InterruptedTemplate,
                RetryNoticeViewModel => RetryTemplate,
                _ => base.SelectTemplate(item, container)
            };
        }
    }

    /// <summary>Picks the user vs. assistant bubble layout for a <see cref="ChatMessageViewModel"/>.</summary>
    public sealed class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? UserTemplate { get; set; }
        public DataTemplate? AssistantTemplate { get; set; }
        public DataTemplate? SystemTemplate { get; set; }

        public override DataTemplate? SelectTemplate(object item, DependencyObject container)
        {
            if (item is ChatMessageViewModel message)
            {
                return message.Role switch
                {
                    ChatRole.User => UserTemplate,
                    ChatRole.System => SystemTemplate,
                    _ => AssistantTemplate
                };
            }

            return base.SelectTemplate(item, container);
        }
    }
}
