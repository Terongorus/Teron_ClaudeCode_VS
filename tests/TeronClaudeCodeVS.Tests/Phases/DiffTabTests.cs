using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using TeronClaudeCodeVS.Core;
using TeronClaudeCodeVS.Tests.Infrastructure;
using TeronClaudeCodeVS.ViewModels;
using Xunit;

namespace TeronClaudeCodeVS.Tests.Phases
{
    /// <summary>
    /// Phase E (FEAT-2), ported from <c>comparison-audit/scripts/phase-e-unit.ps1</c>.
    /// <para>
    /// The live script drives the two paths a user actually takes. These are the branches it cannot
    /// reach, because reaching them needs inputs a live session will not produce on demand: the CLI
    /// writes a backup for every edit, so the reverse-reconstruction fallback never runs; the model
    /// does not emit <c>replace_all</c> on request; and no temp directory is ever a day old during
    /// a test.
    /// </para>
    /// </summary>
    public sealed class DiffTabTests
    {
        private static readonly Type VsDiffTabType = typeof(VsDiffTab);

        /// <summary>
        /// Builds a tool-call input. Written as JSON on purpose - the PowerShell version learned
        /// the hard way that poking properties into a <see cref="JObject"/> through an adapter can
        /// silently produce an empty object, and several checks then "passed" while exercising
        /// nothing. Parsing a string has no adapter in the middle.
        /// </summary>
        private static JObject Input(string json) => JObject.Parse(json);

        private static string? ApplyForward(string tool, JObject? input, string before)
            => Reflect.StaticCall<string>(VsDiffTabType, "ApplyForward", tool, input, before);

        private static string? ReverseApply(string tool, JObject? input, string after)
            => Reflect.StaticCall<string>(VsDiffTabType, "ReverseApply", tool, input, after);

        private static string? OpenCore(string tool, JObject? input, bool alreadyApplied)
            => Reflect.StaticCall<string>(VsDiffTabType, "OpenCore", tool, input, alreadyApplied, "", null, null);

        // ─── ApplyForward: what the file becomes ────────────────────────────────────────────────

        [Fact]
        public void Edit_replaces_only_the_first_occurrence_by_default()
        {
            string? result = ApplyForward("Edit",
                Input(@"{ ""old_string"": ""ALPHA"", ""new_string"": ""BRAVO"" }"), "x ALPHA y ALPHA z");

            Assert.Equal("x BRAVO y ALPHA z", result);
        }

        [Fact]
        public void Replace_all_rewrites_every_occurrence()
        {
            string? result = ApplyForward("Edit",
                Input(@"{ ""old_string"": ""ALPHA"", ""new_string"": ""BRAVO"", ""replace_all"": true }"), "x ALPHA y ALPHA z");

            Assert.Equal("x BRAVO y BRAVO z", result);
        }

        [Fact]
        public void An_empty_old_string_is_the_CLIs_convention_for_creating_a_file()
        {
            string? result = ApplyForward("Edit",
                Input(@"{ ""old_string"": """", ""new_string"": ""brand new\nfile"" }"), "");

            Assert.Equal("brand new\nfile", result);
        }

        [Fact]
        public void Text_that_is_not_in_the_file_yields_no_comparison()
        {
            string? result = ApplyForward("Edit",
                Input(@"{ ""old_string"": ""NOT PRESENT"", ""new_string"": ""x"" }"), "unrelated contents");

            Assert.Null(result);
        }

        [Fact]
        public void Write_replaces_the_entire_file()
        {
            Assert.Equal("whole new body",
                ApplyForward("Write", Input(@"{ ""content"": ""whole new body"" }"), "anything at all"));
        }

        [Fact]
        public void Write_with_no_content_is_an_empty_file_not_a_failure()
        {
            Assert.Equal("", ApplyForward("Write", Input("{}"), "anything at all"));
        }

        // ─── ReverseApply: what the file WAS - the fallback the live run never reached ──────────

        [Fact]
        public void Reverse_undoes_a_single_replacement()
        {
            Assert.Equal("x ALPHA y ALPHA z",
                ReverseApply("Edit", Input(@"{ ""old_string"": ""ALPHA"", ""new_string"": ""BRAVO"" }"), "x BRAVO y ALPHA z"));
        }

        [Fact]
        public void Reverse_undoes_a_replace_all()
        {
            Assert.Equal("x ALPHA y ALPHA z",
                ReverseApply("Edit",
                    Input(@"{ ""old_string"": ""ALPHA"", ""new_string"": ""BRAVO"", ""replace_all"": true }"), "x BRAVO y BRAVO z"));
        }

        [Fact]
        public void A_write_cannot_be_undone_from_the_call_alone()
        {
            Assert.Null(ReverseApply("Write", Input(@"{ ""content"": ""new body"" }"), "new body"));
        }

        [Fact]
        public void Reverse_refuses_to_guess_when_the_file_has_moved_on()
        {
            Assert.Null(ReverseApply("Edit",
                Input(@"{ ""old_string"": ""ALPHA"", ""new_string"": ""BRAVO"" }"), "the file has since changed"));
        }

        [Fact]
        public void A_pure_deletion_cannot_be_located_and_is_refused()
        {
            Assert.Null(ReverseApply("Edit", Input(@"{ ""old_string"": ""ALPHA"", ""new_string"": """" }"), "anything"));
        }

        // ─── Which tools are offered a tab at all ───────────────────────────────────────────────
        // OpenCore returns its refusal before touching any VS service, so this needs no IDE.

        [Fact]
        public void NotebookEdit_is_refused_with_a_reason_not_silently_ignored()
        {
            string? reason = OpenCore("NotebookEdit", Input(@"{ ""notebook_path"": ""x.ipynb"" }"), false);

            Assert.NotNull(reason);
            Assert.Contains("Edit and Write", reason);
        }

        [Fact]
        public void A_non_file_tool_is_refused()
        {
            string? reason = OpenCore("Bash", Input(@"{ ""command"": ""ls"" }"), false);

            Assert.NotNull(reason);
            Assert.Contains("Edit and Write", reason);
        }

        [Fact]
        public void An_edit_with_no_file_path_is_refused_with_its_own_reason()
        {
            string? reason = OpenCore("Edit", Input(@"{ ""old_string"": ""a"", ""new_string"": ""b"" }"), false);

            Assert.NotNull(reason);
            Assert.Contains("doesn't name a file", reason);
        }

        [Fact]
        public void An_applied_edit_to_a_file_that_is_gone_says_so()
        {
            string missing = Path.Combine(Path.GetTempPath(), "phase-e-absent-" + Guid.NewGuid().ToString("N") + ".txt");
            var input = new JObject
            {
                ["file_path"] = missing,
                ["old_string"] = "a",
                ["new_string"] = "b",
            };

            string? reason = OpenCore("Edit", input, alreadyApplied: true);

            Assert.NotNull(reason);
            Assert.Contains("no longer on disk", reason);
        }

        // ─── SweepStaleTempDirs: cleanup nothing has ever been old enough to trigger ────────────

        [Fact]
        public void Stale_comparison_directories_are_swept_and_current_ones_are_not()
        {
            string root = Path.Combine(Path.GetTempPath(), "TeronClaudeCodeVS-difftab");
            string stale = Path.Combine(root, "unit-stale");
            string fresh = Path.Combine(root, "unit-fresh");

            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(fresh);

            string staleFile = Path.Combine(stale, "a.before.txt");
            File.WriteAllText(staleFile, "old");
            File.WriteAllText(Path.Combine(fresh, "a.before.txt"), "new");

            // Read-only is the whole reason this cleanup is ours rather than Visual Studio's, so
            // the stale file has to actually be one.
            File.SetAttributes(staleFile, FileAttributes.ReadOnly);
            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));

            try
            {
                Reflect.StaticCall(VsDiffTabType, "SweepStaleTempDirs");

                Assert.False(Directory.Exists(stale), "a stale comparison directory should have been removed");
                Assert.False(File.Exists(staleFile), "read-only files should not block that removal");
                Assert.True(Directory.Exists(fresh), "a current comparison directory should be left alone");
            }
            finally
            {
                if (File.Exists(staleFile))
                    File.SetAttributes(staleFile, FileAttributes.Normal);

                try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            }
        }

        // ─── SessionCheckpointStore against the transcripts real runs left behind ──────────────

        [Fact]
        public void The_checkpoint_store_reads_both_a_delta_backed_and_a_snapshot_only_history()
        {
            // Ground truth, read straight out of ~/.claude: the Edit was the first change to this
            // file, so its own delta holds the original; the Write came later, when the file was
            // already tracked and the CLI wrote no delta at all - its "before" exists only in the
            // turn snapshot. That second case is the one a delta-only reading got wrong, which is
            // why the test insists on finding a transcript containing both.
            const string workingDirectory = @"D:\Projects\Visual Studio Projects\Test_Project_Claude";
            string scratch = Path.Combine(workingDirectory, "phase-e-scratch.txt");
            string projectDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @".claude\projects\D--Projects-Visual-Studio-Projects-Test-Project-Claude");

            Skip.Unless(Directory.Exists(projectDir),
                $"No CLI transcripts at {projectDir} - this reads history left behind by an earlier live session.");

            (string sessionId, string editId, string writeId)? found = FindSessionWithBothTools(projectDir);

            Skip.Unless(found.HasValue,
                "No transcript in that folder contains both an Edit and a Write, which is what this test compares.");

            (string sessionId, string editId, string writeId) = found!.Value;

            string? beforeEdit = SessionCheckpointStore.TryReadContentBeforeEdit(workingDirectory, sessionId, editId, scratch);
            string? beforeWrite = SessionCheckpointStore.TryReadContentBeforeEdit(workingDirectory, sessionId, writeId, scratch);

            Assert.NotNull(beforeEdit);
            Assert.Contains("ALPHA", beforeEdit);
            Assert.DoesNotContain("BRAVO", beforeEdit);

            Assert.NotNull(beforeWrite);
            Assert.Contains("BRAVO", beforeWrite);
            Assert.DoesNotContain("CHARLIE", beforeWrite);

            // If these matched, one of the two reads was answering with the other's history.
            Assert.NotEqual(beforeEdit, beforeWrite);

            Assert.Null(SessionCheckpointStore.TryReadContentBeforeEdit(
                workingDirectory, sessionId, "toolu_does_not_exist", scratch));

            Assert.Null(SessionCheckpointStore.TryReadContentBeforeEdit(
                workingDirectory, sessionId, editId, Path.Combine(workingDirectory, "Class1.cs")));
        }

        [Fact]
        public void A_missing_transcript_answers_nothing()
        {
            Assert.Null(SessionCheckpointStore.TryReadContentBeforeEdit(
                @"D:\Projects\Visual Studio Projects\Test_Project_Claude",
                "not-a-real-session-id",
                "toolu_x",
                "whatever.txt"));
        }

        private static (string sessionId, string editId, string writeId)? FindSessionWithBothTools(string projectDir)
        {
            IEnumerable<FileInfo> transcripts = new DirectoryInfo(projectDir)
                .GetFiles("*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc);

            foreach (FileInfo transcript in transcripts)
            {
                string? editId = null;
                string? writeId = null;

                foreach (string line in File.ReadLines(transcript.FullName))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    JObject record;
                    try { record = JObject.Parse(line); }
                    catch (Newtonsoft.Json.JsonException) { continue; }

                    if ((string?)record["type"] != "assistant")
                        continue;

                    foreach (JToken block in record["message"]?["content"] as JArray ?? new JArray())
                    {
                        if ((string?)block["type"] != "tool_use")
                            continue;

                        string? name = (string?)block["name"];
                        if (name == "Edit") editId ??= (string?)block["id"];
                        if (name == "Write") writeId ??= (string?)block["id"];
                    }
                }

                if (editId != null && writeId != null)
                    return (Path.GetFileNameWithoutExtension(transcript.Name), editId, writeId);
            }

            return null;
        }
    }
}
