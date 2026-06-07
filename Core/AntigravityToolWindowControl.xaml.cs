using Antigravity_CLI_GUI.Core;
using Antigravity_CLI_GUI.Utilities;
using Microsoft.VisualStudio.Shell;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Antigravity_CLI_GUI
{
    public partial class AntigravityToolWindowControl : UserControl
    {
        private readonly ChatViewModel _vm = new();
        private AntigravityProcessHost? _process;
        private const string PlaceholderText = "Ask Antigravity...";

        public AntigravityToolWindowControl()
        {
            InitializeComponent();
            DataContext = _vm;

            Loaded += OnLoaded;
            SendButton.Click += OnSendClicked;
            NewChatButton.Click += OnNewChatClicked;
            OpenTerminalButton.Click += OnToggleTerminalClicked;
            ModelSelector.SelectionChanged += OnModelChanged;
        }

#pragma warning disable VSTHRD100
        private async void OnLoaded(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            // Set initial placeholder text
            if (string.IsNullOrWhiteSpace(ChatInput.Text))
            {
                ChatInput.Text = PlaceholderText;
                ChatInput.Opacity = 0.5;
            }

            await StartNewProcessAsync();
        }

#pragma warning disable VSTHRD100
        private async void OnSendClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            string text = ChatInput.Text.Trim();
            if (string.IsNullOrEmpty(text) || text == PlaceholderText) return;

            _vm.AddUserMessage(text);
            ChatInput.Clear();

            // Set focus back and restore placeholder if needed
            ChatInput.Focus();

            if (_process != null)
            {
                // Send ONLY the prompt — model is already set via /model
                await _process.SendAsync(text + Environment.NewLine);
            }
        }

#pragma warning disable VSTHRD100
        private async void OnNewChatClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            _vm.Messages.Clear();
            TerminalOutput.Clear();

            await StartNewProcessAsync();
        }

        private void OnToggleTerminalClicked(object sender, RoutedEventArgs e)
        {
            _vm.IsTerminalVisible = !_vm.IsTerminalVisible;
        }

#pragma warning disable VSTHRD100
        private async void OnModelChanged(object sender, SelectionChangedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (_process != null)
            {
                string cmd = $"/model {_vm.SelectedModel}{Environment.NewLine}";
                await _process.SendAsync(cmd);
            }
        }

        private async System.Threading.Tasks.Task StartNewProcessAsync()
        {
            if (_process != null)
            {
                try
                {
                    _process.Dispose();
                }
                catch { }
            }

            _process = new AntigravityProcessHost();
            await _process.StartAsync();
            _process.OutputReceived += OnProcessOutput;

            // Set initial model on startup
            await _process.SendAsync($"/model {_vm.SelectedModel}{Environment.NewLine}");
        }

        private void OnProcessOutput(object sender, string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            var msg = TerminalMessageParser.Parse(line);

            switch (msg.Type)
            {
                case TerminalMessageType.AssistantText:
                    RouteAssistantText(msg.Content);
                    break;

                case TerminalMessageType.System:
                case TerminalMessageType.Warning:
                case TerminalMessageType.Error:
                    RouteSystemMessage(msg);
                    break;

                default:
                    RouteTerminalMessage(msg);
                    break;
            }
        }

#pragma warning disable VSTHRD100
        private async void RouteAssistantText(string content)
#pragma warning restore VSTHRD100
        {
            if (string.IsNullOrWhiteSpace(content))
                return;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var last = _vm.Messages.LastOrDefault();

            if (last != null && last.Role == "assistant")
            {
                last.Pending += content + "\n";
                await TypingAnimator.AnimateAsync(last);
            }
            else
            {
                var msg = new ChatMessage
                {
                    Role = "assistant",
                    Text = "",
                    Pending = content + "\n"
                };

                _vm.Messages.Add(msg);
                await TypingAnimator.AnimateAsync(msg);
            }

            ChatHistory.ScrollToEnd();
        }

#pragma warning disable VSTHRD100
        private async void RouteSystemMessage(TerminalMessage message)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string role = message.Type switch
            {
                TerminalMessageType.Error => "error",
                TerminalMessageType.Warning => "warning",
                _ => "system"
            };

            bool isPermission = message.Content.Contains("(y/n)") || 
                                message.Content.Contains("Do you want to") || 
                                message.Content.Contains("Permission requested");

            var chatMsg = new ChatMessage
            {
                Role = role,
                Text = message.Content,
                IsPermissionRequest = isPermission,
                IsActionable = true
            };

            _vm.Messages.Add(chatMsg);
            ChatHistory.ScrollToEnd();

            // Also echo system messages to the raw terminal output
            RouteTerminalMessage(message);
        }

#pragma warning disable VSTHRD100
        private async void RouteTerminalMessage(TerminalMessage message)
#pragma warning restore VSTHRD100
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string prefix = message.Type switch
            {
                TerminalMessageType.Error => "[ERROR] ",
                TerminalMessageType.Warning => "[WARN] ",
                TerminalMessageType.System => "[SYS] ",
                TerminalMessageType.Json => "[JSON] ",
                TerminalMessageType.CodeBlock => "[CODE] ",
                _ => ""
            };

            TerminalOutput.AppendText(prefix + message.Content + Environment.NewLine);
            TerminalOutput.ScrollToEnd();
        }

#pragma warning disable VSTHRD100
        private async void OnApproveClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (sender is Button btn && btn.DataContext is ChatMessage msg)
            {
                msg.IsActionable = false;
                if (_process != null)
                {
                    await _process.SendAsync("y" + Environment.NewLine);
                }
            }
        }

#pragma warning disable VSTHRD100
        private async void OnDenyClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            if (sender is Button btn && btn.DataContext is ChatMessage msg)
            {
                msg.IsActionable = false;
                if (_process != null)
                {
                    await _process.SendAsync("n" + Environment.NewLine);
                }
            }
        }

        private void ChatInput_GotFocus(object sender, RoutedEventArgs e)
        {
            if (ChatInput.Text == PlaceholderText)
            {
                ChatInput.Text = "";
                ChatInput.Opacity = 1.0;
            }
        }

        private void ChatInput_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ChatInput.Text))
            {
                ChatInput.Text = PlaceholderText;
                ChatInput.Opacity = 0.5;
            }
        }
    }
}
