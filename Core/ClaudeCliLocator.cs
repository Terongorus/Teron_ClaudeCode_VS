using System;
using System.IO;
using System.Linq;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>
    /// Locates the Claude Code CLI executable on the current machine.
    /// </summary>
    internal static class ClaudeCliLocator
    {
        /// <summary>
        /// Resolves a path to `claude`/`claude.exe`/`claude.cmd`.
        /// Order: explicit override (Options page) -> PATH -> ~/.claude/local/claude.exe ->
        /// bundled copy from the official VS Code extension (highest version) -> null.
        /// </summary>
        public static string? Find(string? overridePath)
        {
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                if (File.Exists(overridePath))
                    return overridePath;

                string? fromDir = TryResolveInDirectory(overridePath!);
                if (fromDir != null)
                    return fromDir;
            }

            string? fromPath = TryResolveOnPath();
            if (fromPath != null)
                return fromPath;

            string localInstall = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "local", "claude.exe");
            if (File.Exists(localInstall))
                return localInstall;

            string? bundled = TryResolveBundledWithVsCode();
            if (bundled != null)
                return bundled;

            return null;
        }

        private static string? TryResolveInDirectory(string dir)
        {
            if (!Directory.Exists(dir))
                return null;

            foreach (var name in new[] { "claude.exe", "claude.cmd", "claude" })
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static string? TryResolveOnPath()
        {
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar))
                return null;

            var names = new[] { "claude.exe", "claude.cmd", "claude" };

            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;

                string trimmed = dir.Trim().Trim('"');
                foreach (var name in names)
                {
                    try
                    {
                        string candidate = Path.Combine(trimmed, name);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch (ArgumentException)
                    {
                        // Malformed PATH entry - skip it.
                    }
                }
            }

            return null;
        }

        private static string? TryResolveBundledWithVsCode()
        {
            string extensionsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vscode", "extensions");

            if (!Directory.Exists(extensionsDir))
                return null;

            var candidates = Directory.GetDirectories(extensionsDir, "anthropic.claude-code-*")
                .Select(dir => new
                {
                    Dir = dir,
                    Version = ParseVersionSuffix(Path.GetFileName(dir))
                })
                .Where(c => c.Version != null)
                .OrderByDescending(c => c.Version)
                .ToList();

            foreach (var candidate in candidates)
            {
                string exe = Path.Combine(candidate.Dir, "resources", "native-binary", "claude.exe");
                if (File.Exists(exe))
                    return exe;
            }

            return null;
        }

        private static Version? ParseVersionSuffix(string folderName)
        {
            const string prefix = "anthropic.claude-code-";
            if (!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;

            string versionPart = folderName.Substring(prefix.Length);
            // Folder names can carry an architecture suffix, e.g. "2.1.177-win32-x64".
            int dashIndex = versionPart.IndexOf('-');
            if (dashIndex >= 0)
                versionPart = versionPart.Substring(0, dashIndex);

            return Version.TryParse(versionPart, out var version) ? version : null;
        }
    }
}
