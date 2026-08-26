using TeronClaudeCodeVS.ViewModels;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace TeronClaudeCodeVS.Core
{
    public partial class ClaudeCodeChatControl : UserControl
    {
        private readonly ChatSessionViewModel _vm = new ChatSessionViewModel();
        private string _solutionDirectory = "";

        private string[] _projectFiles = Array.Empty<string>();
        private int _atTokenStart = -1;

        private static readonly HashSet<string> s_excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".git", "node_modules", "bin", "obj", ".vs", ".idea", "packages", "__pycache__", ".nuget" };

        public ClaudeCodeChatControl()
        {
            InitializeComponent();
            DataContext = _vm;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.PermissionRequestAdded += OnPermissionRequestAdded;
        }

#pragma warning disable VSTHRD100
        private async void OnLoaded(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            var options = ClaudeCodePackage.Instance?.GetOptions();

            if (options != null)
            {
                if (!string.IsNullOrWhiteSpace(options.DefaultModel))
                {
                    var model = _vm.Models.FirstOrDefault(m => string.Equals(m.Value, options.DefaultModel, StringComparison.OrdinalIgnoreCase));
                    if (model != null)
                        _vm.SelectedModel = model;
                }

                if (!string.IsNullOrWhiteSpace(options.DefaultPermissionMode))
                {
                    var mode = _vm.PermissionModes.FirstOrDefault(m => string.Equals(m.Value, options.DefaultPermissionMode, StringComparison.OrdinalIgnoreCase));
                    if (mode != null)
                        _vm.SelectedPermissionMode = mode;
                }

                if (!string.IsNullOrWhiteSpace(options.DefaultEffortLevel))
                {
                    var effort = _vm.ThinkingLevels.FirstOrDefault(t => string.Equals(t.EffortArg, options.DefaultEffortLevel, StringComparison.OrdinalIgnoreCase));
                    if (effort != null)
                        _vm.SelectedThinkingLevel = effort;
                }
            }

            MessageList.AddHandler(
                UIElement.MouseWheelEvent,
                new MouseWheelEventHandler(OnMessageListMouseWheel),
                handledEventsToo: true);

            _solutionDirectory = await GetWorkingDirectoryAsync();

            _ = IndexProjectFilesAsync();

            string? overridePath = string.IsNullOrWhiteSpace(options?.ClaudeExecutablePath) ? null : options!.ClaudeExecutablePath;
            if (_vm.Initialize(overridePath, _solutionDirectory))
                _vm.StartSession();

            UpdateSendStopVisibility();
            Keyboard.Focus(InputBox);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _vm.Dispose();
        }

#pragma warning disable VSTHRD001, VSTHRD110
        private void OnPermissionRequestAdded(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => ChatScrollViewer.ScrollToEnd()));
        }
#pragma warning restore VSTHRD001, VSTHRD110

        private async Task IndexProjectFilesAsync()
        {
            if (string.IsNullOrEmpty(_solutionDirectory)) return;
            string root = _solutionDirectory;
            _projectFiles = await Task.Run(() => EnumerateProjectFiles(root)).ConfigureAwait(false);
        }

        private static string[] EnumerateProjectFiles(string root)
        {
            var files = new List<string>(512);
            try { EnumerateRecursive(root, files); } catch { }
            return files.ToArray();
        }

        private static void EnumerateRecursive(string dir, List<string> files)
        {
            if (files.Count >= 5000) return;
            try
            {
                foreach (string file in Directory.GetFiles(dir))
                {
                    files.Add(file);
                    if (files.Count >= 5000) return;
                }
                foreach (string subDir in Directory.GetDirectories(dir))
                {
                    if (s_excludedDirs.Contains(Path.GetFileName(subDir))) continue;
                    EnumerateRecursive(subDir, files);
                }
            }
            catch { }
        }

        private static async Task<string> GetWorkingDirectoryAsync()
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

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatSessionViewModel.IsBusy))
                UpdateSendStopVisibility();
        }

        private void UpdateSendStopVisibility()
        {
            SendButton.Visibility = _vm.IsBusy ? Visibility.Collapsed : Visibility.Visible;
            StopButton.Visibility = _vm.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnNewSessionClicked(object sender, RoutedEventArgs e)
        {
            _vm.NewSession();
        }

        private void OnHistoryClicked(object sender, RoutedEventArgs e)
        {
            if (_vm.IsSessionHistoryVisible)
            {
                _vm.IsSessionHistoryVisible = false;
            }
            else
            {
                SessionSearchBox.Text = "";
                _vm.IsSessionHistoryVisible = true;
            }
        }

        private void OnCloseHistoryClicked(object sender, RoutedEventArgs e)
        {
            _vm.IsSessionHistoryVisible = false;
        }

        private void OnSessionSearchChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SessionSearchBox.Text;
            var view = CollectionViewSource.GetDefaultView(_vm.SessionHistory);
            if (string.IsNullOrWhiteSpace(filter))
                view.Filter = null;
            else
                view.Filter = obj => obj is SessionHistoryEntry entry &&
                    entry.Title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnSessionItemClicked(object sender, MouseButtonEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is SessionHistoryEntry entry)
                _vm.ResumeSessionEntry(entry);
        }

#pragma warning disable VSTHRD001, VSTHRD110
        private void OnEditSessionTitleClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is SessionHistoryEntry entry)
            {
                entry.IsEditing = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (SessionListBox.ItemContainerGenerator.ContainerFromItem(entry) is ListBoxItem container)
                    {
                        var tb = FindVisualChild<TextBox>(container);
                        if (tb != null) { tb.Focus(); tb.SelectAll(); }
                    }
                }));
            }
        }
#pragma warning restore VSTHRD001, VSTHRD110

        private void OnSessionTitleKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is SessionHistoryEntry entry)
            {
                if (e.Key == Key.Enter)
                {
                    _vm.CommitSessionEntryTitle(entry, tb.Text);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    entry.IsEditing = false;
                    e.Handled = true;
                }
            }
        }

        private void OnSessionTitleLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is SessionHistoryEntry entry)
                _vm.CommitSessionEntryTitle(entry, tb.Text);
        }

        private void OnDeleteSessionClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is SessionHistoryEntry entry)
                _vm.DeleteSessionEntry(entry);
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private void OnSettingsClicked(object sender, RoutedEventArgs e)
        {
            ClaudeCodePackage.Instance?.ShowOptions();
        }

        private void OnStopClicked(object sender, RoutedEventArgs e)
        {
            _vm.StopSession();
        }

        private void OnCommandMenuClicked(object sender, RoutedEventArgs e)
        {
            AccountUsagePopup.IsOpen = false;
            CommandMenuPopup.IsOpen = !CommandMenuPopup.IsOpen;
        }

#pragma warning disable VSTHRD100
        private async void OnAccountUsageClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            CommandMenuPopup.IsOpen = false;
            AccountUsagePopup.IsOpen = true;

            if (!string.IsNullOrEmpty(_vm.ClaudePath))
                await _vm.AccountUsage.RefreshAsync(_vm.ClaudePath);
        }

        private void OnCloseAccountUsageClicked(object sender, RoutedEventArgs e)
        {
            AccountUsagePopup.IsOpen = false;
        }

        private void OnCopyRawOutputClicked(object sender, RoutedEventArgs e)
        {
            if (_vm.RawOutput.Count > 0)
            {
                try { Clipboard.SetText(string.Join("\n", _vm.RawOutput)); }
                catch { }
            }
        }

        private void OnManageUsageClicked(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
            }
            catch { }
            e.Handled = true;
            AccountUsagePopup.IsOpen = false;
        }

        private void OnModelOptionClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ModelOption option)
                _vm.SelectedModel = option;

            CommandMenuPopup.IsOpen = false;
        }

        private void OnPermissionOptionClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is PermissionModeOption option)
                _vm.SelectedPermissionMode = option;

            CommandMenuPopup.IsOpen = false;
        }

        private void OnThinkingOptionClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ThinkingLevelOption option)
                _vm.SelectedThinkingLevel = option;

            CommandMenuPopup.IsOpen = false;
        }

        private void OnSlashCommandMenuItemClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is string command)
            {
                InputBox.Text = "/" + command + " ";
                InputBox.CaretIndex = InputBox.Text.Length;
            }

            CommandMenuPopup.IsOpen = false;
            Keyboard.Focus(InputBox);
        }

#pragma warning disable VSTHRD100
        private async void OnSendClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await SendCurrentInputAsync();
        }

        private async Task SendCurrentInputAsync()
        {
            string text = InputBox.Text;
            if (string.IsNullOrWhiteSpace(text) || !_vm.CanSend)
                return;

            InputBox.Clear();
            await _vm.SendMessageAsync(text);
        }

#pragma warning disable VSTHRD100
        private async void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (FilePickerPopup.IsOpen)
            {
                if (e.Key == Key.Down || e.Key == Key.Up)
                {
                    MoveFilePickerSelection(e.Key == Key.Down ? 1 : -1);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    ApplySelectedFile();
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    FilePickerPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                }
            }

            if (SlashCommandPopup.IsOpen)
            {
                if (e.Key == Key.Down || e.Key == Key.Up)
                {
                    MoveSlashCommandSelection(e.Key == Key.Down ? 1 : -1);
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    ApplySelectedSlashCommand();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape)
                {
                    SlashCommandPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
            {
                e.Handled = true;
                await SendCurrentInputAsync();
            }
        }

        private void OnInputTextChanged(object sender, TextChangedEventArgs e)
        {
            string text = InputBox.Text;
            int caret = InputBox.CaretIndex;

            // @ file picker — triggered by @token anywhere in the text
            int atIdx = FindAtTokenStart(text, caret);
            if (atIdx >= 0)
            {
                string filter = text.Substring(atIdx + 1, caret - atIdx - 1);
                string[] matches = FilterProjectFiles(filter);
                if (matches.Length > 0)
                {
                    _atTokenStart = atIdx;
                    FilePickerList.ItemsSource = matches;
                    FilePickerList.SelectedIndex = 0;
                    FilePickerPopup.IsOpen = true;
                    SlashCommandPopup.IsOpen = false;
                    return;
                }
            }

            FilePickerPopup.IsOpen = false;

            // / slash command picker — only triggers when the whole input is a single /word
            if (text.StartsWith("/", StringComparison.Ordinal) && !text.Contains(" ") && !text.Contains("\n"))
            {
                string filter = text.Substring(1);
                var slashMatches = _vm.SlashCommands
                    .Where(c => c.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (slashMatches.Count > 0)
                {
                    SlashCommandList.ItemsSource = slashMatches;
                    SlashCommandList.SelectedIndex = 0;
                    SlashCommandPopup.IsOpen = true;
                    return;
                }
            }

            SlashCommandPopup.IsOpen = false;
        }

        // Returns the index of the '@' that the caret is currently inside a token for,
        // or -1 if the caret is not inside an @token.
        private static int FindAtTokenStart(string text, int caret)
        {
            for (int i = caret - 1; i >= 0; i--)
            {
                char c = text[i];
                if (c == '@')
                {
                    // Valid trigger if '@' is at start of text or preceded by whitespace
                    if (i == 0 || char.IsWhiteSpace(text[i - 1]))
                        return i;
                    return -1;
                }
                // Crossed whitespace without hitting '@' — not in an @token
                if (char.IsWhiteSpace(c))
                    return -1;
            }
            return -1;
        }

        private string[] FilterProjectFiles(string filter)
        {
            if (_projectFiles.Length == 0) return Array.Empty<string>();

            return _projectFiles
                .Select(f => GetRelativePath(_solutionDirectory, f))
                .Where(rel =>
                    string.IsNullOrEmpty(filter) ||
                    Path.GetFileName(rel).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rel.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(rel =>
                {
                    string fn = Path.GetFileName(rel);
                    if (fn.StartsWith(filter, StringComparison.OrdinalIgnoreCase)) return 0;
                    if (fn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) return 1;
                    return 2;
                })
                .Take(20)
                .ToArray();
        }

        private void MoveFilePickerSelection(int delta)
        {
            int count = FilePickerList.Items.Count;
            if (count == 0) return;
            int next = FilePickerList.SelectedIndex + delta;
            if (next < 0) next = count - 1;
            if (next >= count) next = 0;
            FilePickerList.SelectedIndex = next;
        }

        private void OnFilePickerChosen(object sender, MouseButtonEventArgs e)
        {
            ApplySelectedFile();
        }

        private void ApplySelectedFile()
        {
            if (FilePickerList.SelectedItem is string relative && _atTokenStart >= 0)
            {
                int caret = InputBox.CaretIndex;
                string text = InputBox.Text;
                string insertion = "@" + relative + " ";
                InputBox.Text = text.Substring(0, _atTokenStart) + insertion + text.Substring(caret);
                InputBox.CaretIndex = _atTokenStart + insertion.Length;
                _atTokenStart = -1;
            }
            FilePickerPopup.IsOpen = false;
            Keyboard.Focus(InputBox);
        }

        private void MoveSlashCommandSelection(int delta)
        {
            int count = SlashCommandList.Items.Count;
            if (count == 0) return;

            int next = SlashCommandList.SelectedIndex + delta;
            if (next < 0) next = count - 1;
            if (next >= count) next = 0;
            SlashCommandList.SelectedIndex = next;
        }

        private void OnSlashCommandChosen(object sender, MouseButtonEventArgs e)
        {
            ApplySelectedSlashCommand();
        }

        private void ApplySelectedSlashCommand()
        {
            if (SlashCommandList.SelectedItem is string command)
            {
                InputBox.Text = "/" + command + " ";
                InputBox.CaretIndex = InputBox.Text.Length;
            }

            SlashCommandPopup.IsOpen = false;
            Keyboard.Focus(InputBox);
        }

#pragma warning disable VSTHRD100
        private async void OnAddActiveFileClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            var docView = await VS.Documents.GetActiveDocumentViewAsync();
            string? path = docView?.FilePath;
            if (string.IsNullOrEmpty(path))
                return;

            InsertContextReference(path!, null, null);
        }

#pragma warning disable VSTHRD100
        private async void OnAddSelectionClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            var docView = await VS.Documents.GetActiveDocumentViewAsync();
            string? path = docView?.FilePath;
            var textView = docView?.TextView;
            if (string.IsNullOrEmpty(path) || textView == null)
                return;

            ITextSelection selection = textView.Selection;
            if (selection.IsEmpty)
            {
                InsertContextReference(path!, null, null);
                return;
            }

            SnapshotPoint start = selection.Start.Position;
            SnapshotPoint end = selection.End.Position;
            int startLine = start.Snapshot.GetLineNumberFromPosition(start.Position) + 1;
            int endLine = end.Snapshot.GetLineNumberFromPosition(end.Position) + 1;

            InsertContextReference(path!, startLine, endLine);
        }

        private void InsertContextReference(string filePath, int? startLine, int? endLine)
        {
            string relative = GetRelativePath(_solutionDirectory, filePath);

            string reference = startLine.HasValue
                ? (startLine == endLine ? $"@{relative}#L{startLine}" : $"@{relative}#L{startLine}-L{endLine}")
                : $"@{relative}";

            int caret = InputBox.CaretIndex;
            string current = InputBox.Text;
            string insertion = reference + " ";

            InputBox.Text = current.Substring(0, caret) + insertion + current.Substring(caret);
            InputBox.CaretIndex = caret + insertion.Length;
            Keyboard.Focus(InputBox);
        }

        private static string GetRelativePath(string basePath, string fullPath)
        {
            if (string.IsNullOrEmpty(basePath))
                return fullPath;

            try
            {
                string baseFull = Path.GetFullPath(basePath);
                if (!baseFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    baseFull += Path.DirectorySeparatorChar;

                var baseUri = new Uri(baseFull);
                var fullUri = new Uri(Path.GetFullPath(fullPath));

                if (baseUri.Scheme != fullUri.Scheme)
                    return fullPath;

                Uri relativeUri = baseUri.MakeRelativeUri(fullUri);
                string relativePath = Uri.UnescapeDataString(relativeUri.ToString());
                return relativePath.Replace('\\', '/');
            }
            catch
            {
                return fullPath;
            }
        }

        private void OnMessageListMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0) return;
            e.Handled = true;
            ChatScrollViewer.ScrollToVerticalOffset(ChatScrollViewer.VerticalOffset - e.Delta);
        }

        private void OnChatScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange > 0)
            {
                bool wasAtBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ExtentHeightChange - 1;
                if (wasAtBottom)
                    ChatScrollViewer.ScrollToEnd();
            }
        }
    }
}
