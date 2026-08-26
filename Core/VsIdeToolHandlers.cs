using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>
    /// The real, VS SDK-backed implementation of <see cref="IIdeToolHandlers"/>. Every VS SDK call
    /// here needs the UI thread - each method starts with a main-thread switch, matching the
    /// pattern already used throughout this project (<see cref="ExtensionUpdateCheck"/>,
    /// <c>ClaudeCodeChatControl</c>'s document/selection helpers).
    /// </summary>
    internal sealed class VsIdeToolHandlers : IIdeToolHandlers
    {
        // Tab names of diff windows we opened ourselves, so CloseAllDiffTabsAsync can target
        // exactly those rather than guessing which open tabs are "diff tabs".
        private readonly HashSet<string> _openDiffTabNames = new HashSet<string>(StringComparer.Ordinal);

        public async Task<JObject> GetWorkspaceFoldersAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string dir = await GetWorkingDirectoryAsync();
            var folder = new JObject
            {
                ["name"] = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)),
                ["uri"] = new Uri(dir).AbsoluteUri,
                ["path"] = dir,
                ["index"] = 0
            };

            return new JObject
            {
                ["success"] = true,
                ["folders"] = new JArray(folder),
                ["rootPath"] = dir,
                ["workspaceFile"] = null
            };
        }

        internal static async Task<string> GetWorkingDirectoryAsync()
        {
            try
            {
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                string? path = solution?.FullPath;
                if (!string.IsNullOrEmpty(path))
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                        return dir!;
                }
            }
            catch { }

            return Environment.CurrentDirectory;
        }

        public async Task<JObject> GetOpenEditorsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var tabs = new JArray();
            var frames = await VS.Windows.GetAllDocumentWindowsAsync();
            foreach (var frame in frames)
            {
                var docView = await frame.GetDocumentViewAsync();
                if (string.IsNullOrEmpty(docView?.FilePath)) continue;

                tabs.Add(new JObject
                {
                    ["uri"] = new Uri(docView!.FilePath!).AbsoluteUri,
                    ["fileName"] = docView.FilePath,
                    ["label"] = frame.Caption,
                    ["isDirty"] = docView.Document?.IsDirty ?? false
                });
            }

            return new JObject { ["tabs"] = tabs };
        }

        public async Task<JObject> GetCurrentSelectionAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var docView = await VS.Documents.GetActiveDocumentViewAsync();
            var textView = docView?.TextView;
            if (string.IsNullOrEmpty(docView?.FilePath) || textView == null)
                return new JObject { ["success"] = false, ["message"] = "No active editor." };

            ITextSelection selection = textView.Selection;
            SnapshotPoint start = selection.Start.Position;
            SnapshotPoint end = selection.End.Position;
            string text = textView.TextBuffer.CurrentSnapshot.GetText(new SnapshotSpan(start, end));

            return new JObject
            {
                ["success"] = true,
                ["filePath"] = docView!.FilePath,
                ["text"] = text,
                ["selection"] = new JObject
                {
                    ["start"] = PositionToLineChar(start),
                    ["end"] = PositionToLineChar(end)
                }
            };
        }

        // No separate "last focused editor's selection" tracking exists yet (unlike the official
        // extension, which keeps this even after focus moves away) - known simplification,
        // documented in docs/Phase 3.
        public Task<JObject> GetLatestSelectionAsync() => GetCurrentSelectionAsync();

        private static JObject PositionToLineChar(SnapshotPoint point)
        {
            var line = point.GetContainingLine();
            return new JObject { ["line"] = line.LineNumber, ["character"] = point.Position - line.Start.Position };
        }

        public async Task<JObject> CheckDocumentDirtyAsync(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var docView = await VS.Documents.GetDocumentViewAsync(filePath);
            return new JObject { ["filePath"] = filePath, ["isDirty"] = docView?.Document?.IsDirty ?? false };
        }

        public async Task<JObject> SaveDocumentAsync(string filePath)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var docView = await VS.Documents.GetDocumentViewAsync(filePath);
            if (docView?.Document == null)
                return new JObject { ["success"] = false, ["message"] = "Document is not open." };

            docView.Document.Save();
            return new JObject { ["success"] = true };
        }

        public async Task<JObject> OpenFileAsync(string filePath, bool preview, string? startText, string? endText, bool selectToEndOfLine, bool makeFrontmost)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var docView = preview
                ? await VS.Documents.OpenInPreviewTabAsync(filePath)
                : await VS.Documents.OpenAsync(filePath);

            if (docView?.TextView == null)
                return new JObject { ["success"] = false, ["message"] = "Could not open file." };

            if (!string.IsNullOrEmpty(startText))
                SelectByText(docView.TextView, startText!, endText, selectToEndOfLine);

            return new JObject { ["success"] = true };
        }

        private static void SelectByText(Microsoft.VisualStudio.Text.Editor.IWpfTextView textView, string startText, string? endText, bool selectToEndOfLine)
        {
            var snapshot = textView.TextBuffer.CurrentSnapshot;
            string full = snapshot.GetText();

            int startIdx = full.IndexOf(startText, StringComparison.Ordinal);
            if (startIdx < 0) return;

            int endIdx = startIdx + startText.Length;
            if (!string.IsNullOrEmpty(endText))
            {
                int found = full.IndexOf(endText!, startIdx, StringComparison.Ordinal);
                if (found >= 0)
                    endIdx = found + endText!.Length;
            }

            if (selectToEndOfLine)
            {
                var endPointLine = new SnapshotPoint(snapshot, Math.Min(endIdx, snapshot.Length)).GetContainingLine();
                endIdx = endPointLine.End.Position;
            }

            var span = new SnapshotSpan(snapshot, startIdx, Math.Max(0, Math.Min(endIdx, snapshot.Length) - startIdx));
            textView.Selection.Select(span, isReversed: false);
            textView.Caret.MoveTo(span.End);
            textView.ViewScroller.EnsureSpanVisible(span);
        }

        public async Task<JObject> CloseTabAsync(string tabName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var frames = await VS.Windows.GetAllDocumentWindowsAsync();
            foreach (var frame in frames)
            {
                if (string.Equals(frame.Caption, tabName, StringComparison.Ordinal))
                {
                    await frame.CloseFrameAsync(FrameCloseOption.SaveIfDirty);
                    _openDiffTabNames.Remove(tabName);
                    return new JObject { ["success"] = true };
                }
            }

            return new JObject { ["success"] = false, ["message"] = "Tab not found." };
        }

        public async Task<JObject> CloseAllDiffTabsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            int closed = 0;
            var frames = await VS.Windows.GetAllDocumentWindowsAsync();
            foreach (var frame in frames)
            {
                if (_openDiffTabNames.Contains(frame.Caption))
                {
                    await frame.CloseFrameAsync(FrameCloseOption.NoSave);
                    closed++;
                }
            }
            _openDiffTabNames.Clear();

            return new JObject { ["success"] = true, ["closed"] = closed };
        }

        /// <summary>
        /// Diagnostics via EnvDTE's classic Error List automation (<c>DTE.ToolWindows.ErrorList</c>)
        /// - the simplest API that's already transitively available with no new package reference.
        /// Only files with at least one diagnostic are included (the real server lists every file
        /// VS Code has "seen" recently, even with zero diagnostics - not a concept we can cheaply
        /// replicate, and not a meaningful loss since empty entries carry no information anyway).
        /// </summary>
        public async Task<JArray> GetDiagnosticsAsync(string? uri)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string? filterPath = null;
            if (!string.IsNullOrEmpty(uri))
            {
                try { filterPath = new Uri(uri!).LocalPath; }
                catch { filterPath = uri; }
            }

            var byFile = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);

            var dte = Package.GetGlobalService(typeof(SDTE)) as DTE2;
            var errorItems = dte?.ToolWindows?.ErrorList?.ErrorItems;
            if (errorItems != null)
            {
                for (int i = 1; i <= errorItems.Count; i++)
                {
                    ErrorItem item = errorItems.Item(i);
                    string file = item.FileName ?? "";
                    if (file.Length == 0) continue;
                    if (filterPath != null && !string.Equals(file, filterPath, StringComparison.OrdinalIgnoreCase)) continue;

                    if (!byFile.TryGetValue(file, out var arr))
                    {
                        arr = new JArray();
                        byFile[file] = arr;
                    }

                    arr.Add(new JObject
                    {
                        ["message"] = item.Description,
                        ["severity"] = item.ErrorLevel.ToString(),
                        ["line"] = item.Line,
                        ["column"] = item.Column
                    });
                }
            }

            var result = new JArray();
            foreach (var kv in byFile)
            {
                Uri fileUri;
                try { fileUri = new Uri(kv.Key); }
                catch { continue; }
                result.Add(new JObject { ["uri"] = fileUri.AbsoluteUri, ["diagnostics"] = kv.Value });
            }
            return result;
        }

        /// <summary>
        /// Opens a native VS diff comparing the current and proposed file content, with an InfoBar
        /// Accept/Reject affordance (VS's own <see cref="IVsDifferenceService"/> is read-only
        /// browsing UI with no built-in apply mechanism - confirmed during research). Blocks until
        /// the user resolves it. On Accept, writes new_file_contents to new_file_path for real.
        /// </summary>
        public async Task<(string status, string detail)> OpenDiffAsync(string oldFilePath, string newFilePath, string newFileContents, string tabName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string oldContent = "";
            bool leftIsTemp = true;
            if (File.Exists(oldFilePath))
            {
                try { oldContent = File.ReadAllText(oldFilePath); leftIsTemp = false; }
                catch { }
            }

            string leftPath = leftIsTemp ? WriteTempFile(oldContent, "old_" + Path.GetFileName(oldFilePath)) : oldFilePath;
            string rightPath = WriteTempFile(newFileContents, "new_" + Path.GetFileName(newFilePath));

            if (Package.GetGlobalService(typeof(SVsDifferenceService)) is not IVsDifferenceService diffService)
                return ("DIFF_REJECTED", "Difference service unavailable.");

            uint options = (uint)(__VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary
                | (leftIsTemp ? __VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary : 0));

            diffService.OpenComparisonWindow2(leftPath, rightPath, tabName, tabName, "Current", "Proposed", "", "", options);
            _openDiffTabNames.Add(tabName);

            var tcs = new TaskCompletionSource<(string, string)>();

            var model = new InfoBarModel(
                new[]
                {
                    new InfoBarTextSpan($"Claude Code proposes changes to {Path.GetFileName(newFilePath)}. "),
                    new InfoBarHyperlink("Accept"),
                    new InfoBarHyperlink("Reject"),
                },
                KnownMonikers.StatusInformation,
                isCloseButtonVisible: true);

            var infoBar = await VS.InfoBar.CreateAsync(model);
            if (infoBar == null)
            {
                _openDiffTabNames.Remove(tabName);
                return ("DIFF_REJECTED", tabName);
            }

            void Resolve(string status, string detail)
            {
                if (!tcs.TrySetResult((status, detail)))
                    return;

                // Fire-and-forget cleanup: Resolve is a sync callback from an event handler and
                // must return immediately; the caller (SendResultAsync) doesn't need to wait for
                // InfoBar/temp-file cleanup to complete before the MCP response goes out.
#pragma warning disable VSSDK007
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    try { infoBar.Close(); } catch { }
                    _openDiffTabNames.Remove(tabName);
                    CleanupTemp(rightPath);
                    if (leftIsTemp) CleanupTemp(leftPath);
                });
#pragma warning restore VSSDK007
            }

            infoBar.ActionItemClicked += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (e.ActionItem.Text == "Accept")
                {
                    try
                    {
                        File.WriteAllText(newFilePath, newFileContents);
                        Resolve("FILE_SAVED", newFileContents);
                    }
                    catch (Exception ex)
                    {
                        Resolve("DIFF_REJECTED", $"Failed to save: {ex.Message}");
                    }
                }
                else
                {
                    Resolve("DIFF_REJECTED", tabName);
                }
            };
            // InfoBar has no Closed event to detect the user dismissing it via the X button
            // without an explicit Accept/Reject click - known simplification, documented in
            // docs/Phase 3; Accept/Reject are the only two resolution paths for now.

            return await tcs.Task.ConfigureAwait(false);
        }

        private static string WriteTempFile(string content, string suggestedName)
        {
            string dir = Path.Combine(Path.GetTempPath(), "TeronClaudeCodeVS-diff");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{Guid.NewGuid():N}_{SanitizeFileName(suggestedName)}");
            File.WriteAllText(path, content);
            return path;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static void CleanupTemp(string path)
        {
            try
            {
                if (path.IndexOf("TeronClaudeCodeVS-diff", StringComparison.OrdinalIgnoreCase) >= 0)
                    File.Delete(path);
            }
            catch { }
        }
    }
}
