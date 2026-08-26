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
                CommandID cmdId = new CommandID(GuidList.guidClaudeCodeCmdSet, (int)id);
                MenuCommand cmd = new MenuCommand(handler, cmdId);
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
            });
        }
    }
}
