using Community.VisualStudio.Toolkit;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Antigravity_CLI_GUI
{
    public class AntigravityProcessHost : IDisposable
    {
        private System.Diagnostics.Process? _process;

        public event EventHandler<string>? OutputReceived;
        public event EventHandler<string>? ErrorReceived;

        public async Task StartAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            DTE2? dte = await VS.GetServiceAsync<DTE, DTE2>();
            string? solutionPath = dte?.Solution?.FullName;

            string workingDir = !string.IsNullOrEmpty(solutionPath)
                ? Path.GetDirectoryName(solutionPath)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);


            string args =
                "-NoExit -Command \"& { " +
                "Import-Module \\\"$env:VSAPPIDDIR..\\Tools\\Microsoft.VisualStudio.DevShell.dll\\\"; " +
                "Enter-VsDevShell -SkipAutomaticLocation -SetDefaultWindowTitle -InstallPath $env:VSAPPIDDIR..\\..; " +
                "agy }\"";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            _process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    OutputReceived?.Invoke(this, e.Data);
            };
            _process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    ErrorReceived?.Invoke(this, e.Data);
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public Task SendAsync(string text)
        {
            if (_process == null || _process.HasExited) return Task.CompletedTask;
            return _process.StandardInput.WriteLineAsync(text);
        }

        public void Dispose()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.StandardInput.WriteLine("exit");
                    _process.Kill();
                }
            }
            catch { }
        }
    }
}
