using TeronClaudeCodeVS.Core;
using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace TeronClaudeCodeVS.Commands
{
    internal sealed class ClaudeCodeCommand
    {
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to UI thread before accessing command services
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (await package.GetServiceAsync(typeof(IMenuCommandService))
                is not OleMenuCommandService commandService)
                return;

            void Add(uint id, EventHandler handler)
            {
                CommandID cmdId = new(GuidList.guidClaudeCodeCmdSet, (int)id);
                MenuCommand cmd = new(handler, cmdId);
                commandService.AddCommand(cmd);
            }

            Add(PkgCmdIDList.cmdidClaudeCodeToolbar, (s, e) => ShowWindow(package));
            Add(PkgCmdIDList.cmdidClaudeCodeToolsMenu, (s, e) => ShowWindow(package));
            Add(PkgCmdIDList.cmdidClaudeCodeSolutionExplorer, (s, e) => ShowWindow(package));
            Add(PkgCmdIDList.cmdidClaudeCodeWindow, (s, e) => ShowWindow(package));
            Add(PkgCmdIDList.cmdidClaudeCodeCheckForUpdates, (s, e) => CheckForUpdates(package));
        }

        private static void CheckForUpdates(AsyncPackage package)
        {
            _ = package.JoinableTaskFactory.RunAsync(() => ExtensionUpdateCheck.CheckAsync(force: true));
        }

        private static void ShowWindow(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Observe the task explicitly
            _ = package.JoinableTaskFactory.RunAsync(async () =>
            {
                await package.ShowToolWindowAsync(
                    typeof(ClaudeCodeToolWindow),
                    0,
                    true,
                    package.DisposalToken);

                // ShowToolWindowAsync activates the pane frame, but that is not the same as WPF
                // keyboard focus landing on the input box - if the pane was already the visible,
                // foreground tab (the common case for this shortcut: pressed while working
                // elsewhere in the IDE), the control's own Loaded event never fires again, so its
                // OnLoaded-based refocus never runs either. Reach in explicitly instead.
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                if (package.FindToolWindow(typeof(ClaudeCodeToolWindow), 0, false) is ClaudeCodeToolWindow { Content: ClaudeCodeChatControl control })
                    control.FocusInput();
            });
        }
    }
}
