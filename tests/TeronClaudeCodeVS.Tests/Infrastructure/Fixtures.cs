using System;
using System.IO;
using System.Linq;

namespace TeronClaudeCodeVS.Tests.Infrastructure
{
    /// <summary>
    /// Locates the captured CLI output in <c>docs/comparison-audit/fixtures</c>. The csproj copies that
    /// folder next to the test assembly, so the tests read the same bytes the PowerShell harnesses
    /// read - see that folder's README for why several of them come in matched pairs.
    /// </summary>
    internal static class Fixtures
    {
        public static string Directory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures");

        public static string Path_(string name) => System.IO.Path.Combine(Directory, name);

        /// <summary>
        /// The extension project's own folder, found by walking up from the test assembly until the
        /// csproj appears. A couple of tests read shipped source - the XAML, for one - and a
        /// relative hop count would break the moment the output path changed.
        /// </summary>
        public static string ProjectRoot
        {
            get
            {
                var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

                while (directory != null && !File.Exists(System.IO.Path.Combine(directory.FullName, "TeronClaudeCodeVS.csproj")))
                    directory = directory.Parent;

                return directory?.FullName
                    ?? throw new DirectoryNotFoundException(
                        "Could not find TeronClaudeCodeVS.csproj above " + AppDomain.CurrentDomain.BaseDirectory);
            }
        }

        public static string ProjectFile(params string[] relativeParts)
            => System.IO.Path.Combine(new[] { ProjectRoot }.Concat(relativeParts).ToArray());

        public static string Read(string name)
        {
            string path = Path_(name);

            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fixture '{name}' is missing from {Directory}. It is copied from " +
                    "docs/comparison-audit/fixtures by the test csproj; a rebuild should restore it.", path);

            return File.ReadAllText(path);
        }
    }
}
