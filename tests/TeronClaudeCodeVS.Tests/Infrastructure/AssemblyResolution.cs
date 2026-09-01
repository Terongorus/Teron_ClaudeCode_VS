using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace TeronClaudeCodeVS.Tests.Infrastructure
{
    /// <summary>
    /// Lets the extension assembly load outside Visual Studio.
    /// <para>
    /// The extension references the VS SDK, whose assemblies are never copied to an output
    /// directory - inside VS they resolve from the running IDE, which is not here. Without this,
    /// the first test that touches a type whose signature mentions an SDK type fails with a
    /// <see cref="FileNotFoundException"/> that has nothing to do with the behaviour under test.
    /// </para>
    /// <para>
    /// The probe path is the VSSDK BuildTools package's own reference-assembly folder, which is the
    /// same trick <c>docs/comparison-audit/scripts/*-unit.ps1</c> has used since Phase E. It is a
    /// module initializer rather than a fixture because it has to be in place before xUnit reflects
    /// over this assembly's test classes, which is already too late for a constructor to help.
    /// </para>
    /// </summary>
    internal static class AssemblyResolution
    {
        private static readonly HashSet<string> s_resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int s_installed;

        [ModuleInitializer]
        internal static void Install()
        {
            if (System.Threading.Interlocked.Exchange(ref s_installed, 1) != 0)
                return;

            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly? OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string simpleName = args.Name.Split(',')[0];

            // Satellite assemblies are genuinely absent, not misplaced; probing for them only
            // produces recursive resolve storms.
            if (simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                return null;

            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }

            // A probe that itself triggers a resolve for the same name would recurse forever.
            lock (s_resolving)
            {
                if (!s_resolving.Add(simpleName))
                    return null;
            }

            try
            {
                foreach (string directory in ProbeDirectories)
                {
                    string candidate = Path.Combine(directory, simpleName + ".dll");
                    if (File.Exists(candidate))
                        return Assembly.LoadFrom(candidate);
                }

                return null;
            }
            finally
            {
                lock (s_resolving) { s_resolving.Remove(simpleName); }
            }
        }

        private static IEnumerable<string> ProbeDirectories
        {
            get
            {
                yield return AppDomain.CurrentDomain.BaseDirectory;

                string packageRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages", "microsoft.vssdk.buildtools");

                if (!Directory.Exists(packageRoot))
                    yield break;

                // Newest first: the extension builds against the newest BuildTools it has.
                foreach (string version in Directory.GetDirectories(packageRoot).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string lib = Path.Combine(version, @"tools\vssdk\bin\lib");
                    if (Directory.Exists(lib))
                        yield return lib;
                }
            }
        }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// .NET Framework has no <c>ModuleInitializerAttribute</c>, but the C# compiler only requires
    /// that a type with this exact name and namespace exists - so declaring it here is enough to
    /// get a real module initializer on net481.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }
}
