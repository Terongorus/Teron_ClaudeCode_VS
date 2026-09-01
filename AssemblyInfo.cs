using System.Runtime.CompilerServices;

// Phase K: the headless verification suite moved from PowerShell reflection into real xUnit tests.
// Those tests assert on genuinely internal seams - VoiceInput, AgentSessionsViewModel.Parse,
// IsSameFolder, NormalizeCloudId and the rest - which the PowerShell harnesses reached through
// BindingFlags.NonPublic. Granting the test assembly access keeps those seams internal (they are
// not extension API and should not become public just to be testable) while letting the tests call
// them as ordinary code.
//
// The assembly is unsigned, so no public key is required here.
[assembly: InternalsVisibleTo("TeronClaudeCodeVS.Tests")]
