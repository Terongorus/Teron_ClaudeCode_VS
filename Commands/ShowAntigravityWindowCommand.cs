using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;

namespace Antigravity_CLI_GUI.Commands
{
    internal sealed class ShowAntigravityWindowCommand
    {
        public const int CommandId = 0x0100;
        public static readonly Guid CommandSet = new Guid("YOUR-GUID-HERE");
        private readonly AsyncPackage _package;

        private ShowAntigravityWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;

            var cmdId = new CommandID(CommandSet, CommandId);
            var cmd = new MenuCommand(Execute, cmdId);
            commandService.AddCommand(cmd);
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            _ = new ShowAntigravityWindowCommand(package, commandService??throw new ArgumentNullException(nameof(commandService)));
        }

        private void Execute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.ShowToolWindowAsync(
                    typeof(AntigravityToolWindow), 0, true, _package.DisposalToken);
            });
        }
    }
}
