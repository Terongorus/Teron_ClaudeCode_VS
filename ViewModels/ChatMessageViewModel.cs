using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

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
            Blocks.CollectionChanged += OnBlocksChanged;
        }

        // ─── UX-7: grouped tool-call annotation ───────────────────────────────────

        /// <summary>
        /// UX-7: "3 tool calls · 1 failed", or null when there is nothing worth saying.
        /// <para>
        /// Baseline collapses a whole run of tool calls into one annotated row. Our transcript
        /// keeps each call as its own (already collapsed) card, which is more informative when
        /// calls succeed, but it left a failure buried inside a run of otherwise identical-looking
        /// rows. This adds the grouped count and the failure count above the run without
        /// restructuring the card list - the specific thing baseline surfaces that we did not.
        /// </para>
        /// <para>
        /// Deliberately silent for a single successful call: there the annotation would only
        /// restate the one card directly beneath it. It appears as soon as there are two calls, or
        /// as soon as any call fails.
        /// </para>
        /// </summary>
        private string? _toolCallSummary;
        public string? ToolCallSummary
        {
            get => _toolCallSummary;
            private set => SetField(ref _toolCallSummary, value);
        }

        private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // A tool call's status changes long after it is added (Running -> Done/Error), so the
            // summary has to follow each card's own notifications, not just the collection's.
            if (e.OldItems != null)
            {
                foreach (ToolCallViewModel call in e.OldItems.OfType<ToolCallViewModel>())
                    call.PropertyChanged -= OnToolCallPropertyChanged;
            }

            if (e.NewItems != null)
            {
                foreach (ToolCallViewModel call in e.NewItems.OfType<ToolCallViewModel>())
                    call.PropertyChanged += OnToolCallPropertyChanged;
            }

            RefreshToolCallSummary();
        }

        private void OnToolCallPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ToolCallViewModel.Status))
                RefreshToolCallSummary();
        }

        private void RefreshToolCallSummary()
        {
            ToolCallViewModel[] calls = [.. Blocks.OfType<ToolCallViewModel>()];
            int failed = calls.Count(c => c.Status == ToolCallStatus.Error);

            if (calls.Length < 2 && failed == 0)
            {
                ToolCallSummary = null;
                OnPropertyChanged(nameof(HasToolCallFailure));
                return;
            }

            string count = calls.Length == 1 ? "1 tool call" : $"{calls.Length} tool calls";
            ToolCallSummary = failed == 0 ? count : $"{count} · {failed} failed";
            OnPropertyChanged(nameof(HasToolCallFailure));
        }

        /// <summary>UX-7: true when a failure is included in the count, so the view can colour it.</summary>
        public bool HasToolCallFailure =>
            Blocks.OfType<ToolCallViewModel>().Any(c => c.Status == ToolCallStatus.Error);
    }
}
