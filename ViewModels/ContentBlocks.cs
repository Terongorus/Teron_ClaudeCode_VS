using ClaudeCodeVS.Controls;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Input;

namespace ClaudeCodeVS.ViewModels
{
    /// <summary>Base type for the pieces that make up a chat message (text, thinking, tool calls, ...).</summary>
    public abstract class ContentBlockViewModel : ObservableObject
    {
    }

    /// <summary>Implemented by content blocks that render to a <see cref="FlowDocument"/> via <see cref="MarkdownRenderer"/>.</summary>
    public interface IMarkdownContent
    {
        FlowDocument Document { get; }
    }

    /// <summary>A streamed markdown text block.</summary>
    public sealed class TextBlockViewModel : ContentBlockViewModel, IMarkdownContent
    {
        private string _text = "";
        public string Text
        {
            get => _text;
            set
            {
                if (SetField(ref _text, value))
                    OnPropertyChanged(nameof(Document));
            }
        }

        public FlowDocument Document => MarkdownRenderer.Render(_text);

        public void Append(string delta) => Text += delta;
    }

    /// <summary>A streamed "thinking" block - collapsed by default, shown in a muted style.</summary>
    public sealed class ThinkingBlockViewModel : ContentBlockViewModel, IMarkdownContent
    {
        private string _text = "";
        public string Text
        {
            get => _text;
            set
            {
                if (SetField(ref _text, value))
                    OnPropertyChanged(nameof(Document));
            }
        }

        public FlowDocument Document => MarkdownRenderer.Render(_text);

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetField(ref _isExpanded, value);
        }

        public void Append(string delta) => Text += delta;
    }

    public enum ToolCallStatus
    {
        Running,
        AwaitingApproval,
        Done,
        Error
    }

    /// <summary>A tool call card: icon + summary while collapsed, full input/diff/output when expanded.</summary>
    public sealed class ToolCallViewModel : ContentBlockViewModel, IMarkdownContent
    {
        public string ToolUseId { get; }
        public string ToolName { get; }
        public string Icon => ToolPresentation.GetIcon(ToolName);
        public string DisplayName => ToolPresentation.GetDisplayName(ToolName);

        private JObject? _input;
        public JObject? Input
        {
            get => _input;
            set
            {
                if (SetField(ref _input, value))
                {
                    OnPropertyChanged(nameof(Summary));
                    OnPropertyChanged(nameof(DetailDocument));
                    OnPropertyChanged(nameof(HasDetail));
                    OnPropertyChanged(nameof(Document));
                }
            }
        }

        public string Summary => ToolPresentation.GetSummary(ToolName, _input);

        private string? _output;
        public string? Output
        {
            get => _output;
            set
            {
                if (SetField(ref _output, value))
                {
                    OnPropertyChanged(nameof(DetailDocument));
                    OnPropertyChanged(nameof(HasDetail));
                    OnPropertyChanged(nameof(Document));
                }
            }
        }

        private ToolCallStatus _status = ToolCallStatus.Running;
        public ToolCallStatus Status
        {
            get => _status;
            set
            {
                if (SetField(ref _status, value))
                {
                    OnPropertyChanged(nameof(StatusGlyph));
                    OnPropertyChanged(nameof(DetailDocument));
                    OnPropertyChanged(nameof(HasDetail));
                    OnPropertyChanged(nameof(Document));
                }
            }
        }

        public string StatusGlyph => _status switch
        {
            ToolCallStatus.Running => "●",
            ToolCallStatus.AwaitingApproval => "?",
            ToolCallStatus.Done => "✓",
            ToolCallStatus.Error => "✗",
            _ => ""
        };

        private string? DetailMarkdown => ToolPresentation.GetDetailMarkdown(ToolName, _input, _output, _status == ToolCallStatus.Error);

        public bool HasDetail => DetailMarkdown != null;

        public FlowDocument? DetailDocument => DetailMarkdown is string md ? MarkdownRenderer.Render(md) : null;

        public FlowDocument Document => DetailDocument ?? new FlowDocument();

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetField(ref _isExpanded, value);
        }

        public ToolCallViewModel(string toolUseId, string toolName)
        {
            ToolUseId = toolUseId;
            ToolName = toolName;
        }
    }

    /// <summary>An inline `can_use_tool` permission prompt with Allow/Deny actions.</summary>
    public sealed class PermissionRequestViewModel : ContentBlockViewModel, IMarkdownContent
    {
        public string ToolName { get; }
        public string Title { get; }
        public string Summary { get; }
        public FlowDocument? DetailDocument { get; }
        public bool HasDetail => DetailDocument != null;
        public FlowDocument Document => DetailDocument ?? new FlowDocument();

        private bool _isResolved;
        public bool IsResolved
        {
            get => _isResolved;
            private set => SetField(ref _isResolved, value);
        }

        private string? _resolutionText;
        public string? ResolutionText
        {
            get => _resolutionText;
            private set => SetField(ref _resolutionText, value);
        }

        public ICommand AllowCommand { get; }
        public ICommand DenyCommand { get; }

        public PermissionRequestViewModel(string toolName, string title, JObject input, Func<bool, Task> respond)
        {
            ToolName = toolName;
            Title = title;
            Summary = ToolPresentation.GetSummary(toolName, input);

            string? detail = ToolPresentation.GetDetailMarkdown(toolName, input, null, false);
            DetailDocument = detail != null ? MarkdownRenderer.Render(detail) : null;

            AllowCommand = new RelayCommand(() => Resolve(true, respond), () => !IsResolved);
            DenyCommand = new RelayCommand(() => Resolve(false, respond), () => !IsResolved);
        }

        private void Resolve(bool allow, Func<bool, Task> respond)
        {
            if (IsResolved) return;
            IsResolved = true;
            ResolutionText = allow ? "Allowed" : "Denied";
            _ = respond(allow);
        }
    }

    /// <summary>The small "Done · 1.2s · $0.0012" line at the end of a completed turn.</summary>
    public sealed class ResultFooterViewModel : ContentBlockViewModel
    {
        public string Text { get; }
        public bool IsError { get; }

        public ResultFooterViewModel(string text, bool isError)
        {
            Text = text;
            IsError = isError;
        }
    }
}
