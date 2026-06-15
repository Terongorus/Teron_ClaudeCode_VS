using ClaudeCodeVS.Core;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCodeVS.Commands
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

            void Add(uint id)
            {
                var cmdId = new CommandID(GuidList.guidClaudeCodeCmdSet, (int)id);
                var cmd = new MenuCommand((s, e) => ShowWindow(package), cmdId);
                commandService.AddCommand(cmd);
            }

            Add(PkgCmdIDList.cmdidClaudeCodeToolbar);
            Add(PkgCmdIDList.cmdidClaudeCodeToolsMenu);
            Add(PkgCmdIDList.cmdidClaudeCodeSolutionExplorer);
            Add(PkgCmdIDList.cmdidClaudeCodeWindow);

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
