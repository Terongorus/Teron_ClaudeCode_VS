using ClaudeCodeVS.ViewModels;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeCodeVS.Core
{
    public partial class ClaudeCodeChatControl : UserControl
    {
        private readonly ChatSessionViewModel _vm = new ChatSessionViewModel();
        private string _solutionDirectory = "";

        public ClaudeCodeChatControl()
        {
            InitializeComponent();
            DataContext = _vm;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
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
            }

            _solutionDirectory = await GetWorkingDirectoryAsync();

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
            catch
            {
                // No solution open / VS services unavailable - fall back to the current directory below.
            }

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
            CommandMenuPopup.IsOpen = !CommandMenuPopup.IsOpen;
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

            if (text.StartsWith("/", StringComparison.Ordinal) && !text.Contains(" ") && !text.Contains("\n"))
            {
                string filter = text.Substring(1);
                var matches = _vm.SlashCommands
                    .Where(c => c.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count > 0)
                {
                    SlashCommandList.ItemsSource = matches;
                    SlashCommandList.SelectedIndex = 0;
                    SlashCommandPopup.IsOpen = true;
                    return;
                }
            }

            SlashCommandPopup.IsOpen = false;
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
