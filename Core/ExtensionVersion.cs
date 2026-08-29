using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>
    /// UX-10: the extension's own version, for the palette footer and bug reports.
    /// <para>
    /// Read from the shipped <c>extension.vsixmanifest</c> rather than from the assembly, because
    /// those two numbers are not the same thing here: the manifest carries the real released
    /// version (what Visual Studio's Extensions Manager shows, and what a user would quote), while
    /// the assembly version is left at its 1.0.0 default. Quoting the assembly number in a bug
    /// report would name a version that does not exist.
    /// </para>
    /// </summary>
    public static class ExtensionVersion
    {
        private static string? _cached;

        /// <summary>e.g. "0.3.0", or "unknown" if the manifest cannot be read.</summary>
        public static string Current => _cached ??= Resolve();

        private static string Resolve()
        {
            try
            {
                string? dir = Path.GetDirectoryName(typeof(ExtensionVersion).Assembly.Location);
                if (string.IsNullOrEmpty(dir)) return FromAssembly();

                string manifest = Path.Combine(dir!, "extension.vsixmanifest");
                if (!File.Exists(manifest)) return FromAssembly();

                XDocument doc = XDocument.Load(manifest);
                XElement? identity = doc.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "Identity");

                string? version = identity?.Attribute("Version")?.Value;
                return string.IsNullOrWhiteSpace(version) ? FromAssembly() : version!;
            }
            catch
            {
                return FromAssembly();
            }
        }

        private static string FromAssembly()
        {
            try
            {
                Version? v = typeof(ExtensionVersion).Assembly.GetName().Version;
                return v?.ToString(3) ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
