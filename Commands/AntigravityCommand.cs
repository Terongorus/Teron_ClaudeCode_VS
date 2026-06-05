using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace Antigravity_CLI_GUI.Commands
{
    internal sealed class AntigravityCommand
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
                var cmdId = new CommandID(GuidList.guidAntigravityCmdSet, (int)id);
                var cmd = new MenuCommand((s, e) => ShowWindow(package), cmdId);
                commandService.AddCommand(cmd);
            }

            Add(PkgCmdIDList.cmdidAntigravityToolbar);
            Add(PkgCmdIDList.cmdidAntigravityToolsMenu);
            Add(PkgCmdIDList.cmdidAntigravitySolutionExplorer);
            Add(PkgCmdIDList.cmdidAntigravityWindow);

        }

        private static void ShowWindow(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Observe the task explicitly
            _ = package.JoinableTaskFactory.RunAsync(async () =>
            {
                await package.ShowToolWindowAsync(
                    typeof(AntigravityToolWindow),
                    0,
                    true,
                    package.DisposalToken);
            });
        }
    }
}
