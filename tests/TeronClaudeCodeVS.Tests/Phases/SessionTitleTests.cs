using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using TeronClaudeCodeVS.Tests.Infrastructure;
using TeronClaudeCodeVS.ViewModels;
using Xunit;

namespace TeronClaudeCodeVS.Tests.Phases
{
    /// <summary>
    /// Phase F (FEAT-3), ported from <c>comparison-audit/scripts/phase-f-unit.ps1</c>.
    /// <para>
    /// FEAT-3 is a read of somebody else's file format, so the verification that is worth anything
    /// runs against REAL transcripts in the shapes that actually occur - not against fixtures
    /// written to match my own reading of the format. Those transcripts are live user data rather
    /// than committed files, so each of those tests skips out loud if its transcript has since been
    /// deleted; a missing fixture must never look like a pass.
    /// </para>
    /// <para>
    /// The precedence and revision cases are chosen because a wrong-but-plausible rule ("last
    /// record wins", "first title wins") gives a DIFFERENT answer on them, and each asserts first
    /// that the two candidate answers genuinely differ - otherwise the test could not tell the
    /// rules apart and would pass either way.
    /// </para>
    /// </summary>
    public sealed class SessionTitleTests : IDisposable
    {
        private readonly ScratchFiles _files = new ScratchFiles();

        public void Dispose() => _files.Dispose();

        private const string ExtensionsCwd = @"d:\Projects\Visual Studio Projects\Teron_Extensions";

        private static string ProjectsRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @".claude\projects");

        // Real transcripts on this machine, each picked for the shape it exercises.
        private const string RevisedTranscript =
            @"d--Projects-Visual-Studio-Projects-Teron-Extensions\1bb4112b-f6a0-4156-8a3f-d540ac208f92.jsonl";
        private const string CustomTitleTranscript =
            @"d--Projects-Visual-Studio-Projects-Teron-Applications\7fa8d213-48bc-4c86-9dd7-6d7132719c69.jsonl";
        private const string SmallAiTranscript =
            @"C--Program-Files-Microsoft-Visual-Studio-18-Community-Common7-IDE\67e8b7cd-9d8a-4856-a7ba-4d53002e296d.jsonl";
        private const string NoTitleTranscript =
            @"D--Projects-Visual-Studio-Projects-Test-Project-Claude\61df1c7e-5b1c-4dd1-8974-ef4303b3bef2.jsonl";
        private const string HugeTranscript =
            @"d--Projects-Visual-Studio-Projects-Teron-Extensions\19440230-dcab-4414-b21a-13d2ac1669e8.jsonl";

        private static string RequireFixture(string relative)
        {
            string path = Path.Combine(ProjectsRoot, relative);
            Skip.Unless(File.Exists(path), $"Fixture transcript is gone: {relative}");
            return path;
        }

        /// <summary>
        /// Ground truth, computed independently of the code under test. Deliberately NOT the same
        /// algorithm: a full forward scan, no tail window, and it returns the last generated title
        /// and the last custom title SEPARATELY rather than applying a precedence rule. The
        /// precedence is then asserted in the tests, where a wrong rule shows up as a wrong answer
        /// instead of being baked into both sides of the comparison.
        /// </summary>
        private sealed class Truth
        {
            public string? Ai { get; private set; }
            public string? AiFirst { get; private set; }
            public string? Custom { get; private set; }
            public int AiCount { get; private set; }
            public int CustomCount { get; private set; }

            public static Truth Scan(string path)
            {
                var truth = new Truth();

                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length > 2048 || !line.StartsWith("{\"type\":\"", StringComparison.Ordinal))
                        continue;

                    if (line.StartsWith("{\"type\":\"ai-title\"", StringComparison.Ordinal))
                    {
                        string? title = Field(line, "aiTitle");
                        if (string.IsNullOrEmpty(title))
                            continue;

                        truth.AiFirst ??= title;
                        truth.Ai = title;
                        truth.AiCount++;
                    }
                    else if (line.StartsWith("{\"type\":\"custom-title\"", StringComparison.Ordinal))
                    {
                        string? title = Field(line, "customTitle");
                        if (string.IsNullOrEmpty(title))
                            continue;

                        truth.Custom = title;
                        truth.CustomCount++;
                    }
                }

                return truth;
            }

            private static string? Field(string line, string name)
            {
                try { return (string?)Newtonsoft.Json.Linq.JObject.Parse(line)[name]; }
                catch (JsonException) { return null; }
            }
        }

        // ─── Precedence, on transcripts where a wrong rule gives a different answer ─────────────

        [Fact]
        public void A_custom_title_beats_a_later_generated_one()
        {
            string path = RequireFixture(CustomTitleTranscript);
            Truth truth = Truth.Scan(path);

            // Without this the case cannot discriminate between the real rule and "last record wins".
            Assert.NotEqual(truth.Custom, truth.Ai);

            SessionTitleReader.Result? result = SessionTitleReader.ReadFile(path);

            Assert.NotNull(result);
            Assert.Equal(truth.Custom, result!.Title);
            Assert.True(result.IsCustom, "a user-assigned title must be reported as one");
        }

        [Fact]
        public void The_revised_generated_title_wins_over_the_first_one()
        {
            string path = RequireFixture(RevisedTranscript);
            Truth truth = Truth.Scan(path);

            Assert.NotEqual(truth.AiFirst, truth.Ai);

            SessionTitleReader.Result? result = SessionTitleReader.ReadFile(path);

            Assert.NotNull(result);
            Assert.Equal(truth.Ai, result!.Title);
            Assert.False(result.IsCustom, "a generated title must not be reported as user-assigned");
        }

        // ─── The small-file path, where no tail truncation happens ──────────────────────────────

        [Fact]
        public void A_whole_file_read_still_finds_the_title()
        {
            string path = RequireFixture(SmallAiTranscript);
            Assert.Equal(Truth.Scan(path).Ai, SessionTitleReader.ReadFile(path)?.Title);
        }

        [Fact]
        public void A_transcript_with_no_title_yields_null_not_a_guess()
        {
            string path = RequireFixture(NoTitleTranscript);
            Truth truth = Truth.Scan(path);

            // The fixture really must have no title records, or null proves nothing.
            Assert.Equal(0, truth.AiCount + truth.CustomCount);
            Assert.Null(SessionTitleReader.ReadFile(path));
        }

        [Fact]
        public void A_missing_file_yields_null_rather_than_throwing()
        {
            Assert.Null(SessionTitleReader.ReadFile(Path.Combine(Path.GetTempPath(), "no-such-transcript-4a1f.jsonl")));
        }

        // ─── The tail window, and the full-scan fallback behind it ──────────────────────────────

        [Fact]
        public void A_title_behind_the_tail_window_is_still_found()
        {
            // A title at the very START of a file larger than the 1 MB window: the tail read cannot
            // see it, so the only way this passes is if the fallback full scan runs.
            string path = WriteTranscript("title-before-the-window.jsonl", writer =>
            {
                writer.WriteLine(@"{""type"":""ai-title"",""aiTitle"":""Buried far from the end"",""sessionId"":""synthetic""}");
                WriteFiller(writer, 600);
            });

            Assert.True(new FileInfo(path).Length > 1024 * 1024, "the synthetic file must be past the 1 MB window");
            Assert.Equal("Buried far from the end", SessionTitleReader.ReadFile(path)?.Title);
        }

        [Fact]
        public void A_title_inside_the_tail_window_is_found_without_a_full_scan()
        {
            string path = WriteTranscript("title-inside-the-window.jsonl", writer =>
            {
                WriteFiller(writer, 600);
                writer.WriteLine(@"{""type"":""ai-title"",""aiTitle"":""Inside the window"",""sessionId"":""synthetic""}");
            });

            Assert.Equal("Inside the window", SessionTitleReader.ReadFile(path)?.Title);
        }

        [Fact]
        public void A_seek_landing_mid_character_does_not_break_the_read()
        {
            // Non-ASCII right before the end, so the 1 MB seek lands inside a multi-byte character.
            string path = WriteTranscript("multibyte-boundary.jsonl", writer =>
            {
                string wide = @"{""type"":""assistant"",""message"":{""content"":""" + new string('é', 2000) + @"""}}";
                for (int i = 0; i < 600; i++)
                    writer.WriteLine(wide);

                writer.WriteLine(@"{""type"":""custom-title"",""customTitle"":""Survived a split character"",""sessionId"":""synthetic""}");
            });

            Assert.Equal("Survived a split character", SessionTitleReader.ReadFile(path)?.Title);
        }

        // ─── Lines that look like titles but are not ────────────────────────────────────────────

        [Fact]
        public void Decoy_lines_are_not_mistaken_for_title_records()
        {
            string[] lines =
            {
                // Assistant text quoting the record shape - the exact false positive a substring
                // match makes. Long, because the length gate is part of what rejects it.
                @"{""type"":""assistant"",""message"":{""content"":""the CLI writes {\""type\"":\""ai-title\"",\""aiTitle\"":\""WRONG ANSWER\""} per turn, "
                    + string.Concat(Enumerable.Repeat("padding ", 400)) + @"""}}",
                @"{""type"":""ai-title"",""aiTitle"":""   "",""sessionId"":""synthetic""}",        // whitespace only
                @"{""type"":""ai-title"",""aiTitle"":""Real title  "",""sessionId"":""synthetic""}", // trailing space, trimmed
                @"{""type"":""ai-title"" BROKEN JSON",                                              // unparseable
                "",                                                                                 // blank
                @"{""type"":""custom-title"",""customTitle"":"""",""sessionId"":""synthetic""}",    // empty custom title
            };

            string path = Path.Combine(Path.GetTempPath(), "teron-phase-f-decoys-" + Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllLines(path, lines, new UTF8Encoding(false));

            try
            {
                SessionTitleReader.Result? result = SessionTitleReader.ReadFile(path);

                Assert.NotNull(result);
                Assert.NotEqual("WRONG ANSWER", result!.Title);
                Assert.False(result.IsCustom, "an empty custom-title must not win over a real generated one");
                Assert.Equal("Real title", result.Title);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void A_final_line_with_no_newline_is_still_read()
        {
            // The shape a transcript has while it is still being written.
            string path = _files.WriteText("no-trailing-newline.jsonl",
                @"{""type"":""ai-title"",""aiTitle"":""Last line unterminated"",""sessionId"":""synthetic""}");

            Assert.Equal("Last line unterminated", SessionTitleReader.ReadFile(path)?.Title);
        }

        // ─── Cost on the largest transcript here ────────────────────────────────────────────────

        [Fact]
        public void The_tail_window_beats_a_full_scan_on_a_very_large_transcript()
        {
            string path = RequireFixture(HugeTranscript);

            var readerTimer = Stopwatch.StartNew();
            SessionTitleReader.Result? result = SessionTitleReader.ReadFile(path);
            readerTimer.Stop();

            var scanTimer = Stopwatch.StartNew();
            Truth truth = Truth.Scan(path);
            scanTimer.Stop();

            Assert.Equal(truth.Custom ?? truth.Ai, result?.Title);
            Assert.True(readerTimer.ElapsedMilliseconds < scanTimer.ElapsedMilliseconds,
                $"reader took {readerTimer.ElapsedMilliseconds} ms vs a full scan's {scanTimer.ElapsedMilliseconds} ms - " +
                "the tail window is not doing its job");
        }

        // ─── The cwd-to-transcript mapping, not just a path ─────────────────────────────────────

        [Fact]
        public void A_cwd_plus_a_session_id_resolves_to_that_transcript()
        {
            string path = RequireFixture(RevisedTranscript);
            string sessionId = Path.GetFileNameWithoutExtension(path);

            Assert.Equal(Truth.Scan(path).Ai, SessionTitleReader.Read(ExtensionsCwd, sessionId)?.Title);
            Assert.Null(SessionTitleReader.Read(ExtensionsCwd, "00000000-0000-0000-0000-000000000000"));
            Assert.Null(SessionTitleReader.Read("", sessionId));
        }

        // ─── ComputeTitleUpdates: what the history list actually consumes ───────────────────────

        [Fact]
        public void A_stale_truncated_title_is_replaced_and_stamped()
        {
            string path = RequireFixture(RevisedTranscript);
            string sessionId = Path.GetFileNameWithoutExtension(path);
            string? realTitle = Truth.Scan(path).Ai;

            List<SessionHistoryStore.TitleUpdate> updates = Updates(
                Entry(sessionId, "Read the meta-procedure file and tell me…", hasUserTitle: false, stamp: ""));

            SessionHistoryStore.TitleUpdate update = Assert.Single(updates);
            Assert.Equal(realTitle, update.Title);
            Assert.Matches(@"^\d+:\d+$", update.Stamp);
        }

        [Fact]
        public void An_unchanged_transcript_is_not_read_again_but_a_stale_stamp_forces_a_reread()
        {
            string path = RequireFixture(RevisedTranscript);
            string sessionId = Path.GetFileNameWithoutExtension(path);

            string stamp = Assert.Single(Updates(Entry(sessionId, "anything", false, ""))).Stamp;

            Assert.Empty(Updates(Entry(sessionId, "anything", false, stamp)));

            // The paired positive: the same row with a stamp that no longer matches IS re-read, so
            // the empty result above means "nothing to do" rather than "the reader stopped working".
            Assert.Single(Updates(Entry(sessionId, "anything", false, "1:1")));
        }

        [Fact]
        public void A_row_the_user_renamed_is_left_alone()
        {
            string path = RequireFixture(RevisedTranscript);
            string sessionId = Path.GetFileNameWithoutExtension(path);

            Assert.Empty(Updates(Entry(sessionId, "A name I typed myself", hasUserTitle: true, stamp: "")));
        }

        [Fact]
        public void A_row_already_showing_the_current_title_is_stamped_but_not_retitled()
        {
            string path = RequireFixture(RevisedTranscript);
            string sessionId = Path.GetFileNameWithoutExtension(path);
            string? realTitle = Truth.Scan(path).Ai;

            SessionHistoryStore.TitleUpdate update = Assert.Single(
                Updates(Entry(sessionId, realTitle!, hasUserTitle: false, stamp: "")));

            Assert.Null(update.Title);
            Assert.Matches(@"^\d+:\d+$", update.Stamp);
        }

        [Fact]
        public void A_session_with_no_transcript_produces_no_update()
        {
            RequireFixture(RevisedTranscript);   // keeps this test honest about the environment

            Assert.Empty(Updates(Entry("00000000-0000-0000-0000-000000000000", "Untitled", false, "")));
        }

        [Fact]
        public void A_mixed_batch_returns_exactly_the_row_that_could_change()
        {
            string path = RequireFixture(RevisedTranscript);
            string sessionId = Path.GetFileNameWithoutExtension(path);

            List<SessionHistoryStore.TitleUpdate> updates = Updates(
                Entry(sessionId, "A name I typed myself", hasUserTitle: true, stamp: ""),
                Entry("00000000-0000-0000-0000-000000000000", "Untitled", false, ""),
                Entry(sessionId, "Read the meta-procedure file and tell me…", false, ""));

            Assert.Single(updates);
        }

        // ─── The sessions.json already on disk still deserializes ───────────────────────────────

        [Fact]
        public void A_history_file_written_before_Phase_F_loads_with_the_new_fields_defaulted()
        {
            // The two fields FEAT-3 added are additive, and a change that silently reset every row
            // to defaults would be the data-loss regression this project has shipped once before.
            //
            // The PowerShell original asserted this against the real sessions.json, which worked
            // only for as long as that file predated FEAT-3. It no longer does - the shipped
            // feature has since stamped most of its rows, which is the feature working - so the
            // same assertion there would now fail for the right reason and prove nothing. The
            // claim is pinned here instead, on a literal pre-Phase-F row that cannot drift.
            const string beforePhaseF = @"[
              {
                ""id"": ""8acec497-31e4-4bdb-98a2-c6863a8d9257"",
                ""title"": ""An older session"",
                ""lastUsed"": ""2026-08-20T20:06:34.3650126Z"",
                ""cwd"": ""d:\\Projects\\Visual Studio Projects\\Teron_Extensions""
              }
            ]";

            SessionHistoryEntry row = Assert.Single(
                JsonConvert.DeserializeObject<List<SessionHistoryEntry>>(beforePhaseF)!);

            Assert.Equal("8acec497-31e4-4bdb-98a2-c6863a8d9257", row.SessionId);
            Assert.Equal("An older session", row.Title);
            Assert.Equal(@"d:\Projects\Visual Studio Projects\Teron_Extensions", row.WorkingDirectory);
            Assert.False(row.HasUserTitle, "a row that predates the flag must not come back flagged");
            Assert.True(string.IsNullOrEmpty(row.TitleStamp), "a row that predates the stamp must come back unstamped, so it is read once");
        }

        [Fact]
        public void The_real_history_file_on_this_machine_still_round_trips()
        {
            // The companion to the test above, against live data: whatever state FEAT-3 has left
            // the file in, every row must still carry the two identifiers the history list is
            // useless without.
            string sessionsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"TeronClaudeCodeVS\sessions.json");

            Skip.Unless(File.Exists(sessionsPath), "No chat history has been written on this machine yet.");

            List<SessionHistoryEntry>? rows = JsonConvert.DeserializeObject<List<SessionHistoryEntry>>(
                File.ReadAllText(sessionsPath));

            Assert.NotNull(rows);
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(row.SessionId), "a row lost its session id"));
            Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(row.WorkingDirectory), "a row lost its working directory"));
        }

        // ─── helpers ────────────────────────────────────────────────────────────────────────────

        private static SessionHistoryEntry Entry(string sessionId, string title, bool hasUserTitle, string stamp)
            => new SessionHistoryEntry
            {
                SessionId = sessionId,
                WorkingDirectory = ExtensionsCwd,
                Title = title,
                HasUserTitle = hasUserTitle,
                TitleStamp = stamp,
            };

        private static List<SessionHistoryStore.TitleUpdate> Updates(params SessionHistoryEntry[] entries)
            => SessionHistoryStore.ComputeTitleUpdates(entries);

        private string WriteTranscript(string name, Action<StreamWriter> write)
        {
            string path = _files.WriteText(name, string.Empty);

            using (var writer = new StreamWriter(path, append: false, encoding: new UTF8Encoding(false)))
                write(writer);

            return path;
        }

        /// <summary>Content lines long enough to be rejected by the reader's length gate, as real ones are.</summary>
        private static void WriteFiller(StreamWriter writer, int lines)
        {
            string filler = @"{""type"":""assistant"",""message"":{""content"":""" + new string('x', 4000) + @"""}}";

            for (int i = 0; i < lines; i++)
                writer.WriteLine(filler);
        }
    }
}
