using System;
using Xunit.Sdk;

namespace TeronClaudeCodeVS.Tests.Infrastructure
{
    /// <summary>
    /// Skips a test at run time, for facts whose subject may genuinely be absent from the machine.
    /// <para>
    /// Two of these tests depend on something outside the repository: the dictation round trip
    /// needs a Windows speech recognizer installed, and the checkpoint-store test needs a real CLI
    /// transcript left behind by an earlier live session. Neither absence is a defect, and neither
    /// should be quietly asserted away - a test that passes because its subject was missing is the
    /// vacuous pass rigor rule #6 exists to forbid. Skipping says so out loud in the run summary.
    /// </para>
    /// <para>
    /// xUnit v2 has no <c>Assert.Skip</c>, but it does support dynamic skipping: the runner treats
    /// an exception whose message carries the <c>$XunitDynamicSkip$</c> marker as a skip rather
    /// than a failure, and <c>SkipException.ForSkip</c> builds exactly that. This is what the
    /// Xunit.SkippableFact package wraps; going direct avoids a dependency for six lines.
    /// </para>
    /// </summary>
    internal static class Skip
    {
        public static void Because(string reason) => throw SkipException.ForSkip(reason);

        public static void If(bool condition, string reason)
        {
            if (condition)
                Because(reason);
        }

        public static void Unless(bool condition, string reason) => If(!condition, reason);
    }
}
