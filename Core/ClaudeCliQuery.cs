using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>
    /// The outcome of one non-interactive `claude &lt;subcommand&gt;` run.
    ///
    /// Deliberately distinguishes "the command ran and printed nothing" from "the command never
    /// ran". The panels built on this (FEAT-4, FEAT-5) render an *empty state* for the first and an
    /// *error* for the second, and the previous helper - AccountUsageViewModel's private
    /// RunCommandAsync - could not tell them apart because every failure path returned "".
    /// </summary>
    internal sealed class ClaudeCliResult
    {
        public ClaudeCliResult(string stdOut, string stdErr, int? exitCode, bool started, bool timedOut, string? failureReason)
        {
            StdOut = stdOut;
            StdErr = stdErr;
            ExitCode = exitCode;
            Started = started;
            TimedOut = timedOut;
            FailureReason = failureReason;
        }

        public string StdOut { get; }
        public string StdErr { get; }

        /// <summary>Null when the process never exited within the timeout, or never started.</summary>
        public int? ExitCode { get; }

        public bool Started { get; }
        public bool TimedOut { get; }

        /// <summary>Human-readable reason the run is unusable, or null when it is usable.</summary>
        public string? FailureReason { get; }

        public bool Succeeded => Started && !TimedOut && ExitCode == 0;

        /// <summary>
        /// A message fit to show in a panel's error slot, or null when the run succeeded.
        /// Prefers the CLI's own stderr over our wording - it is the CLI that knows what went wrong.
        /// </summary>
        public string? ErrorMessage
        {
            get
            {
                if (Succeeded) return null;
                if (FailureReason != null) return FailureReason;

                string stderr = ClaudeCliQuery.StripAnsi(StdErr).Trim();
                if (stderr.Length > 0) return stderr;

                return $"`claude` exited with code {ExitCode?.ToString() ?? "?"}.";
            }
        }

        public static ClaudeCliResult Failure(string reason) =>
            new ClaudeCliResult("", "", null, started: false, timedOut: false, failureReason: reason);
    }

    /// <summary>
    /// Runs a Claude CLI subcommand headlessly and captures its output.
    ///
    /// Shared by every panel that surfaces CLI state rather than session state: Account &amp; Usage
    /// (`auth status`), the MCP panel (`mcp list`) and the plugins panel (`plugin list`,
    /// `plugin marketplace list`).
    ///
    /// Three things here are not optional, each learned the hard way:
    ///   * <b>Working directory.</b> `claude mcp list` reports project-scoped servers out of the
    ///     `.mcp.json` beside the *current directory*. Run it from the extension host's own cwd -
    ///     which is where devenv.exe happens to be - and a solution's own servers silently vanish.
    ///   * <b>Both pipes drained concurrently.</b> Reading stdout to the end while stderr fills its
    ///     buffer deadlocks the child. Two ReadToEndAsync tasks awaited together, never one.
    ///   * <b>UTF-8 on both pipes.</b> `mcp list` statuses are glyphs - ✓ ⏸ ✗ ⊘ - and the console
    ///     default codepage turns them into mojibake that no parser should have to guess at.
    /// </summary>
    internal static class ClaudeCliQuery
    {
        /// <summary>CSI/OSC escape sequences, in case the CLI ever decides this is a colour terminal.</summary>
        private static readonly Regex AnsiPattern =
            new Regex(@"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled);

        public static string StripAnsi(string? text) =>
            string.IsNullOrEmpty(text) ? "" : AnsiPattern.Replace(text!, "");

        public static async Task<ClaudeCliResult> RunAsync(
            string? claudePath,
            string arguments,
            string? workingDirectory = null,
            int timeoutMs = 15000)
        {
            if (string.IsNullOrWhiteSpace(claudePath))
                return ClaudeCliResult.Failure("The Claude Code CLI was not found. Set its path in Tools ▸ Options ▸ Claude Code.");

            string fileName = claudePath!;
            string fullArgs = arguments;

            // A .cmd/.bat shim is not an executable image; it needs an interpreter.
            string ext = Path.GetExtension(claudePath);
            if (string.Equals(ext, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                fileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                fullArgs = $"/c \"{claudePath}\" {arguments}";
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = fullArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                psi.WorkingDirectory = workingDirectory;

            Process? process = null;
            try
            {
                process = Process.Start(psi);
                if (process == null)
                    return ClaudeCliResult.Failure("Could not start the Claude Code CLI.");

                Task<string> outTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errTask = process.StandardError.ReadToEndAsync();

                Task both = Task.WhenAll(outTask, errTask);
                Task finished = await Task.WhenAny(both, Task.Delay(timeoutMs)).ConfigureAwait(false);

                if (finished != both)
                {
                    TryKill(process);
                    return new ClaudeCliResult("", "", null, started: true, timedOut: true,
                        failureReason: $"`claude {FirstWords(arguments)}` did not finish within {DescribeBudget(timeoutMs)}.");
                }

                // Both pipes are closed, so the child is at most milliseconds from exiting.
                if (!process.WaitForExit(2000))
                {
                    TryKill(process);
                    return new ClaudeCliResult(await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false),
                        null, started: true, timedOut: true,
                        failureReason: $"`claude {FirstWords(arguments)}` did not exit after printing its output.");
                }

                return new ClaudeCliResult(
                    StripAnsi(await outTask.ConfigureAwait(false)),
                    StripAnsi(await errTask.ConfigureAwait(false)),
                    process.ExitCode, started: true, timedOut: false, failureReason: null);
            }
            catch (Exception ex)
            {
                return ClaudeCliResult.Failure(ex.Message);
            }
            finally
            {
                process?.Dispose();
            }
        }

        /// <summary>"30000" -> "30s", "1" -> "1ms". Whole seconds read better than milliseconds,
        /// but a sub-second budget rendered as "0s" reads as a bug in the message.</summary>
        private static string DescribeBudget(int timeoutMs) =>
            timeoutMs >= 1000 ? $"{timeoutMs / 1000}s" : $"{timeoutMs}ms";

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch
            {
                // Already gone, or we are not allowed to - either way there is nothing to do.
            }
        }

        /// <summary>"plugin marketplace list --json" -> "plugin marketplace list", for messages.</summary>
        private static string FirstWords(string arguments)
        {
            var words = arguments.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new StringBuilder();
            foreach (string word in words)
            {
                if (word.StartsWith("-", StringComparison.Ordinal)) break;
                if (kept.Length > 0) kept.Append(' ');
                kept.Append(word);
            }
            return kept.Length > 0 ? kept.ToString() : arguments.Trim();
        }
    }
}
