using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using TeronClaudeCodeVS.ViewModels;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>
    /// FEAT-2. Opens a real Visual Studio side-by-side diff tab for one file-editing tool call,
    /// alongside - not instead of - the inline card in the chat.
    ///
    /// The audit (real:12) measured baseline opening an editor tab titled `[Claude Code] &lt;path&gt;`
    /// carrying five toolbar buttons: accept, revert, next change, previous change, swap sides. VS
    /// splits those in half for us. Difference navigation and the side-by-side/inline view switch
    /// are built into VS's own diff window, so three of the five come for free. The other two
    /// cannot: <see cref="IVsDifferenceService"/> is read-only browsing UI with no apply mechanism
    /// and no way to add custom toolbar commands to the window it creates - already established in
    /// this codebase, see the note on VsIdeToolHandlers.OpenDiffAsync. So accept and revert stay on
    /// the chat card, driving the same permission response they always did. The tab is the view;
    /// the card is the control. That split is deliberate and is recorded in the Phase 7 doc.
    ///
    /// Both sides are written to temp files and marked read-only, because this really is a view -
    /// a user who types into a pane and saves would otherwise be editing a scratch file while
    /// believing they had edited their own. That read-only attribute is also why this does not
    /// pass VSDIFFOPT_LeftFileIsTemporary/RightFileIsTemporary the way the MCP `openDiff` path
    /// does: File.Delete throws on a read-only file, so handing VS the job of deleting files we
    /// have deliberately locked would be handing it a job it cannot do. Cleanup is ours instead.
    ///
    /// A note on the VSTHRD010 suppressions below. Every entry point here is already on the UI
    /// thread by construction - a WPF Click handler, or OnPermissionRequested, which the session
    /// posts through _dispatcher.BeginInvoke precisely because it mutates bound collections - and
    /// <see cref="Open"/> re-checks that at runtime before touching anything. The suppressions are
    /// scoped to the individual SDK calls rather than declared with ThreadHelper.ThrowIfNotOnUIThread
    /// on purpose: that assert makes the analyzer treat this whole method as main-thread-affinitized
    /// and then walks the inference outwards through every caller, which here means the entire chat
    /// view model - a dozen warnings on pre-existing methods that have nothing to do with diffs. The
    /// runtime guard is kept; only the marker the analyzer keys off is not.
    /// </summary>
    internal static class VsDiffTab
    {
        private const string TempRootName = "TeronClaudeCodeVS-difftab";

        /// <summary>
        /// One tab per file. A long agent run edits the same file repeatedly, and stacking a tab
        /// per edit would bury the editor; re-opening replaces the previous comparison instead.
        /// </summary>
        private static readonly Dictionary<string, IVsWindowFrame> s_frames =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Opens the comparison, or returns a human-readable reason why it could not be opened.
        /// Never throws: every caller is a UI gesture or a permission prompt, and neither should
        /// be able to take the tool window down.
        /// </summary>
        /// <param name="alreadyApplied">
        /// False for a pending permission request (disk is still the "before"), true for a
        /// completed tool call (disk is now the "after").
        /// </param>
        public static string? Open(
            string toolName,
            JObject? input,
            bool alreadyApplied,
            string workingDirectory,
            string? sessionId,
            string? toolUseId)
        {
            if (!ThreadHelper.CheckAccess())
                return "A diff tab can only be opened from the UI thread.";

            try
            {
                return OpenCore(toolName, input, alreadyApplied, workingDirectory, sessionId, toolUseId);
            }
            catch (Exception ex)
            {
                return $"Couldn't open a diff tab: {ex.GetType().Name} - {ex.Message}";
            }
        }

        private static string? OpenCore(
            string toolName,
            JObject? input,
            bool alreadyApplied,
            string workingDirectory,
            string? sessionId,
            string? toolUseId)
        {
            if (toolName != "Edit" && toolName != "Write")
            {
                // NotebookEdit is deliberately excluded rather than half-supported: it changes one
                // cell inside a .ipynb, and turning that into a whole-file text diff would mean
                // re-serialising the notebook ourselves and diffing our guess at the CLI's output.
                return "A diff tab is available for Edit and Write calls only.";
            }

            string? declared = ToolPresentation.GetFullPath(toolName, input);
            if (declared == null)
                return "That tool call doesn't name a file to compare.";

            string path = SessionCheckpointStore.Resolve(workingDirectory, declared);
            string name = Path.GetFileName(path);

            string? diskText = null;
            if (File.Exists(path))
            {
                try { diskText = File.ReadAllText(path); }
                catch (Exception ex) { return $"Couldn't read {name}: {ex.Message}"; }
            }

            string before, after;

            if (!alreadyApplied)
            {
                // Nothing has run yet, so the working copy IS the before side, and deliberately
                // not the CLI's backup: that backup describes an earlier point in the session, so
                // on a file something already edited this turn it would compare the proposal
                // against a state the user is no longer looking at. Being a real backup of the
                // right file makes that failure worse, not better - it produces a diff that looks
                // entirely plausible and is quietly about the wrong moment.
                before = diskText ?? "";
                string? forward = ApplyForward(toolName, input, before);
                if (forward == null)
                {
                    return $"Couldn't work out what {name} would look like after this edit - " +
                           "the text it replaces isn't in the file as it currently stands.";
                }
                after = forward;
            }
            else
            {
                if (diskText == null)
                    return $"{name} is no longer on disk, so there's nothing to compare against.";

                after = diskText;

                // Here the backup is the only honest source - the working copy has moved on, and a
                // Write call carries no record of what it overwrote. Reconstruction by undoing an
                // Edit is the fallback, and only works while the replaced text is still where the
                // call left it.
                string? backup = toolUseId == null || sessionId == null
                    ? null
                    : SessionCheckpointStore.TryReadContentBeforeEdit(workingDirectory, sessionId!, toolUseId!, path);

                string? previous = backup ?? ReverseApply(toolName, input, diskText);
                if (previous == null)
                {
                    return $"The previous contents of {name} aren't recoverable: Claude Code kept " +
                           "no backup for this call, and the edit itself doesn't say what it replaced.";
                }
                before = previous;
            }

            if (string.Equals(before, after, StringComparison.Ordinal))
                return $"That call left {name} unchanged - there's nothing to compare.";

            SweepStaleTempDirs();

            string dir = Path.Combine(Path.GetTempPath(), TempRootName, Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);

            // Keep the real extension on both sides so VS applies the right language service and
            // the comparison is syntax-coloured rather than plain text.
            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string leftPath = Path.Combine(dir, stem + ".before" + ext);
            string rightPath = Path.Combine(dir, stem + ".after" + ext);

            File.WriteAllText(leftPath, before);
            File.WriteAllText(rightPath, after);
            MakeReadOnly(leftPath);
            MakeReadOnly(rightPath);

#pragma warning disable VSTHRD010
            if (Package.GetGlobalService(typeof(SVsDifferenceService)) is not IVsDifferenceService diffService)
                return "Visual Studio's difference service isn't available.";

            CloseExisting(path);
#pragma warning restore VSTHRD010

            // Baseline titles the tab `[Claude Code] <path>`. A VS document tab is far narrower
            // than a VS Code one, so the caption carries the file name and the full path goes in
            // the tooltip, where it stays discoverable without truncating the marker.
            string caption = "[Claude Code] " + name;
            string leftLabel = name + " (before)";
            string rightLabel = name + (alreadyApplied ? " (after Claude's edit)" : " (proposed)");
            string inlineLabel = name + " - Claude Code";

#pragma warning disable VSTHRD010
            IVsWindowFrame frame = diffService.OpenComparisonWindow2(
                leftPath, rightPath, caption, path, leftLabel, rightLabel, inlineLabel, "", 0);
#pragma warning restore VSTHRD010

            if (frame != null)
                s_frames[path] = frame;

            return null;
        }

        /// <summary>The file as it will look once the call runs, or null if the call doesn't fit the file.</summary>
        private static string? ApplyForward(string toolName, JObject? input, string before)
        {
            if (toolName == "Write")
                return input?.Value<string>("content") ?? "";

            string? oldString = input?.Value<string>("old_string");
            string? newString = input?.Value<string>("new_string") ?? "";
            if (oldString == null)
                return null;

            // The CLI's own convention for creating a file through Edit.
            if (oldString.Length == 0)
                return newString;

            return Replace(before, oldString, newString, ReplaceAll(input));
        }

        /// <summary>
        /// The file as it looked before the call, undone from the current contents. Only possible
        /// for Edit, and only while the replaced text is still where the call left it - a Write
        /// call carries no record of what it overwrote, which is exactly the case
        /// <see cref="SessionCheckpointStore"/> exists to cover.
        /// </summary>
        private static string? ReverseApply(string toolName, JObject? input, string after)
        {
            if (toolName != "Edit")
                return null;

            string? oldString = input?.Value<string>("old_string");
            string? newString = input?.Value<string>("new_string");
            if (oldString == null || string.IsNullOrEmpty(newString))
                return null;

            return Replace(after, newString!, oldString, ReplaceAll(input));
        }

        private static bool ReplaceAll(JObject? input) => input?.Value<bool?>("replace_all") == true;

        /// <summary>
        /// Replaces the first occurrence, or every occurrence when the call said `replace_all`.
        /// Returns null when the needle isn't present, which is the caller's signal that the file
        /// has moved on since and the reconstruction can't be trusted.
        /// </summary>
        private static string? Replace(string haystack, string needle, string replacement, bool all)
        {
            int at = haystack.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0)
                return null;

            if (all)
                return haystack.Replace(needle, replacement);

            return haystack.Substring(0, at) + replacement + haystack.Substring(at + needle.Length);
        }

        private static void CloseExisting(string path)
        {
            if (!s_frames.TryGetValue(path, out IVsWindowFrame existing))
                return;

            s_frames.Remove(path);
#pragma warning disable VSTHRD010
            try { existing.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); }
            catch { /* already closed by the user, which is the normal case */ }
#pragma warning restore VSTHRD010
        }

        private static void MakeReadOnly(string path)
        {
            try { File.SetAttributes(path, FileAttributes.ReadOnly); }
            catch { /* a diff that is merely editable is still a usable diff */ }
        }

        /// <summary>
        /// Deletes comparison scratch older than a day. Runs on open rather than on close because
        /// VS holds the files for as long as the window lives and we are not told when it dies -
        /// so the reliable moment to clean up yesterday's is while making today's.
        /// </summary>
        private static void SweepStaleTempDirs()
        {
            try
            {
                string root = Path.Combine(Path.GetTempPath(), TempRootName);
                if (!Directory.Exists(root))
                    return;

                DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (string dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dir) > cutoff)
                            continue;

                        foreach (string file in Directory.GetFiles(dir))
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                        Directory.Delete(dir);
                    }
                    catch { /* still open in a diff window, or gone already */ }
                }
            }
            catch { }
        }
    }
}
