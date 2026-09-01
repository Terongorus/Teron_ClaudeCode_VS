using System;
using System.Collections.Generic;

namespace TeronClaudeCodeVS.ViewModels
{
    /// <summary>
    /// Decouples the MEF-hosted plan-comment adornment (Controls/PlanCommentAdornment.cs, which
    /// lives outside the MVVM ViewModels layer by design and has no reference to any specific
    /// ChatSessionViewModel instance) from the chat ViewModel(s) that own the pending
    /// PlanApprovalViewModel cards. A ChatSessionViewModel registers the CLI-written plan file path
    /// while its approval card is pending, subscribes to CommentSubmitted, and routes matching
    /// comments to the right card via AddPlanComment - which is a safe no-op if this instance
    /// doesn't own that file path (relevant if multiple chat windows/tabs are ever open at once).
    /// </summary>
    public static class PlanCommentRegistry
    {
        private static readonly HashSet<string> _activePlanFilePaths = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised when a comment is submitted from the plan tab's selection adornment: (planFilePath, quotedExcerpt, commentText).</summary>
        public static event Action<string, string, string>? CommentSubmitted;

        public static void RegisterActivePlan(string planFilePath)
        {
            if (!string.IsNullOrEmpty(planFilePath))
                _activePlanFilePaths.Add(planFilePath);
        }

        public static void UnregisterActivePlan(string planFilePath)
        {
            if (!string.IsNullOrEmpty(planFilePath))
                _activePlanFilePaths.Remove(planFilePath);
        }

        /// <summary>Whether the given file path is currently a pending (unresolved) plan approval - gates showing the "Add Comment" affordance.</summary>
        public static bool IsActivePlanFile(string filePath) => _activePlanFilePaths.Contains(filePath);

        public static void SubmitComment(string planFilePath, string quotedExcerpt, string commentText)
            => CommentSubmitted?.Invoke(planFilePath, quotedExcerpt, commentText);
    }
}
