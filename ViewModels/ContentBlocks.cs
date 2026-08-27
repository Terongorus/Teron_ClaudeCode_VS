using TeronClaudeCodeVS.Controls;
using TeronClaudeCodeVS.Protocol;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TeronClaudeCodeVS.ViewModels
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

    /// <summary>A pasted screenshot attached to a sent user message - shown as a thumbnail.</summary>
    public sealed class ImageAttachmentViewModel : ContentBlockViewModel
    {
        public ImageSource Thumbnail { get; }

        public ImageAttachmentViewModel(ImageSource thumbnail) => Thumbnail = thumbnail;
    }

    /// <summary>A dropped text/code/PDF file attached to a sent user message - shown as a filename chip.</summary>
    public sealed class FileAttachmentViewModel : ContentBlockViewModel
    {
        public string Title { get; }

        public FileAttachmentViewModel(string title) => Title = title;
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
                    OnPropertyChanged(nameof(RawDiff));
                    OnPropertyChanged(nameof(HasDetail));
                    OnPropertyChanged(nameof(HasMarkdownDetail));
                    OnPropertyChanged(nameof(DetailDocument));
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
                    OnPropertyChanged(nameof(HasDetail));
                    OnPropertyChanged(nameof(HasMarkdownDetail));
                    OnPropertyChanged(nameof(DetailDocument));
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
                    OnPropertyChanged(nameof(HasDetail));
                    OnPropertyChanged(nameof(HasMarkdownDetail));
                    OnPropertyChanged(nameof(DetailDocument));
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

        /// <summary>
        /// Raw "+"/"-" diff lines for Edit/NotebookEdit calls; null for all other tools.
        /// Consumed by DiffViewer — the MarkdownViewer shows only the output/error portion.
        /// </summary>
        public string? RawDiff => ToolPresentation.GetRawDiff(ToolName, _input);

        /// <summary>
        /// Markdown shown in the MarkdownViewer below the diff (output/error for Edit tools;
        /// full detail for all other tools).
        /// </summary>
        private string? DetailMarkdown
        {
            get
            {
                if (RawDiff != null)
                {
                    // Edit tool: DiffViewer already shows the diff; MarkdownViewer shows only the output.
                    if (string.IsNullOrEmpty(_output)) return null;
                    string header = _status == ToolCallStatus.Error ? "**Error:**" : "**Output:**";
                    return $"{header}\n````\n{_output}\n````";
                }
                return ToolPresentation.GetDetailMarkdown(ToolName, _input, _output, _status == ToolCallStatus.Error);
            }
        }

        public bool HasDetail => RawDiff != null || DetailMarkdown != null;

        public bool HasMarkdownDetail => DetailMarkdown != null;

        public FlowDocument? DetailDocument => DetailMarkdown is string md ? MarkdownRenderer.Render(md) : null;

        public FlowDocument Document => DetailDocument ?? new FlowDocument();

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetField(ref _isExpanded, value);
        }

        /// <summary>The message this call's card lives in - lets the running-tasks panel scroll to it.</summary>
        public ChatMessageViewModel? OwnerMessage { get; set; }

        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

        private string _elapsedText = "0s";
        public string ElapsedText => _elapsedText;

        /// <summary>Refreshed on the session's existing 1s status-line tick - no separate per-task timer.</summary>
        public void RefreshElapsedText()
        {
            TimeSpan elapsed = DateTime.UtcNow - StartedAtUtc;
            string text = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds}s"
                : $"{elapsed.Seconds}s";
            SetField(ref _elapsedText, text, nameof(ElapsedText));
        }

        public ToolCallViewModel(string toolUseId, string toolName)
        {
            ToolUseId = toolUseId;
            ToolName = toolName;
        }
    }

    /// <summary>An inline `can_use_tool` permission prompt with Allow / Allow-for-session / Deny actions.</summary>
    public sealed class PermissionRequestViewModel : ContentBlockViewModel, IMarkdownContent
    {
        public string ToolName { get; }
        public string Title { get; }
        public string Summary { get; }

        /// <summary>
        /// Line-level diff for Edit/NotebookEdit calls; null for everything else. Consumed by
        /// DiffViewer, same as ToolCallViewModel.RawDiff - keeps the pending-approval card and the
        /// resolved tool-call card showing an identical diff instead of two different renderers.
        /// </summary>
        public string? RawDiff { get; }

        public FlowDocument? DetailDocument { get; }
        public bool HasDetail => RawDiff != null || DetailDocument != null;
        public bool HasMarkdownDetail => DetailDocument != null;
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
        public ICommand AllowForSessionCommand { get; }
        public ICommand DenyCommand { get; }

        /// <summary>
        /// The respond callback receives (allow, forSession). When forSession is true the caller
        /// should remember the approval so future requests from the same tool are auto-allowed.
        /// </summary>
        public PermissionRequestViewModel(string toolName, string title, JObject input, Func<bool, bool, Task> respond)
        {
            ToolName = toolName;
            Title = title;
            Summary = ToolPresentation.GetSummary(toolName, input);

            RawDiff = ToolPresentation.GetRawDiff(toolName, input);

            // When DiffViewer already shows the diff, don't also render it as a ```diff fence.
            string? detail = RawDiff == null ? ToolPresentation.GetDetailMarkdown(toolName, input, null, false) : null;
            DetailDocument = detail != null ? MarkdownRenderer.Render(detail) : null;

            AllowCommand = new RelayCommand(() => Resolve(true, false, respond), () => !IsResolved);
            AllowForSessionCommand = new RelayCommand(() => Resolve(true, true, respond), () => !IsResolved);
            DenyCommand = new RelayCommand(() => Resolve(false, false, respond), () => !IsResolved);
        }

        private void Resolve(bool allow, bool forSession, Func<bool, bool, Task> respond)
        {
            if (IsResolved) return;
            IsResolved = true;
            ResolutionText = allow ? (forSession ? "Allowed for this session" : "Allowed") : "Denied";
            _ = respond(allow, forSession);
        }
    }

    /// <summary>One selectable option inside a question, tracking its own checked state.</summary>
    public sealed class SelectableOptionViewModel : ObservableObject
    {
        public AskQuestionOption Option { get; }
        public string Label => Option.Label;
        public string Description => Option.Description;

        /// <summary>Shared by every option under the same question, for RadioButton mutual exclusion; unused for checkboxes.</summary>
        public string RadioGroupName { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public SelectableOptionViewModel(AskQuestionOption option, string radioGroupName)
        {
            Option = option;
            RadioGroupName = radioGroupName;
        }
    }

    /// <summary>Tracks the user's current selection for one question inside an AskUserQuestion card.</summary>
    public sealed class QuestionAnswerViewModel : ObservableObject
    {
        public AskQuestion Question { get; }
        public bool HasOptions => Question.Options.Length > 0;
        public bool IsMultiSelect => Question.IsMultiSelect;
        public bool IsSingleSelectWithOptions => HasOptions && !IsMultiSelect;

        /// <summary>Backs both the single-select (RadioButton) and multi-select (CheckBox) lists; mirrors Question.Options 1:1.</summary>
        public ObservableCollection<SelectableOptionViewModel> Options { get; }

        /// <summary>
        /// Unique per question instance so RadioButtons from different questions in the same card
        /// don't cross-exclude each other (WPF groups RadioButtons by GroupName across the whole
        /// visual tree, not just within one ItemsControl).
        /// </summary>
        public string RadioGroupName { get; } = Guid.NewGuid().ToString("N");

        private string _answerText = "";
        public string AnswerText
        {
            get => _answerText;
            set => SetField(ref _answerText, value);
        }

        public string? GetAnswer()
        {
            if (HasOptions)
            {
                if (IsMultiSelect)
                {
                    var selected = Options.Where(o => o.IsSelected).Select(o => o.Option.Value).ToArray();
                    return selected.Length > 0 ? string.Join(", ", selected) : null;
                }
                return Options.FirstOrDefault(o => o.IsSelected)?.Option.Value;
            }
            return string.IsNullOrWhiteSpace(_answerText) ? null : _answerText.Trim();
        }

        public QuestionAnswerViewModel(AskQuestion question)
        {
            Question = question;
            Options = new ObservableCollection<SelectableOptionViewModel>(
                question.Options.Select(o => new SelectableOptionViewModel(o, RadioGroupName)));
        }
    }

    /// <summary>An inline card for `ask_user_question` control requests — lets the user answer before Claude continues.</summary>
    public sealed class AskUserQuestionViewModel : ContentBlockViewModel
    {
        public ObservableCollection<QuestionAnswerViewModel> QuestionAnswers { get; }
            = [];

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

        public ICommand SubmitCommand { get; }
        public ICommand SkipCommand { get; }

        public AskUserQuestionViewModel(IReadOnlyList<AskQuestion> questions, Func<Dictionary<string, string>, Task> respond)
        {
            foreach (var q in questions)
                QuestionAnswers.Add(new QuestionAnswerViewModel(q));

            SubmitCommand = new RelayCommand(() => Resolve(skip: false, respond), () => !IsResolved);
            SkipCommand = new RelayCommand(() => Resolve(skip: true, respond), () => !IsResolved);
        }

        private void Resolve(bool skip, Func<Dictionary<string, string>, Task> respond)
        {
            if (IsResolved) return;
            IsResolved = true;

            Dictionary<string, string> answers = new Dictionary<string, string>();
            if (!skip)
            {
                foreach (var qa in QuestionAnswers)
                {
                    string? answer = qa.GetAnswer();
                    if (answer != null)
                        answers[qa.Question.QuestionText] = answer;
                }
            }

            ResolutionText = skip ? "Skipped" : $"Submitted {answers.Count} answer(s)";
            _ = respond(answers);
        }
    }

    /// <summary>One user comment anchored to a quoted excerpt of the plan text, added from the
    /// native plan-preview tab's selection adornment (see Controls/PlanCommentAdornment.cs).</summary>
    public sealed class PlanCommentEntry
    {
        public string QuotedExcerpt { get; }
        public string CommentText { get; }
        public ICommand RemoveCommand { get; }

        public PlanCommentEntry(string quotedExcerpt, string commentText, Action<PlanCommentEntry> onRemove)
        {
            QuotedExcerpt = quotedExcerpt;
            CommentText = commentText;
            RemoveCommand = new RelayCommand(() => onRemove(this));
        }
    }

    /// <summary>
    /// The `ExitPlanMode` approval card. Deliberately not a reuse of PermissionRequestViewModel -
    /// the real extension's semantics here are different: three choices (auto-accept future edits /
    /// manually approve edits / keep planning) instead of Allow/Allow-for-session/Deny, plus a
    /// free-text box and comments anchored to specific spans of the plan. Adding any comment swaps
    /// the primary action to a single "Send feedback and keep planning" button, matching the real
    /// UI's observed behavior (confirmed live, 2026-08-27).
    /// </summary>
    public sealed class PlanApprovalViewModel : ContentBlockViewModel
    {
        public string PlanMarkdown { get; }
        public string PlanFilePath { get; }

        public ObservableCollection<PlanCommentEntry> Comments { get; } = [];
        public bool HasComments => Comments.Count > 0;

        private string _feedbackText = "";
        public string FeedbackText
        {
            get => _feedbackText;
            set => SetField(ref _feedbackText, value);
        }

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

        public ICommand AutoAcceptCommand { get; }
        public ICommand ManuallyApproveCommand { get; }
        public ICommand KeepPlanningCommand { get; }
        public ICommand SendFeedbackCommand { get; }
        public ICommand ReopenTabCommand { get; }

        /// <summary>The respond callback receives (allow, autoAccept, denyMessage).</summary>
        public PlanApprovalViewModel(string planMarkdown, string planFilePath, Func<bool, bool, string?, Task> respond, Action reopenTab)
        {
            PlanMarkdown = planMarkdown;
            PlanFilePath = planFilePath;

            AutoAcceptCommand = new RelayCommand(() => Resolve(true, true, respond), () => !IsResolved);
            ManuallyApproveCommand = new RelayCommand(() => Resolve(true, false, respond), () => !IsResolved);
            KeepPlanningCommand = new RelayCommand(() => Resolve(false, false, respond), () => !IsResolved);
            SendFeedbackCommand = new RelayCommand(() => Resolve(false, false, respond), () => !IsResolved);
            ReopenTabCommand = new RelayCommand(reopenTab);
        }

        /// <summary>Called from the plan-preview tab's comment adornment when the user submits a comment.</summary>
        public void AddComment(string quotedExcerpt, string commentText)
        {
            if (IsResolved) return;
            Comments.Add(new PlanCommentEntry(quotedExcerpt, commentText, RemoveComment));
            OnPropertyChanged(nameof(HasComments));
        }

        private void RemoveComment(PlanCommentEntry entry)
        {
            Comments.Remove(entry);
            OnPropertyChanged(nameof(HasComments));
        }

        private void Resolve(bool allow, bool autoAccept, Func<bool, bool, string?, Task> respond)
        {
            if (IsResolved) return;
            IsResolved = true;

            // Hides the "Add Comment" affordance on the plan tab once this card resolves - the
            // registry check in PlanCommentAdornmentManager.OnSelectionChanged is what actually
            // gates the button, so this doesn't need to touch the MEF component directly.
            if (!string.IsNullOrEmpty(PlanFilePath))
                PlanCommentRegistry.UnregisterActivePlan(PlanFilePath);

            string? message = null;
            if (!allow)
            {
                // Format confirmed live (2026-08-27): the real extension delivers comments as
                // `[Re: "<quoted excerpt>"] <comment text>` blocks inside the deny message, not
                // through any separate wire-level comment mechanism.
                List<string> parts = [];
                foreach (PlanCommentEntry c in Comments)
                    parts.Add($"[Re: \"{c.QuotedExcerpt}\"] {c.CommentText}");
                if (!string.IsNullOrWhiteSpace(_feedbackText))
                    parts.Add(_feedbackText.Trim());
                message = parts.Count > 0 ? string.Join("\n", parts) : "The user chose to keep planning.";
            }

            ResolutionText = allow
                ? (autoAccept ? "Approved — auto-accepting edits" : "Approved")
                : "Sent feedback — continuing to plan";
            _ = respond(allow, autoAccept, message);
        }
    }

    /// <summary>Shown in the chat when the user stops the agent mid-turn.</summary>
    public sealed class InterruptedBlockViewModel : ContentBlockViewModel { }

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

    /// <summary>A turn that failed or was cut off (unexpected process exit, rate limit, ...) - shown
    /// with an explicit "Try again" affordance that resends the original prompt verbatim, so the
    /// model never has to guess what a follow-up "Continue" refers to.</summary>
    public sealed class RetryNoticeViewModel : ContentBlockViewModel
    {
        public string Text { get; }
        public ICommand RetryCommand { get; }

        public RetryNoticeViewModel(string text, Action onRetry)
        {
            Text = text;
            RetryCommand = new RelayCommand(onRetry);
        }
    }
}
