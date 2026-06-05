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

        public AntigravityToolWindowControl()
        {
            InitializeComponent();
            DataContext = _vm;

            Loaded += OnLoaded;
            SendButton.Click += OnSendClicked;
            ModelSelector.SelectionChanged += OnModelChanged;
        }

#pragma warning disable VSTHRD100
        private async void OnLoaded(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            _process = new AntigravityProcessHost();
            await _process.StartAsync();
            _process.OutputReceived += OnProcessOutput;

            // Set initial model on startup
            await _process.SendAsync($"/model {_vm.SelectedModel}{Environment.NewLine}");
        }

#pragma warning disable VSTHRD100
        private async void OnSendClicked(object sender, RoutedEventArgs e)
#pragma warning restore VSTHRD100
        {
            string text = ChatInput.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;

            _vm.AddUserMessage(text);
            ChatInput.Clear();

            // Send ONLY the prompt — model is already set via /model
            await _process!.SendAsync(text + Environment.NewLine);
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

                default:
                    RouteTerminalMessage(msg);
                    break;
            }
        }

        private void OnMarkdownLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is MarkdownViewer md && md.DataContext is ChatMessage msg)
                md.SetMarkdown(msg.Text);
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
    }
}
