using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using TeronClaudeCodeVS.Core;
using TeronClaudeCodeVS.Tests.Infrastructure;
using TeronClaudeCodeVS.ViewModels;
using Xunit;

namespace TeronClaudeCodeVS.Tests.Phases
{
    /// <summary>
    /// Phase I (FEAT-1, rewind and fork), ported from <c>comparison-audit/scripts/phase-i-unit.ps1</c>.
    /// <para>
    /// Three things are worth covering headlessly:
    /// </para>
    /// <list type="bullet">
    /// <item>The transcript reader, against a REAL two-turn session captured from the real CLI (see
    /// <c>../fixtures/README.md</c>), chosen because it contains the two things that make a naive
    /// reader wrong: tool-result relays that are also <c>user</c> records, and a second edit to a
    /// file the CLI was already tracking.</item>
    /// <item>The fork's command line. <c>ClaudeCodeSession.Start</c> spawns the process itself, so
    /// the only way to read <c>--fork-session</c> / <c>--resume-session-at</c> is to let it spawn a
    /// real <c>claude.exe</c> and ask Windows what it was started with, in a scratch directory,
    /// sending nothing.</item>
    /// <item>Copy that came from baseline rather than from me, asserted verbatim.</item>
    /// </list>
    /// <para>Rigor rule #6 throughout: absence is always paired with a matching presence.</para>
    /// </summary>
    public sealed class RewindTests
    {
        // The fixture's own ids, read off the captured session at capture time.
        private const string UuidAlpha = "e24a5a14-28be-4a63-afd2-80cd84635bd0";    // turn 1's prompt
        private const string UuidBeta = "b199b493-da1a-4c1e-8fff-912295764b54";     // turn 2's prompt
        private const string UuidAnchor = "e6c53864-566c-43f2-b090-538ab1e4b9a6";   // turn 1's last assistant entry

        private static string Original => Fixtures.Path_("rewind-session-original.jsonl");
        private static string Forked => Fixtures.Path_("rewind-session-forked.jsonl");

        // ─── The rewind points read out of a real transcript ────────────────────────────────────

        [Fact]
        public void Two_rewind_points_come_back_one_per_real_prompt()
        {
            Assert.True(File.Exists(Original), "the captured original transcript is missing");

            List<RewindPoint> points = SessionCheckpointStore.ReadRewindPoints(Original, DateTime.UtcNow);
            Assert.Equal(2, points.Count);

            // CONTROL: the file really does hold more than two `user` records, so "two" is a
            // filter doing work and not an artefact of a short fixture.
            int userRecords = File.ReadLines(Original).Count(line => line.Contains("\"type\":\"user\""));
            Assert.True(userRecords > points.Count,
                $"{userRecords} user records vs {points.Count} prompts - the fixture may have changed");
        }

        [Fact]
        public void Points_are_ordered_newest_first_with_the_right_fork_anchors()
        {
            List<RewindPoint> points = SessionCheckpointStore.ReadRewindPoints(Original, DateTime.UtcNow);
            Assert.Equal(2, points.Count);

            Assert.Equal(UuidBeta, points[0].MessageUuid);
            Assert.Equal(UuidAlpha, points[1].MessageUuid);

            Assert.Equal(UuidAnchor, points[0].ResumeAtUuid);
            Assert.Null(points[1].ResumeAtUuid);
            Assert.True(points[1].IsFirstMessage, "the first prompt has no anchor, so forking there means a new session");
            Assert.False(points[0].IsFirstMessage, "CONTROL - the later prompt must not also claim to be the first");

            Assert.Equal(0, points[1].UserOrdinal);
            Assert.Equal(1, points[0].UserOrdinal);
        }

        [Fact]
        public void Prompt_text_is_what_was_typed_with_no_tool_result_relay_leaking_in()
        {
            List<RewindPoint> points = SessionCheckpointStore.ReadRewindPoints(Original, DateTime.UtcNow);

            Assert.StartsWith("Now change note.txt", points[0].PromptText);
            Assert.DoesNotContain(points, p => p.PromptText.Contains("tool_result"));
            Assert.DoesNotContain(points, p => string.IsNullOrEmpty(p.MessageUuid));
            Assert.DoesNotContain(points, p => p.TimestampUtc == DateTime.MinValue);
        }

        [Fact]
        public void A_rewind_point_announces_itself_as_the_prompt_not_its_type_name()
        {
            // A ListBoxItem with no AutomationProperties.Name falls back to ToString(). A live run
            // found the picker's rows announcing themselves as the CLR type name to anything
            // reading the accessibility tree, which is what a screen reader would have read out.
            RewindPoint point = SessionCheckpointStore.ReadRewindPoints(Original, DateTime.UtcNow)[0];

            Assert.Equal(point.PromptText, point.ToString());
            Assert.DoesNotContain("RewindPoint", point.ToString());
        }

        // ─── The fork the CLI actually produced, checked against the original ──────────────────

        [Fact]
        public void The_fork_keeps_the_chain_up_to_the_anchor_and_drops_what_followed()
        {
            // Not a claim about the flag - a measurement of what came back when it was used. Both
            // files are real CLI output; this asserts the relationship FEAT-1 depends on.
            List<string> originalChain = ChainUuids(Original);
            List<string> forkedChain = ChainUuids(Forked);

            int anchorAt = originalChain.IndexOf(UuidAnchor);
            Assert.True(anchorAt >= 0, "the anchor is not in the original chain");

            Assert.True(forkedChain.Count >= anchorAt + 1);
            Assert.Equal(originalChain.Take(anchorAt + 1), forkedChain.Take(anchorAt + 1));

            Assert.DoesNotContain(UuidBeta, forkedChain);

            // CONTROL: the original still has that turn, so nothing was rewritten in place.
            Assert.Contains(UuidBeta, originalChain);

            Assert.True(forkedChain.Count > anchorAt + 1, "the fork should continue past the anchor with its own turn");
        }

        private static List<string> ChainUuids(string path)
        {
            var uuids = new List<string>();

            foreach (string line in File.ReadLines(path))
            {
                Match typeMatch = Regex.Match(line, "\"type\":\"(user|assistant)\"");
                Match uuidMatch = Regex.Match(line, "\"uuid\":\"([0-9a-f-]{36})\"");

                if (typeMatch.Success && uuidMatch.Success)
                    uuids.Add(uuidMatch.Groups[1].Value);
            }

            return uuids;
        }

        // ─── Relative ages, in baseline's own wording ───────────────────────────────────────────

        [Theory]
        [InlineData(5, "just now")]
        [InlineData(59, "just now")]
        [InlineData(90, "1m ago")]
        [InlineData(7200, "2h ago")]
        [InlineData(259200, "3d ago")]
        [InlineData(3540, "59m ago")]   // the hour boundary is 60 minutes, not 59
        public void Relative_ages_match_baselines_wording(double secondsAgo, string expected)
        {
            DateTime now = DateTime.UtcNow;
            Assert.Equal(expected, RewindPoint.DescribeAge(now.AddSeconds(-secondsAgo), now));
        }

        // ─── The outcome wording, including the part that stops a count reading as data loss ────

        [Fact]
        public void The_outcome_wording_pluralises_correctly_and_spells_out_the_reason()
        {
            Assert.Equal("Code rewind successful", ChatSessionViewModel.DescribeRewindOutcome(0));

            string one = ChatSessionViewModel.DescribeRewindOutcome(1);
            string two = ChatSessionViewModel.DescribeRewindOutcome(2);

            Assert.Contains("1 file was skipped", one);
            Assert.Contains("2 files were skipped", two);
            Assert.Contains("a link or other non-regular file", one);
        }

        // ─── The fork flags on a REAL command line ──────────────────────────────────────────────

        [Fact]
        public void The_fork_flags_appear_on_a_real_spawned_command_line_only_when_they_should()
        {
            string claude = ClaudeCliLocator.Find(null)
                ?? throw new InvalidOperationException("ClaudeCliLocator found no CLI on this machine.");

            string scratch = Path.Combine(Path.GetTempPath(), "claude-phase-i-args-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);

            try
            {
                string? plain = CommandLineFor(claude, scratch, resumeId: null, fork: false, resumeAt: null);
                Assert.NotNull(plain);
                Assert.DoesNotContain("--fork-session", plain);
                Assert.DoesNotContain("--resume-session-at", plain);
                // CONTROL: the same read finds a flag that IS there, so "absent" is a result and
                // not a failure to read the command line at all.
                Assert.Contains("--permission-prompt-tool", plain);

                string? forked = CommandLineFor(claude, scratch, UuidAlpha, fork: true, resumeAt: UuidAnchor);
                Assert.NotNull(forked);
                Assert.Contains($"--resume {UuidAlpha}", forked);
                Assert.Contains("--fork-session", forked);
                Assert.Contains($"--resume-session-at {UuidAnchor}", forked);

                // Neither flag means anything without --resume, and the CLI ignores them there;
                // not emitting them keeps the command line honest about what the session is.
                string? noResume = CommandLineFor(claude, scratch, resumeId: null, fork: true, resumeAt: UuidAnchor);
                Assert.NotNull(noResume);
                Assert.DoesNotContain("--fork-session", noResume);
                Assert.DoesNotContain("--resume-session-at", noResume);
            }
            finally
            {
                try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
            }
        }

        private static string? CommandLineFor(string claude, string scratch, string? resumeId, bool fork, string? resumeAt)
        {
            using var session = new ClaudeCodeSession();

            session.Start(
                claude, scratch, "haiku", null,
                resumeSessionId: resumeId,
                forkSession: fork,
                resumeSessionAt: resumeAt);

            // Filtered by PARENT pid - the audit's standing rule, earned when a claude.exe matched
            // by name turned out to be the operator's own VS Code.
            string? commandLine = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);

            while (DateTime.UtcNow < deadline && commandLine == null)
            {
                commandLine = FindChildClaudeCommandLine(Process.GetCurrentProcess().Id);
                if (commandLine == null)
                    System.Threading.Thread.Sleep(400);
            }

            session.Dispose();
            System.Threading.Thread.Sleep(800);
            return commandLine;
        }

        private static string? FindChildClaudeCommandLine(int parentPid)
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ParentProcessId={parentPid} AND Name='claude.exe'");

            foreach (ManagementObject result in searcher.Get().Cast<ManagementObject>())
                return (string?)result["CommandLine"];

            return null;
        }

        // ─── The markup ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_ElementName_reference_in_the_rewind_markup_resolves()
        {
            string xaml = File.ReadAllText(Fixtures.ProjectFile("Core", "ClaudeCodeChatControl.xaml"));

            string[] declaredNames = Regex.Matches(xaml, "x:Name=\"([A-Za-z0-9_]+)\"")
                .Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            string[] references = Regex.Matches(xaml, "ElementName=([A-Za-z0-9_]+)")
                .Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToArray();

            foreach (string reference in references)
                Assert.Contains(reference, declaredNames);

            // CONTROL: the same test rejects a name that was never declared.
            Assert.DoesNotContain("RewindPopupp", declaredNames);

            foreach (string expected in new[] { "RewindPopup", "RewindConfirmPopup", "MessageActionsPopup", "RewindList" })
                Assert.Contains(expected, declaredNames);
        }

        [Fact]
        public void Baselines_copy_appears_verbatim()
        {
            // Paraphrasing any of these would be a silent divergence from the thing the audit
            // actually measured.
            string xaml = File.ReadAllText(Fixtures.ProjectFile("Core", "ClaudeCodeChatControl.xaml"));

            string[] verbatimCopy =
            {
                "Rewind to…",
                "Select a message to restore code and fork the conversation from that point.",
                "Fork conversation from here",
                "Rewind code to here",
                "Fork conversation and rewind code",
                "A new forked conversation will be created after rewinding.",
                "The code has not changed, so no code will be restored.",
                "Restore code and conversation to an earlier point",
            };

            foreach (string text in verbatimCopy)
                Assert.Contains(text, xaml);

            Assert.Contains("Rewinding does not affect files edited manually or via bash.", xaml);

            Assert.Matches(
                new Regex("x:Name=\"RewindConfirmPopup\"[^>]*(?s).{0,200}StaysOpen=\"True\""), xaml);
        }

        [Fact]
        public void The_rewind_empty_state_and_initial_visibility_come_from_the_view_model()
        {
            // The empty state is the view model's, not the markup's - the markup binds it. Asserted
            // where it actually lives, which is also the only place a typo in it could hide.
            using var vm = new ChatSessionViewModel();

            Assert.Equal("No messages to rewind to yet.", vm.RewindEmptyStateText);
            Assert.False(vm.IsRewindPickerVisible);
            Assert.Null(vm.SelectedRewindPoint);
            Assert.False(vm.HasSelectedRewindPoint);
            Assert.False(vm.IsRewindConfirmVisible);
            Assert.False(vm.CanConfirmRewind);
        }
    }
}
