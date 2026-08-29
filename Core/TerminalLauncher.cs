using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>
    /// GAP-2: "Open Claude in Terminal" - launches the resolved CLI interactively, in the
    /// solution's directory, optionally with an initial slash command already typed.
    ///
    /// DELIBERATE DIVERGENCE FROM BASELINE, documented rather than papered over. The official
    /// VS Code extension calls `vscode.window.createTerminal(...)` and gets a real terminal
    /// docked inside the IDE. Visual Studio's own Terminal tool window has no equivalent public
    /// API - it is not exposed on DTE, there is no VS SDK service for creating a terminal or
    /// sending text to one, and `View.Terminal` only opens the window (the shell it starts and
    /// what is typed into it are not scriptable). So this opens an *external* terminal instead:
    /// Windows Terminal when it is installed, and a console host otherwise. The user gets a real
    /// CLI session in the right directory either way; it just does not live in the IDE frame.
    /// </summary>
    internal static class TerminalLauncher
    {
        /// <summary>
        /// Starts an interactive `claude` session. <paramref name="initialPrompt"/> is passed as
        /// the CLI's positional prompt argument (`claude [options] [prompt]`), which is how
        /// baseline hands off a slash command such as `/memory`.
        /// </summary>
        /// <returns>null on success, or a human-readable reason it could not be launched.</returns>
        public static string? OpenClaude(string claudePath, string workingDirectory, string? initialPrompt)
        {
            if (string.IsNullOrWhiteSpace(claudePath) || !File.Exists(claudePath))
                return "The Claude Code CLI could not be located, so there is nothing to open.";

            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(initialPrompt))
                args.Add(initialPrompt!);

            string? wt = FindWindowsTerminal();
            try
            {
                if (wt != null)
                {
                    // `wt -d <dir> <command> <args...>`. -d must come before the command, and wt
                    // treats a bare `;` as a pane separator, so anything containing one has to be
                    // escaped - Quote() handles the quoting, and no slash command we pass has one.
                    var wtArgs = new List<string> { "-d", workingDirectory, claudePath };
                    wtArgs.AddRange(args);
                    Start(wt, wtArgs, workingDirectory, useShell: false);
                }
                else
                {
                    // cmd /k keeps the console open after claude exits, so an error message from
                    // the CLI is still readable instead of vanishing with the window.
                    string comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                    var cmdArgs = new List<string> { "/k", claudePath };
                    cmdArgs.AddRange(args);
                    Start(comSpec, cmdArgs, workingDirectory, useShell: true);
                }
            }
            catch (Exception ex)
            {
                return "Could not open a terminal: " + ex.Message;
            }

            return null;
        }

        private static void Start(string fileName, IReadOnlyList<string> args, string workingDirectory, bool useShell)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = BuildArguments(args),
                WorkingDirectory = workingDirectory,
                // A terminal is only useful if it gets its own window, which rules out the
                // redirected/hidden setup the headless session uses.
                UseShellExecute = useShell,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            };

            Process.Start(psi)?.Dispose();
        }

        /// <summary>Joins arguments with the quoting rules CommandLineToArgvW actually applies.</summary>
        private static string BuildArguments(IReadOnlyList<string> args)
        {
            var sb = new StringBuilder();
            foreach (string arg in args)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(Quote(arg));
            }
            return sb.ToString();
        }

        private static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                return arg;

            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    // Backslashes immediately before a quote are doubled, then the quote escaped.
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            // Trailing backslashes are doubled so they don't escape the closing quote.
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// Windows Terminal ships as an app-execution alias under LOCALAPPDATA\Microsoft\WindowsApps
        /// rather than a normal Program Files install, and that folder is on PATH for interactive
        /// shells but not always for a process launched by the VS extension host - so check the
        /// alias directly first rather than trusting PATH alone.
        /// </summary>
        private static string? FindWindowsTerminal()
        {
            string alias = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(alias))
                return alias;

            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar))
                return null;

            foreach (string dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim().Trim('"'), "wt.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry - skip it.
                }
            }

            return null;
        }
    }
}
