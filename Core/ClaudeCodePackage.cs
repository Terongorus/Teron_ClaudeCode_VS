using TeronClaudeCodeVS.Commands;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TeronClaudeCodeVS.Core
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Claude Code for Visual Studio", "Chat with Claude Code without leaving the editor.", "1.0")]
    [ProvideToolWindow(typeof(ClaudeCodeToolWindow))]
    [ProvideOptionPage(typeof(ClaudeCodeOptionsPage), "Claude Code", "General", 0, 0, true)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(GuidList.guidClaudeCodePackageString)]
    public sealed class ClaudeCodePackage : AsyncPackage
    {
        /// <summary>Per-VS-instance singleton, used by the tool window to reach package services (e.g. the Options page).</summary>
        internal static ClaudeCodePackage? Instance { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            Instance = this;

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            await ClaudeCodeCommand.InitializeAsync(this);

            // Fire-and-forget: never block VS startup on a network call (RESUPPLY).
            _ = JoinableTaskFactory.RunAsync(() => ExtensionUpdateCheck.CheckAsync());
        }

        internal ClaudeCodeOptionsPage GetOptions() => (ClaudeCodeOptionsPage)GetDialogPage(typeof(ClaudeCodeOptionsPage));

        internal void ShowOptions() => ShowOptionPage(typeof(ClaudeCodeOptionsPage));
    }
}
