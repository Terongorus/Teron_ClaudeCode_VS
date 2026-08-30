using System;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>FEAT-1. Which of the two independent things a rewind should do.</summary>
    public enum RewindAction
    {
        /// <summary>Branch the conversation at this point and leave the working tree alone.</summary>
        Fork,

        /// <summary>Restore the files this message and everything after it changed, and stay in this conversation.</summary>
        RewindCode,

        /// <summary>Both.</summary>
        ForkAndRewindCode
    }

    /// <summary>
    /// FEAT-1. One user message the conversation can be taken back to.
    ///
    /// The audit's design insight was that baseline treats forking the conversation and restoring
    /// the code as two separate concerns rather than one "undo", and this type is what keeps them
    /// separable: it carries the two different ids each concern needs.
    /// <list type="bullet">
    /// <item><see cref="MessageUuid"/> - the user message's own uuid, which is what the CLI's
    /// `rewind_files` control request takes as its <c>user_message_id</c>.</item>
    /// <item><see cref="ResumeAtUuid"/> - the uuid of the nearest preceding entry in the message
    /// chain, which is what `--resume-session-at` takes: that flag keeps everything up to and
    /// including the id it is given, so forking "from" a message means resuming at the entry
    /// before it.</item>
    /// </list>
    /// Both are read out of the CLI's own transcript rather than tracked by us - see
    /// <see cref="SessionCheckpointStore.LoadRewindPoints"/> for why that is the only honest
    /// source for them.
    /// </summary>
    public sealed class RewindPoint
    {
        /// <summary>The user message's own uuid - the `rewind_files` target.</summary>
        public string MessageUuid { get; set; } = "";

        /// <summary>
        /// The entry to resume at when forking, or null when this is the conversation's first
        /// message and there is nothing before it to keep. Baseline handles that case by starting
        /// a brand-new session with the message prefilled instead of forking, and so do we.
        /// </summary>
        public string? ResumeAtUuid { get; set; }

        /// <summary>The message's text, used both for the picker row and for prefilling the composer.</summary>
        public string PromptText { get; set; } = "";

        /// <summary>Position among the conversation's real user prompts, 0-based - see
        /// <see cref="ChatSessionViewModel"/> for the one place it is used, mapping a transcript
        /// record back onto the message list on screen.</summary>
        public int UserOrdinal { get; set; }

        public DateTime TimestampUtc { get; set; }

        /// <summary>
        /// Baseline's own relative-age wording, to the letter: "just now" under a minute, then
        /// "5m ago", "3h ago", "2d ago". Computed when the picker is opened rather than bound
        /// live, which is also how baseline does it - the list is rebuilt on every open.
        /// </summary>
        public string RelativeTime { get; set; } = "";

        /// <summary>True when forking here means starting a new session rather than resuming one.</summary>
        public bool IsFirstMessage => string.IsNullOrEmpty(ResumeAtUuid);

        /// <summary>
        /// The prompt itself, which is what a picker row is. Added after a live run showed the rows
        /// announcing themselves as "TeronClaudeCodeVS.ViewModels.RewindPoint": a `ListBoxItem`
        /// with no `AutomationProperties.Name` falls back to `ToString()`, so without this the row
        /// a screen reader reads out is the type name and nothing else. The same convention is
        /// already used by <see cref="ModelOption"/> and the other picker types here.
        /// </summary>
        public override string ToString() => PromptText;

        internal static string DescribeAge(DateTime timestampUtc, DateTime nowUtc)
        {
            double seconds = (nowUtc - timestampUtc).TotalSeconds;
            if (seconds < 60) return "just now";
            int minutes = (int)(seconds / 60);
            if (minutes < 60) return minutes + "m ago";
            int hours = minutes / 60;
            if (hours < 24) return hours + "h ago";
            return (hours / 24) + "d ago";
        }
    }
}
