using TeronClaudeCodeVS.ViewModels;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace TeronClaudeCodeVS.Core
{
    public partial class ClaudeCodeChatControl : UserControl
    {
        private readonly ChatSessionViewModel _vm = new();
        private string _solutionDirectory = "";

        private string[] _projectFiles = [];
        private int _atTokenStart = -1;
        private bool _sendOnCtrlEnter;

        // A tool window docked in a shared pane fires WPF's Unloaded/Loaded on every tab switch
        // between sibling tabs, not just on a real open/close - this control instance (and _vm)
        // survives that switch. Guards OnLoaded's one-time setup so a tab switch back doesn't
        // re-run session start (which would restart the live CLI process mid-turn) or re-apply
        // Options-page defaults over whatever the user has since changed live.
        private bool _initialized;

        private static readonly HashSet<string> s_excludedDirs = new(StringComparer.OrdinalIgnoreCase)
            { ".git", "node_modules", "bin", "obj", ".vs", ".idea", "packages", "__pycache__", ".nuget" };

        public ClaudeCodeChatControl()
        {
            InitializeComponent();
            DataContext = _vm;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.PermissionRequestAdded += OnPermissionRequestAdded;
            _vm.PlanFileReadyToOpen += OnPlanFileReadyToOpen;
            _vm.InputPrefillRequested += OnInputPrefillRequested;
        }

#pragma warning disable VSTHRD100
        private async void OnLoaded(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_initialized)
            {
                // Re-entering after a tab switch, not a fresh open - the session (if any) is
                // still running and must not be touched. Just restore focus for convenience.
                Keyboard.Focus(InputBox);
                return;
            }
            _initialized = true;

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

                _sendOnCtrlEnter = options.SendOnCtrlEnter;

                _vm.SetAdvancedOptions(
                    options.AdditionalDirectories, options.AllowedTools, options.DisallowedTools,
                    options.AppendSystemPrompt, options.SystemPrompt,
                    options.McpConfigPaths, options.StrictMcpConfig,
                    options.SwitchModelsAutomatically, options.FallbackModel);
            }

            // UX-6: resolved before the first await in this handler. The call touches DTE, which
            // is main-thread-only; WPF raises Loaded on the UI thread and nothing above this point
            // awaits, so we are on it. The analyzer cannot see that through an async void handler,
            // hence the local suppression rather than a redundant thread switch.
#pragma warning disable VSTHRD010
            ApplyFocusShortcutHint();
#pragma warning restore VSTHRD010

            MessageList.AddHandler(
                UIElement.MouseWheelEvent,
                new MouseWheelEventHandler(OnMessageListMouseWheel),
                handledEventsToo: true);

            _vm.RawOutput.Add($"[cwd-diag] OnLoaded firing, ClaudeCodePackage.Instance={(ClaudeCodePackage.Instance == null ? "NULL" : "set")}");
            _solutionDirectory = await VsIdeToolHandlers.GetWorkingDirectoryAsync(line => _vm.RawOutput.Add(line));
            _vm.RawOutput.Add($"[cwd-diag] resolved _solutionDirectory = {_solutionDirectory}");

            _ = IndexProjectFilesAsync();

            string? overridePath = string.IsNullOrWhiteSpace(options?.ClaudeExecutablePath) ? null : options!.ClaudeExecutablePath;
            if (_vm.Initialize(overridePath, _solutionDirectory))
                _vm.StartSession();

            // FEAT-8: asks the SAPI registry whether dictation is possible at all; never opens the
            // microphone, so it is safe on the load path.
            _vm.ProbeVoiceAvailability();

            UpdateSendStopVisibility();
            Keyboard.Focus(InputBox);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // NOT real teardown - see the _initialized comment above. A tab switch away from this
            // tool window's shared pane fires Unloaded even though the tool window stays open, so
            // the running session must survive it. Only the mic engine (lazily recreated on next
            // use) is safe to tear down here; the real session lives until DisposeSession() is
            // called from ClaudeCodeToolWindow's own Dispose override.
            StopDictation();
            _voice?.Dispose();
            _voice = null;
        }

        /// <summary>Real teardown for when the tool window itself is actually closing - called
        /// from <see cref="ClaudeCodeToolWindow"/>'s Dispose override, never from OnUnloaded.</summary>
        public void DisposeSession() => _vm.Dispose();

        #region FEAT-9: history tabs, running sessions, cloud

        private void OnHistoryLocalTabClicked(object sender, RoutedEventArgs e)
        {
            _vm.SelectedHistoryTab = ChatSessionViewModel.HistoryTab.Local;
        }

#pragma warning disable VSTHRD100
        private async void OnHistoryRunningTabClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            _vm.SelectedHistoryTab = ChatSessionViewModel.HistoryTab.Running;
            await _vm.RefreshAgentSessionsAsync();
        }

        private void OnHistoryCloudTabClicked(object sender, RoutedEventArgs e)
        {
            _vm.SelectedHistoryTab = ChatSessionViewModel.HistoryTab.Cloud;
        }

#pragma warning disable VSTHRD100
        private async void OnRefreshAgentSessionsClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await _vm.RefreshAgentSessionsAsync();
        }

        private void OnOpenAgentSessionHereClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is AgentSessionEntry entry)
                _vm.OpenAgentSessionHere(entry);
        }

        private void OnOpenAgentSessionInTerminalClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is AgentSessionEntry entry)
                _vm.OpenAgentSessionInTerminal(entry);
        }

        private void OnOpenCloudSessionClicked(object sender, RoutedEventArgs e)
        {
            _vm.OpenCloudSession();
        }

        #endregion

        #region FEAT-8: dictation

        private VoiceInput? _voice;

        /// <summary>When the mic press began, so a tap can be told from a hold. See OnMicButtonUp.</summary>
        private DateTime _micPressedUtc;

        /// <summary>
        /// A press below this is a tap and toggles; anything longer is a hold and records only while
        /// held. 400ms is above a deliberate click and below the shortest usable utterance, so the
        /// two gestures do not overlap in practice.
        /// </summary>
        private static readonly TimeSpan MicTapThreshold = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// Set by the mouse gesture so the Click that follows it does not undo it.
        ///
        /// A press-and-release raises MouseDown, MouseUp *and* Click, and all three are wanted:
        /// the mouse pair is what makes hold-to-talk possible, and Click is what makes the button
        /// work for a keyboard (Space/Enter), a screen reader and UI Automation's InvokePattern -
        /// none of which raise a mouse event at all. Without this flag the two paths would fight,
        /// with the Click stopping what the press had just started.
        /// </summary>
        private bool _micGestureHandled;

        private void OnMicButtonDown(object sender, MouseButtonEventArgs e)
        {
            _micPressedUtc = DateTime.UtcNow;

            // Only the press that actually starts dictation needs to suppress the Click that
            // follows it (see _micGestureHandled's remarks - Click would otherwise immediately
            // undo what this press just started). A press while already dictating does nothing
            // here, so it must NOT set the flag, or the second tap's Click - the toggle's only
            // way to stop dictation - gets swallowed the same way and the mic can never be
            // stopped by clicking it again.
            if (_vm.IsDictating) return;
            _micGestureHandled = true;
            StartDictation();
        }

        /// <summary>The keyboard and automation path - see <see cref="_micGestureHandled"/>.</summary>
        private void OnMicClicked(object sender, RoutedEventArgs e)
        {
            if (_micGestureHandled)
            {
                _micGestureHandled = false;
                return;
            }

            if (_vm.IsDictating) StopDictation();
            else StartDictation();
        }

        /// <summary>
        /// Completes the gesture baseline's tooltip promises - "Tap or hold to record".
        ///
        /// Both gestures start recording on the way down, so the mic is live from the first
        /// millisecond either way; what the release decides is whether recording *continues*. A
        /// quick tap leaves it running (press again to stop); a hold ends it here.
        /// </summary>
        private void OnMicButtonUp(object sender, MouseButtonEventArgs e)
        {
            bool wasHeld = DateTime.UtcNow - _micPressedUtc >= MicTapThreshold;
            if (wasHeld) StopDictation();
        }

        private void StartDictation()
        {
            if (!_vm.IsVoiceAvailable) return;

            if (_voice == null)
            {
                _voice = new VoiceInput();
                _voice.TextRecognized += OnVoiceTextRecognized;
                _voice.TextHypothesized += OnVoiceTextHypothesized;
                _voice.ListeningChanged += OnVoiceListeningChanged;
                _voice.Failed += OnVoiceFailed;
            }

            string? error = _voice.Start();
            if (error != null)
            {
                // The "no microphone" case, which cannot be known until the device is asked for.
                _vm.AddSystemNotice(error, isError: true);
                _vm.IsDictating = false;
            }
        }

        private void StopDictation()
        {
            _voice?.Stop();
            _vm.VoiceHypothesis = "";
        }

        // Every one of these arrives on a recognition worker thread - see VoiceInput's remarks -
        // so each hops to the dispatcher before touching the view model or the composer.
#pragma warning disable VSTHRD001, VSTHRD110
        private void OnVoiceTextRecognized(object sender, string text) =>
            Dispatcher.BeginInvoke(new Action(() => AppendDictatedText(text)));

        private void OnVoiceTextHypothesized(object sender, string text) =>
            Dispatcher.BeginInvoke(new Action(() => _vm.VoiceHypothesis = text));

        private void OnVoiceListeningChanged(object sender, bool listening) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _vm.IsDictating = listening;
                if (!listening) _vm.VoiceHypothesis = "";
            }));

        private void OnVoiceFailed(object sender, string reason) =>
            Dispatcher.BeginInvoke(new Action(() => _vm.AddSystemNotice(reason, isError: true)));
#pragma warning restore VSTHRD001, VSTHRD110

        /// <summary>
        /// Inserts a recognised phrase at the caret rather than appending at the end, so dictating
        /// into a half-written message puts the words where the user was looking. The space rule is
        /// the one a person would apply by hand: separate from what precedes it, unless there is
        /// nothing or whitespace already.
        /// </summary>
        private void AppendDictatedText(string text)
        {
            _vm.VoiceHypothesis = "";
            if (text.Length == 0) return;

            int caret = Math.Max(0, Math.Min(InputBox.CaretIndex, InputBox.Text.Length));
            bool needsSpace = caret > 0 && !char.IsWhiteSpace(InputBox.Text[caret - 1]);
            string insert = needsSpace ? " " + text : text;

            InputBox.Text = InputBox.Text.Insert(caret, insert);
            InputBox.CaretIndex = caret + insert.Length;
            InputBox.Focus();
        }

        #endregion

#pragma warning disable VSTHRD001, VSTHRD110
        private void OnPermissionRequestAdded(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => ChatScrollViewer.ScrollToEnd()));
        }
#pragma warning restore VSTHRD001, VSTHRD110

        // Opens the CLI-written plan file as a real native VS tab as soon as it's ready for
        // review, matching the real extension's behavior of surfacing the plan as a separate
        // document instead of only inline in the chat (see docs/Phase 4). Also used to re-open
        // the tab via PlanApprovalViewModel.ReopenTabCommand if the user closed it.
        //
        // Deliberately the plain source tab, not VS 18's native Markdown "preview" split view -
        // that was tried via IVsUIShellOpenDocument.OpenStandardEditor with the MarkdownPreview
        // logical view GUID and crashed VS live with an AccessViolationException from inside the
        // interop call itself (2026-08-27). That's a corrupted-state exception - uncatchable by a
        // normal try/catch, so the earlier fallback-on-failure logic here never even ran. Reverted
        // to the plan's original "out of scope for this pass" call rather than re-attempt a native
        // API that's already demonstrated it can crash the host process.
#pragma warning disable VSTHRD100
        private async void OnPlanFileReadyToOpen(object sender, string planFilePath)
#pragma warning restore VSTHRD100
        {
            if (string.IsNullOrEmpty(planFilePath) || !File.Exists(planFilePath))
                return;

            await VS.Documents.OpenInPreviewTabAsync(planFilePath);
        }

        private async Task IndexProjectFilesAsync()
        {
            if (string.IsNullOrEmpty(_solutionDirectory)) return;
            string root = _solutionDirectory;
            _projectFiles = await Task.Run(() => EnumerateProjectFiles(root)).ConfigureAwait(false);
        }

        private static string[] EnumerateProjectFiles(string root)
        {
            List<string> files = new(512);
            try { EnumerateRecursive(root, files); } catch { }
            return [.. files];
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

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatSessionViewModel.IsBusy))
                UpdateSendStopVisibility();

            // UX-3/GAP-1/GAP-3: the 1/2/3 shortcuts in OnInputPreviewKeyDown only fire while
            // keyboard focus is inside InputBox, but nothing else puts focus there - it can just
            // as easily be sitting on whatever button the user last clicked (e.g. a previous
            // card's own "Continue in Terminal"). Without this, a new card renders with working
            // mouse buttons but keystrokes that silently do nothing.
            if (e.PropertyName == nameof(ChatSessionViewModel.PendingPermissionRequest) &&
                _vm.PendingPermissionRequest != null)
                InputBox.Focus();
            if (e.PropertyName == nameof(ChatSessionViewModel.PendingChoiceCard) &&
                _vm.PendingChoiceCard != null)
                InputBox.Focus();

            // The Model/Permission Mode/Effort dropdowns in the chat header only ever lived in
            // the view model - picking a value there was never written back to the Options page,
            // so it reverted to the "Default *" settings (or the hardcoded ctor defaults) on every
            // new DevEnv session. Persisting on every change, not just at Loaded, is what makes
            // "whatever I last picked in the dropdown" survive a restart.
            //
            // Deliberately per-property, not "write all three defaults on any change": OnLoaded
            // applies the three loaded defaults sequentially (SelectedModel, then
            // SelectedPermissionMode, then SelectedThinkingLevel). A live test caught a real bug
            // in an earlier version of this fix that wrote all three every time - assigning
            // SelectedModel fired this handler before SelectedPermissionMode/SelectedThinkingLevel
            // had been updated from their loaded values, so it read back their still-default
            // in-memory state and stomped the real persisted DefaultPermissionMode/DefaultEffortLevel
            // on disk before OnLoaded ever got to apply them. Writing only the one property that
            // actually changed makes the three settings independent regardless of load order.
            var options = ClaudeCodePackage.Instance?.GetOptions();
            if (options == null)
                return;

            if (e.PropertyName == nameof(ChatSessionViewModel.SelectedModel))
            {
                options.DefaultModel = _vm.SelectedModel.Value ?? "";
                options.SaveSettingsToStorage();
            }
            else if (e.PropertyName == nameof(ChatSessionViewModel.SelectedPermissionMode))
            {
                options.DefaultPermissionMode = _vm.SelectedPermissionMode.Value ?? "";
                options.SaveSettingsToStorage();
            }
            else if (e.PropertyName == nameof(ChatSessionViewModel.SelectedThinkingLevel))
            {
                options.DefaultEffortLevel = _vm.SelectedThinkingLevel.EffortArg ?? "";
                options.SaveSettingsToStorage();
            }
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
                // FEAT-9: the overlay now has three panes, so it opens on a known one rather than
                // wherever it was left - a Cloud paste box is not what "History" should show first.
                _vm.SelectedHistoryTab = ChatSessionViewModel.HistoryTab.Local;
                _vm.OpenSessionHistory();
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

#pragma warning disable VSTHRD100
        private async void OnStopClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await _vm.StopSessionAsync();
        }

        // The four menu buttons (palette/model/permission/effort) share one mutual-exclusion rule
        // with Account & Usage - only one of the five popups is ever open at a time, same behavior
        // as the single combined menu they replaced.
        private void CloseAllMenuPopups()
        {
            PalettePopup.IsOpen = false;
            ModelPopup.IsOpen = false;
            PermissionPopup.IsOpen = false;
            EffortPopup.IsOpen = false;
            AccountUsagePopup.IsOpen = false;
            McpPopup.IsOpen = false;
            PluginsPopup.IsOpen = false;
            AddMenuPopup.IsOpen = false;
            MessageActionsPopup.IsOpen = false;

            // Not RewindConfirmPopup: it is StaysOpen and is dismissed only by its own two
            // buttons, because it is the last thing standing between a click and the working tree.
            RewindPopup.IsOpen = false;
        }

        private void OnPaletteMenuClicked(object sender, RoutedEventArgs e)
        {
            bool willOpen = !PalettePopup.IsOpen;
            CloseAllMenuPopups();

            // UX-4: open on the unfiltered list. Carrying the previous filter forward would show
            // an apparently empty palette to someone who does not remember typing it.
            if (willOpen && PaletteFilterBox.Text.Length > 0)
                PaletteFilterBox.Text = "";

            PalettePopup.IsOpen = willOpen;
        }

        /// <summary>
        /// UX-4: live-filters the palette's command list. Filters the default view of the
        /// view model's SlashCommands collection rather than a private copy, so the palette keeps
        /// the single A-Z ordering established in OnSessionInitialized (UX-5).
        /// </summary>
        private void OnPaletteFilterChanged(object sender, TextChangedEventArgs e)
        {
            ICollectionView view = CollectionViewSource.GetDefaultView(_vm.SlashCommands);
            if (view == null) return;

            string filter = PaletteFilterBox.Text.Trim();
            view.Filter = filter.Length == 0
                ? null
                : o => o is string command
                       && command.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// UX-6: writes the input placeholder's focus hint from the binding Visual Studio actually
        /// holds for the tool-window command, rather than hard-coding the chord declared in
        /// Menus.vsct. A user who rebinds it, or a binding that failed to register, would otherwise
        /// leave the UI advertising a shortcut that does nothing.
        /// </summary>
        private void ApplyFocusShortcutHint()
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                EnvDTE.DTE? dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                EnvDTE.Command? command = dte?.Commands?.Item(
                    TeronClaudeCodeVS.Commands.GuidList.guidClaudeCodeCmdSet.ToString("B"),
                    (int)TeronClaudeCodeVS.Commands.PkgCmdIDList.cmdidClaudeCodeWindow);
                if (command?.Bindings is not object[] bindings) return;

                // Bindings look like "Global::Ctrl+Alt+C" or "Text Editor::Ctrl+Alt+C".
                string? chord = bindings
                    .OfType<string>()
                    .Select(b => b.Contains("::") ? b.Substring(b.IndexOf("::", StringComparison.Ordinal) + 2) : b)
                    .FirstOrDefault(b => !string.IsNullOrWhiteSpace(b));

                if (!string.IsNullOrWhiteSpace(chord))
                    InputPlaceholder.Text = $"Ask Claude anything\u2026  ({chord} to focus)";
            }
            catch
            {
                // The plain placeholder set in XAML is a perfectly good fallback.
            }
        }

        private void OnModelMenuClicked(object sender, RoutedEventArgs e)
        {
            bool willOpen = !ModelPopup.IsOpen;
            CloseAllMenuPopups();
            ModelPopup.IsOpen = willOpen;
        }

        private void OnPermissionMenuClicked(object sender, RoutedEventArgs e)
        {
            bool willOpen = !PermissionPopup.IsOpen;
            CloseAllMenuPopups();
            PermissionPopup.IsOpen = willOpen;
        }

        private void OnEffortMenuClicked(object sender, RoutedEventArgs e)
        {
            bool willOpen = !EffortPopup.IsOpen;
            CloseAllMenuPopups();
            EffortPopup.IsOpen = willOpen;
        }

        private void OnTranscriptModeClicked(object sender, RoutedEventArgs e)
        {
            TranscriptModePopup.IsOpen = !TranscriptModePopup.IsOpen;
        }

        private void OnTranscriptModeOptionClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is TranscriptModeOption option)
                _vm.CurrentTranscriptMode = option;

            TranscriptModePopup.IsOpen = false;
        }

#pragma warning disable VSTHRD100
        private async void OnAccountUsageClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            await OpenAccountUsagePopupAsync();
        }

        private async Task OpenAccountUsagePopupAsync()
        {
            CloseAllMenuPopups();
            AccountUsagePopup.IsOpen = true;

            if (!string.IsNullOrEmpty(_vm.ClaudePath))
                await _vm.AccountUsage.RefreshAsync(_vm.ClaudePath);
        }

        private void OnCloseAccountUsageClicked(object sender, RoutedEventArgs e)
        {
            AccountUsagePopup.IsOpen = false;
        }

        // ── FEAT-4: MCP servers panel ─────────────────────────────────────────

#pragma warning disable VSTHRD100 // WPF Click handlers are void by contract.
        private async void OnMcpServersClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            CloseAllMenuPopups();
            McpPopup.IsOpen = true;

            // The working directory is not incidental: `claude mcp list` resolves project-scoped
            // servers out of the .mcp.json beside it, so the solution directory is the right scope.
            await _vm.McpServers.RefreshAsync(_vm.ClaudePath, _vm.WorkingDirectory);
        }

        private void OnCloseMcpClicked(object sender, RoutedEventArgs e)
        {
            McpPopup.IsOpen = false;
        }

        // ── FEAT-5: Manage plugins panel ──────────────────────────────────────

#pragma warning disable VSTHRD100
        private async void OnManagePluginsClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            CloseAllMenuPopups();
            PluginsPopup.IsOpen = true;
            await _vm.Plugins.RefreshAsync(_vm.ClaudePath, _vm.WorkingDirectory);
        }

        private void OnClosePluginsClicked(object sender, RoutedEventArgs e)
        {
            PluginsPopup.IsOpen = false;
        }

        // ── FEAT-1: rewind and fork ───────────────────────────────────────────

        private void OnRewindClicked(object sender, RoutedEventArgs e)
        {
            CloseAllMenuPopups();
            _vm.OpenRewindPicker();
        }

        private void OnCloseRewindClicked(object sender, RoutedEventArgs e)
        {
            _vm.IsRewindPickerVisible = false;
        }

        /// <summary>
        /// The per-message `…`. The message it belongs to rides on the button's Tag, since the
        /// popup is shared and its own DataContext is the view model rather than any one message.
        /// </summary>
        private void OnMessageActionsClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not ChatMessageViewModel message)
                return;

            CloseAllMenuPopups();
            _messageActionsTarget = message;
            MessageActionsPopup.PlacementTarget = button;
            MessageActionsPopup.IsOpen = true;
        }

        private ChatMessageViewModel? _messageActionsTarget;

        /// <summary>
        /// Resolves the `…` menu's message to a rewind point, or explains in the transcript why it
        /// could not. Deliberately not silent: this is the path where a wrong answer would restore
        /// files from the wrong moment, so "I could not match this message" has to be said.
        /// </summary>
        private bool TryTakeMessageActionTarget(out RewindPoint? point)
        {
            point = null;
            MessageActionsPopup.IsOpen = false;

            ChatMessageViewModel? message = _messageActionsTarget;
            _messageActionsTarget = null;
            if (message == null)
                return false;

            if (_vm.TryResolveRewindPoint(message, out point, out string? problem))
                return true;

            _vm.AddSystemNotice(problem ?? "That message cannot be rewound to.", isError: true);
            return false;
        }

#pragma warning disable VSTHRD100 // WPF Click handlers are void by contract.
        private async void OnMessageForkClicked(object sender, RoutedEventArgs e)
        {
            if (TryTakeMessageActionTarget(out RewindPoint? point) && point != null)
                await _vm.BeginRewindAsync(point, RewindAction.Fork);
        }

        private async void OnMessageRewindCodeClicked(object sender, RoutedEventArgs e)
        {
            if (TryTakeMessageActionTarget(out RewindPoint? point) && point != null)
                await _vm.BeginRewindAsync(point, RewindAction.RewindCode);
        }

        private async void OnMessageForkAndRewindClicked(object sender, RoutedEventArgs e)
        {
            if (TryTakeMessageActionTarget(out RewindPoint? point) && point != null)
                await _vm.BeginRewindAsync(point, RewindAction.ForkAndRewindCode);
        }

        private async void OnRewindForkClicked(object sender, RoutedEventArgs e)
            => await BeginPickerRewindAsync(RewindAction.Fork);

        private async void OnRewindCodeClicked(object sender, RoutedEventArgs e)
            => await BeginPickerRewindAsync(RewindAction.RewindCode);

        private async void OnRewindForkAndCodeClicked(object sender, RoutedEventArgs e)
            => await BeginPickerRewindAsync(RewindAction.ForkAndRewindCode);

        private async void OnConfirmRewindClicked(object sender, RoutedEventArgs e)
        {
            await _vm.ConfirmRewindAsync();
        }
#pragma warning restore VSTHRD100

        private Task BeginPickerRewindAsync(RewindAction action)
        {
            RewindPoint? point = _vm.SelectedRewindPoint;
            return point == null ? Task.CompletedTask : _vm.BeginRewindAsync(point, action);
        }

        private void OnCancelRewindClicked(object sender, RoutedEventArgs e)
        {
            _vm.CancelRewind();
        }

        /// <summary>
        /// Puts the rewound-to message back in the composer. Going back to a point is nearly always
        /// a prelude to saying it differently, so the text is handed back rather than discarded -
        /// and it replaces whatever is there, because a fork has just reset the conversation the
        /// half-typed line belonged to.
        /// </summary>
        private void OnInputPrefillRequested(object? sender, string text)
        {
            InputBox.Text = text;
            InputBox.CaretIndex = InputBox.Text.Length;
            InputBox.Focus();
        }

        private void OnPluginsTabClicked(object sender, RoutedEventArgs e)
        {
            _vm.Plugins.SelectedTab = PluginsTab.Plugins;
        }

        private void OnMarketplacesTabClicked(object sender, RoutedEventArgs e)
        {
            _vm.Plugins.SelectedTab = PluginsTab.Marketplaces;
        }

        /// <summary>
        /// Opens a documentation link in the system browser. Shared by both panels' footers - a
        /// Hyperlink inside a Popup does nothing on its own, RequestNavigate has to be handled.
        /// </summary>
        private void OnDocsLinkClicked(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
            }
            catch
            {
                // No browser, or the shell refused - nothing useful to say about it here.
            }

            e.Handled = true;
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

            ModelPopup.IsOpen = false;
        }

        private void OnPermissionOptionClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is PermissionModeOption option)
                _vm.SelectedPermissionMode = option;

            PermissionPopup.IsOpen = false;
        }

        private void OnThinkingOptionClicked(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is ThinkingLevelOption option)
                _vm.SelectedThinkingLevel = option;

            EffortPopup.IsOpen = false;
        }

#pragma warning disable VSTHRD100
        private async void OnSlashCommandMenuItemClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            PalettePopup.IsOpen = false;

            if (((FrameworkElement)sender).DataContext is string command)
            {
                // Picked from the dedicated command menu (as opposed to typed-and-autocompleted,
                // where the user may still be adding arguments) - run it immediately instead of
                // just prefilling the box and waiting for a manual Enter.
                InputBox.Text = "/" + command;
                await SendCurrentInputAsync();
            }

            Keyboard.Focus(InputBox);
        }

        /// <summary>
        /// FEAT-2: open the native side-by-side tab for whichever card's button was pressed. The
        /// card itself is the DataContext, so one handler serves both the pending-approval card
        /// and the finished tool call.
        /// </summary>
        private void OnOpenDiffTabClicked(object sender, RoutedEventArgs e)
        {
            _vm.OpenDiffTab(((FrameworkElement)sender).DataContext);
        }

        /// <summary>GAP-1: one of the five Customize rows was picked - show its hand-off card.</summary>
        private void OnTerminalHandoffClicked(object sender, RoutedEventArgs e)
        {
            PalettePopup.IsOpen = false;

            if (((FrameworkElement)sender).DataContext is TerminalHandoffEntry entry)
                _vm.ShowTerminalHandoff(entry);
        }

        /// <summary>
        /// GAP-2: launch the CLI interactively with no initial command. Reports the outcome in
        /// the transcript either way - a terminal that silently failed to open would otherwise
        /// look identical to one that opened behind the IDE window.
        /// </summary>
        private void OnOpenInTerminalClicked(object sender, RoutedEventArgs e)
        {
            PalettePopup.IsOpen = false;
            _vm.AddSystemNotice(_vm.OpenInTerminal(null), isError: false);
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
            if ((string.IsNullOrWhiteSpace(text) && !_vm.HasPendingImages && !_vm.HasPendingFiles) || !_vm.CanSend)
                return;

            // /usage never reaches the model in the real CLI either - confirmed live (2026-08-26)
            // against the official VS Code extension, it opens a local panel with zero API cost.
            // Sending it as a chat message just burns a no-op turn (0 in/0 out tokens, nothing
            // useful shown). Open the same Account & Usage popup the toolbar button does instead.
            string trimmed = text.Trim();
            if (trimmed.Equals("/usage", StringComparison.OrdinalIgnoreCase))
            {
                InputBox.Clear();
                await OpenAccountUsagePopupAsync();
                return;
            }

            if (await TryHandleExtensionCommandAsync(trimmed))
                return;

            InputBox.Clear();
            await _vm.SendMessageAsync(text);
        }

        /// <summary>
        /// GAP-3. Runs the three commands baseline injects rather than passes through, so typing
        /// them does what it does in baseline instead of being sent to the model as prose.
        ///
        /// Measured 2026-08-29 against the shipped CLI (v2.1.251): its headless `init` event
        /// lists 50 slash commands and none of these is among them, which is what settled GAP-3's
        /// open question - they are extension-injected, and had to be built. All three ride the
        /// CLI's own control-request channel; see ChatSessionViewModel for the protocol side.
        /// </summary>
        private async Task<bool> TryHandleExtensionCommandAsync(string trimmed)
        {
            if (trimmed.Length == 0 || trimmed[0] != '/')
                return false;

            int split = trimmed.IndexOf(' ');
            string name = (split < 0 ? trimmed.Substring(1) : trimmed.Substring(1, split - 1)).ToLowerInvariant();
            string rest = split < 0 ? "" : trimmed.Substring(split + 1).Trim();

            switch (name)
            {
                case "btw":
                    if (rest.Length == 0)
                    {
                        // Baseline's own argumentHint for this command is "[question]".
                        _vm.AddSystemNotice("Ask the question after the command, e.g. /btw what does this repo use for logging?", isError: true);
                        InputBox.Clear();
                        return true;
                    }
                    InputBox.Clear();
                    await _vm.AskSideQuestionAsync(rest);
                    return true;

                case "feedback":
                    InputBox.Clear();
                    _vm.StartFeedback(rest);
                    return true;

                // "rc" is baseline's own alias for this command.
                case "remote-control":
                case "rc":
                    InputBox.Clear();
                    _vm.ToggleRemoteControl();
                    return true;

                default:
                    return false;
            }
        }

#pragma warning disable VSTHRD100
        private async void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
#pragma warning restore VSTHRD100
        {
            // FEAT-8. Baseline's mic advertises Ctrl+D and this is the chord it advertises. Handled
            // ahead of the pickers deliberately: it is a modifier chord, so it cannot collide with
            // their arrow/Enter/Escape navigation, and dictating with a picker open is legitimate.
            // Marked handled either way when dictation is available, so VS's own Ctrl+D never sees
            // it while focus is in this box.
            if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control && _vm.IsVoiceAvailable)
            {
                if (_vm.IsDictating) StopDictation();
                else StartDictation();
                e.Handled = true;
                return;
            }

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

            // UX-3: while an approval card is waiting and the user has not started typing a
            // message, the number keys answer it and Esc denies it - matching baseline's
            // `1 Yes / 2 Yes, allow all edits / 3 No` plus `Esc to cancel`. Gating on an empty
            // input box means a message that legitimately starts with "1" is never swallowed.
            PermissionRequestViewModel? pending = _vm.PendingPermissionRequest;
            if (pending != null && !pending.IsResolved && InputBox.Text.Length == 0
                && Keyboard.Modifiers == ModifierKeys.None)
            {
                int choice = e.Key switch
                {
                    Key.D1 or Key.NumPad1 => 1,
                    Key.D2 or Key.NumPad2 => 2,
                    Key.D3 or Key.NumPad3 => 3,
                    _ => 0,
                };

                if (choice != 0 && pending.TryHandleShortcut(choice))
                {
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape)
                {
                    pending.DenyCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            // GAP-1/GAP-3: the same 1/2 convention for the two-choice cards, under the same
            // empty-input gate. Checked after the permission card so an approval - which is
            // blocking the agent - always wins the number keys.
            ChoiceCardViewModel? pendingCard = _vm.PendingChoiceCard;
            if (pendingCard != null && InputBox.Text.Length == 0 && Keyboard.Modifiers == ModifierKeys.None)
            {
                int pick = e.Key switch
                {
                    Key.D1 or Key.NumPad1 => 1,
                    Key.D2 or Key.NumPad2 => 2,
                    _ => 0,
                };

                if (pick != 0 && pendingCard.TryHandleShortcut(pick))
                {
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape)
                {
                    pendingCard.SecondaryCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
            }

            // UX-2: Shift+Tab cycles permission modes, as baseline does. Placed after the picker
            // popups so Tab keeps its existing accept-completion meaning while one is open.
            if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                _vm.CycleToNextPermissionMode();
                e.Handled = true;
                return;
            }

            bool isSendChord = _sendOnCtrlEnter
                ? e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control
                : e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift;

            if (isSendChord)
            {
                e.Handled = true;
                await SendCurrentInputAsync();
            }
        }

        private void OnInputTextChanged(object sender, TextChangedEventArgs e) => UpdateInputPickers();

        /// <summary>
        /// Opens or closes the @-mention and /-command pickers for whatever is in the input now.
        /// Split out of the TextChanged handler because a programmatic insert (FEAT-6's "Add
        /// context") sets Text and CaretIndex as two separate assignments - TextChanged fires on
        /// the first, while the caret is still where it was, so the picker has to be asked again
        /// once both have landed.
        /// </summary>
        private void UpdateInputPickers()
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
                List<string> slashMatches = [.. _vm.SlashCommands.Where(c => c.StartsWith(filter, StringComparison.OrdinalIgnoreCase))];

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
            if (_projectFiles.Length == 0) return [];

            return [.. _projectFiles
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
                .Take(20)];
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

        // Real Anthropic Messages API image content-block shape confirmed by reading the official
        // VS Code extension's webview bundle directly (2026-08-27) - it reads pasted clipboard
        // files the same way, via DataTransfer items rather than the WPF-specific event used here.
        private void OnInputBoxPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Bitmap))
                return;

            if (e.DataObject.GetData(DataFormats.Bitmap) is not BitmapSource bitmap)
                return;

            _vm.AddPendingImage(EncodeBitmapToPngBase64(bitmap), bitmap);
            e.CancelCommand();
        }

        private static string EncodeBitmapToPngBase64(BitmapSource bitmap)
        {
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using MemoryStream ms = new();
            encoder.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }

        private void OnRemovePendingImageClicked(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is PendingImageAttachment attachment)
                _vm.RemovePendingImage(attachment);
        }

        private void OnRemovePendingFileClicked(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is PendingFileAttachment attachment)
                _vm.RemovePendingFile(attachment);
        }

        // Extension allowlists ported verbatim from the real VS Code extension's own webview
        // bundle (its EK1/kX0 sets and file-type classifier), confirmed by reading the installed
        // bundle directly (2026-08-27) - not guessed. Local drops only give us a file path (no
        // browser-style MIME type), so classification here is by extension/bare-filename instead.
        private static readonly HashSet<string> s_imageExtensions =
            new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "webp" };

        private static readonly HashSet<string> s_textExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "json","yaml","yml","toml","ini","cfg","conf","config","env","properties","js","jsx","ts","tsx",
            "mjs","cjs","mts","cts","py","pyw","rb","go","rs","java","kt","kts","scala","c","h","cpp","hpp",
            "cc","cxx","cs","fs","fsx","swift","php","pl","pm","lua","r","jl","ex","exs","erl","hrl","clj",
            "cljs","cljc","elm","hs","ml","mli","v","sv","vhd","vhdl","asm","s","html","htm","xhtml","xml",
            "svg","css","scss","sass","less","vue","svelte","astro","sh","bash","zsh","fish","ps1","psm1",
            "psd1","bat","cmd","csv","tsv","sql","graphql","gql","prisma","md","mdx","markdown","rst","txt",
            "text","rtf","tex","latex","org","adoc","asciidoc","makefile","cmake","gradle","dockerfile",
            "containerfile","vagrantfile","rakefile","gemfile","podfile","fastfile","brewfile","procfile",
            "lock","sum","log","diff","patch","gitignore","gitattributes","editorconfig","prettierrc",
            "eslintrc","babelrc","npmrc","nvmrc","yarnrc"
        };

        private static readonly HashSet<string> s_textFilenamesWithoutExtension =
            new(StringComparer.OrdinalIgnoreCase) { "license", "readme", "changelog", "authors", "contributors", "copying", "makefile", "dockerfile" };

        private static bool HasDroppableData(IDataObject data) =>
            data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Bitmap);

        private void OnInputAreaDragEnter(object sender, DragEventArgs e)
        {
            if (HasDroppableData(e.Data))
                InputAreaBorder.BorderBrush = (Brush)FindResource("ClaudeAccentBrush");
        }

        private void OnInputAreaDragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasDroppableData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnInputAreaDragLeave(object sender, DragEventArgs e)
        {
            InputAreaBorder.ClearValue(Border.BorderBrushProperty);
        }

#pragma warning disable VSTHRD100
        private async void OnInputAreaDrop(object sender, DragEventArgs e)
#pragma warning restore VSTHRD100
        {
            InputAreaBorder.ClearValue(Border.BorderBrushProperty);

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                foreach (string path in (string[])e.Data.GetData(DataFormats.FileDrop))
                    await ImportAttachmentAsync(path);
            }
            else if (e.Data.GetDataPresent(DataFormats.Bitmap) && e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                _vm.AddPendingImage(EncodeBitmapToPngBase64(bitmap), bitmap);
            }
        }

        /// <summary>
        /// Stages one file as an attachment. Returns false when the file is not a type the CLI
        /// can be handed - which the two callers treat differently: a drag-and-drop of a folder
        /// full of mixed files skips them quietly, as baseline's own webview does, while a file
        /// the user explicitly picked in a dialog gets said out loud (FEAT-6).
        /// </summary>
        private async Task<bool> ImportAttachmentAsync(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;

                string fileName = Path.GetFileName(path);
                string ext = Path.GetExtension(path).TrimStart('.');

                if (s_imageExtensions.Contains(ext))
                {
                    byte[] bytes = await Task.Run(() => File.ReadAllBytes(path));
                    BitmapImage thumbnail = new();
                    using (MemoryStream ms = new(bytes))
                    {
                        thumbnail.BeginInit();
                        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                        thumbnail.StreamSource = ms;
                        thumbnail.EndInit();
                    }
                    thumbnail.Freeze();
                    // UX-9: the chip shows the real file name for a drop, not "Pasted image".
                    _vm.AddPendingImage(Convert.ToBase64String(bytes), thumbnail, fileName);
                }
                else if (string.Equals(ext, "pdf", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] bytes = await Task.Run(() => File.ReadAllBytes(path));
                    _vm.AddPendingFile(fileName, isPdf: true, Convert.ToBase64String(bytes));
                }
                else if (s_textExtensions.Contains(ext) || s_textFilenamesWithoutExtension.Contains(fileName))
                {
                    string text = await Task.Run(() => File.ReadAllText(path));
                    _vm.AddPendingFile(fileName, isPdf: false, text);
                }
                else
                {
                    // Not a type the CLI can be handed. Reported by the dialog caller, skipped
                    // silently by the drop caller - the real extension's own behaviour there.
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debugger.Log(0, "TeronClaudeCodeVS", $"[TeronClaudeCodeVS] Failed to import file '{path}': {ex.Message}\n");
                return false;
            }
        }

        // ─── FEAT-6: the + add menu ─────────────────────────────────────────────

        private void OnAddMenuClicked(object sender, RoutedEventArgs e)
        {
            bool willOpen = !AddMenuPopup.IsOpen;
            CloseAllMenuPopups();

            // The web box is a follow-up to one entry, not a standing part of the menu; a fresh
            // open should look the same every time rather than remembering the last visit.
            WebQueryPanel.Visibility = Visibility.Collapsed;
            WebQueryBox.Clear();

            AddMenuPopup.IsOpen = willOpen;
        }

        /// <summary>
        /// FEAT-6, "Upload from computer". The same staging path as a drag-and-drop, reached
        /// through a file dialog, with one deliberate difference: a picked file the CLI cannot be
        /// handed is named in the transcript rather than dropped on the floor. Silence is right for
        /// a drop of twenty mixed files; for a file someone chose by hand it just looks broken.
        /// </summary>
#pragma warning disable VSTHRD100 // WPF Click handlers are void by contract.
        private async void OnUploadFromComputerClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            AddMenuPopup.IsOpen = false;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Attach files",
                Multiselect = true,
                CheckFileExists = true,
                InitialDirectory = Directory.Exists(_solutionDirectory) ? _solutionDirectory : "",
                // Built from the same extension allowlists the drop path classifies against, so the
                // dialog cannot offer a file that staging would then turn away.
                Filter = BuildAttachmentFilter(),
            };

            if (dialog.ShowDialog() != true) return;

            List<string> rejected = [];
            foreach (string path in dialog.FileNames)
            {
                if (!await ImportAttachmentAsync(path))
                    rejected.Add(Path.GetFileName(path));
            }

            if (rejected.Count > 0)
            {
                _vm.AddSystemNotice(
                    rejected.Count == 1
                        ? $"Couldn't attach {rejected[0]} - only images, PDFs, and text files can be sent."
                        : $"Couldn't attach {rejected.Count} files ({string.Join(", ", rejected)}) - only images, PDFs, and text files can be sent.",
                    isError: true);
            }
        }

        /// <summary>
        /// The dialog's own filter, derived from the classifier the staging path uses, so the two
        /// can never drift apart. "All supported files" leads because that is the common case.
        /// </summary>
        private static string BuildAttachmentFilter()
        {
            string images = string.Join(";", s_imageExtensions.OrderBy(x => x).Select(x => "*." + x));
            string text = string.Join(";", s_textExtensions.OrderBy(x => x).Select(x => "*." + x));
            return $"All supported files|{images};*.pdf;{text}"
                 + $"|Images|{images}"
                 + "|PDF|*.pdf"
                 + $"|Text and code|{text}"
                 + "|All files|*.*";
        }

        /// <summary>
        /// FEAT-6, "Add context". Baseline's own entry does exactly this - its webview inserts the
        /// literal "@" and lets the mention picker take over - so this inserts "@" into the input
        /// and lets ours do the same, rather than building a second, parallel file browser.
        /// </summary>
        private void OnAddContextClicked(object sender, RoutedEventArgs e)
        {
            AddMenuPopup.IsOpen = false;
            InsertAtCaret("@", trailingSpace: false);

            // The picker is driven by OnInputTextChanged, which only fires for real edits; a
            // programmatic insert has to ask for it. Focus first so the popup's own key handling
            // (Up/Down/Enter) has somewhere to arrive.
            Keyboard.Focus(InputBox);
            UpdateInputPickers();
        }

        private void OnBrowseTheWebClicked(object sender, RoutedEventArgs e)
        {
            WebQueryPanel.Visibility = Visibility.Visible;
            WebQueryBox.Focus();
        }

        private void OnWebQueryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            ApplyWebContext();
        }

        private void OnAddWebContextClicked(object sender, RoutedEventArgs e)
        {
            ApplyWebContext();
        }

        private void ApplyWebContext()
        {
            string? line = WebContextComposer.Compose(WebQueryBox.Text);
            if (line == null)
            {
                // Nothing typed. Leaving the box open with the caret in it says "still waiting on
                // you" without an error for something that is not one.
                WebQueryBox.Focus();
                return;
            }

            AddMenuPopup.IsOpen = false;
            WebQueryPanel.Visibility = Visibility.Collapsed;
            WebQueryBox.Clear();

            InsertAtCaret(line, trailingSpace: true);
            Keyboard.Focus(InputBox);
        }

        /// <summary>
        /// Inserts text at the input caret, keeping the caret after it. Replaces any selection,
        /// which is what every other editor does with a paste-like insert.
        /// </summary>
        private void InsertAtCaret(string text, bool trailingSpace)
        {
            string insertion = trailingSpace ? text + " " : text;
            int start = InputBox.SelectionStart;
            int length = InputBox.SelectionLength;

            InputBox.Text = InputBox.Text.Substring(0, start)
                          + insertion
                          + InputBox.Text.Substring(start + length);
            InputBox.CaretIndex = start + insertion.Length;
        }

        // Scrolls to the owning message rather than the exact block - MessageList only generates
        // containers per ChatMessageViewModel (see MessageTemplateSelector), the tool-call card
        // itself is a nested ItemsControl item with no separately-generated container to target.
        private void OnJumpToRunningTaskClicked(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).Tag is not ToolCallViewModel call || call.OwnerMessage == null)
                return;

            if (MessageList.ItemContainerGenerator.ContainerFromItem(call.OwnerMessage) is FrameworkElement container)
                container.BringIntoView();
        }

        /// <summary>
        /// Resolves the file behind the active document tab.
        ///
        /// The shell's <c>SEID_DocumentFrame</c> is the authoritative answer and is deliberately
        /// tried first. Two things rule out the more obvious
        /// <see cref="VS.Documents.GetActiveDocumentViewAsync"/> as the primary source:
        ///
        /// 1. It only resolves tabs backed by a real text view. On a Markdown Preview tab it does
        ///    not return that tab's file - it returns whichever *text* document was last active,
        ///    or null if there is none. Live verification on 2026-08-28 found the "Active File"
        ///    chip inserting nothing at all in that case (backlog BUG-1); re-testing the first fix
        ///    on 2026-08-29 found the worse variant, where it silently inserted a *different*
        ///    open file than the one on screen.
        /// 2. <c>SEID_WindowFrame</c> would have the opposite problem - clicking the chip focuses
        ///    the Claude Code tool window, so the active *window* frame is often not a document at
        ///    all. <c>SEID_DocumentFrame</c> tracks the active document specifically and is
        ///    unaffected by tool-window focus.
        /// </summary>
        private static async Task<string?> TryResolveActiveFilePathAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (Package.GetGlobalService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection monitor &&
                    monitor.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out object frameObj) == VSConstants.S_OK &&
                    frameObj is IVsWindowFrame frame &&
                    frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object moniker) == VSConstants.S_OK &&
                    moniker is string monikerPath &&
                    !string.IsNullOrEmpty(monikerPath) &&
                    File.Exists(monikerPath))
                {
                    return monikerPath;
                }
            }
            catch (Exception)
            {
                // Fall through - a failure to resolve is not a failure to reference.
            }

            try
            {
                var docView = await VS.Documents.GetActiveDocumentViewAsync();
                if (!string.IsNullOrEmpty(docView?.FilePath))
                    return docView!.FilePath;
            }
            catch (Exception)
            {
            }

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // ActiveDocument throws rather than returning null when nothing is open.
                string? full = (Package.GetGlobalService(typeof(SDTE)) as EnvDTE80.DTE2)?.ActiveDocument?.FullName;
                if (!string.IsNullOrEmpty(full))
                    return full;
            }
            catch (Exception)
            {
            }

            return null;
        }

#pragma warning disable VSTHRD100
        private async void OnAddActiveFileClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            string? path = await TryResolveActiveFilePathAsync();
            if (string.IsNullOrEmpty(path))
            {
                _vm.AddSystemNotice("No active file to reference - open a document tab first.", isError: true);
                return;
            }

            InsertContextReference(path!, null, null);
        }

#pragma warning disable VSTHRD100
        private async void OnAddSelectionClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            string? activePath = await TryResolveActiveFilePathAsync();
            if (string.IsNullOrEmpty(activePath))
            {
                _vm.AddSystemNotice("No active file to reference - open a document tab first.", isError: true);
                return;
            }

            var docView = await VS.Documents.GetActiveDocumentViewAsync();
            var textView = docView?.TextView;

            // The text view is only usable when it actually belongs to the tab on screen. On a
            // Markdown Preview (or any non-text) tab it belongs to some *other* open document, and
            // reading a selection out of it would quote a file the user is not looking at.
            bool textViewMatchesActiveTab =
                textView != null &&
                !string.IsNullOrEmpty(docView?.FilePath) &&
                string.Equals(docView!.FilePath, activePath, StringComparison.OrdinalIgnoreCase);

            if (!textViewMatchesActiveTab)
            {
                // No selection to read, but the file itself is still referenceable - degrade to a
                // whole-file reference and say so, rather than doing nothing at all.
                _vm.AddSystemNotice("This tab has no text selection - referencing the whole file instead.", isError: false);
                InsertContextReference(activePath!, null, null);
                return;
            }

            string path = activePath!;
            ITextSelection selection = textView!.Selection;
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

                Uri baseUri = new(baseFull);
                Uri fullUri = new(Path.GetFullPath(fullPath));

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
            if (_suppressAutoScroll) return;

            if (e.ExtentHeightChange > 0)
            {
                bool wasAtBottom = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ExtentHeightChange - 1;
                if (wasAtBottom)
                    ChatScrollViewer.ScrollToEnd();
            }
        }

        private bool _suppressAutoScroll;

        /// <summary>
        /// A tool-call/thinking-block Expander toggle grows or shrinks its card, which the
        /// ScrollViewer reports as the same ExtentHeightChange as a brand-new message arriving -
        /// OnChatScrollChanged can't tell the two apart on its own, so this suppresses the
        /// resulting ScrollChanged(s) for exactly this local UI toggle rather than treating it as
        /// "new content arrived, snap to bottom."
        /// </summary>
#pragma warning disable VSTHRD001, VSTHRD110
        private void OnCardExpanderToggled(object sender, RoutedEventArgs e)
        {
            _suppressAutoScroll = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => _suppressAutoScroll = false));
        }
#pragma warning restore VSTHRD001, VSTHRD110
    }
}
