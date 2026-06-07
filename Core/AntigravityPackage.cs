using Antigravity_CLI_GUI.Commands;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Antigravity_CLI_GUI
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Antigravity", "Antigravity CLI wrapper", "1.0")]
    [ProvideToolWindow(typeof(AntigravityToolWindow))]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(GuidList.guidAntigravityPackageString)]
    public sealed class AntigravityPackage : AsyncPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            // Switch to UI thread for command registration
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Register the tool window command (your existing one)
            // No-op: tool window command is handled by AntigravityCommand

            // Register toolbar, Tools menu, and Solution Explorer commands
            await AntigravityCommand.InitializeAsync(this);
        }
    }
}
