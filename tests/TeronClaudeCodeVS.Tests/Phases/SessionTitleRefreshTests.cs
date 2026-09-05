using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using TeronClaudeCodeVS.Tests.Infrastructure;
using TeronClaudeCodeVS.ViewModels;
using Xunit;

namespace TeronClaudeCodeVS.Tests.Phases
{
    /// <summary>
    /// Phase F at the view-model level, ported from <c>comparison-audit/scripts/phase-f-vm.ps1</c>.
    /// <para>
    /// <see cref="SessionTitleTests"/> covers the reader and the store. What it cannot reach is the
    /// part that runs in the app: the refresh starts on a background thread and is applied back on
    /// the dispatcher, and the interesting case - the user renaming a row WHILE that read is in
    /// flight - is a race that reasoning alone does not settle. None of it needs an IDE. The view
    /// model constructs on any STA thread and the dispatcher is pumped by the test, which makes the
    /// race deterministic rather than merely likely: the apply cannot run until the test pumps.
    /// </para>
    /// <para>
    /// The store's path is redirected into a sandbox first, and the test asserts that redirect took
    /// effect and that the user's real history file was never touched. Everything else it reads -
    /// the transcripts - is read-only.
    /// </para>
    /// </summary>
    public sealed class SessionTitleRefreshTests
    {
        private const string Cwd = @"d:\Projects\Visual Studio Projects\Teron_Extensions";
        private const string FixtureSessionId = "1bb4112b-f6a0-4156-8a3f-d540ac208f92";

        private static readonly FieldInfo StorePathField =
            typeof(SessionHistoryStore).GetField("s_path", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException("SessionHistoryStore.s_path is gone; the sandbox redirect below depends on it.");

        [Fact]
        public void A_refresh_replaces_stale_titles_on_the_dispatcher_and_leaves_the_rest_alone()
        {
            Sta.Run(() =>
            {
                using var sandbox = new HistorySandbox();

                string expectedTitle = sandbox.ExpectedTitle;
                var vm = new ChatSessionViewModel();
                vm.Initialize(null, Cwd); // populates SessionHistory, now filtered to this cwd - the fixture rows are seeded under it

                try
                {
                    Assert.Equal(3, vm.SessionHistory.Count);

                    SessionHistoryEntry stale = Row(vm, FixtureSessionId);
                    SessionHistoryEntry renamed = Row(vm, FixtureSessionId + "-renamed");
                    SessionHistoryEntry orphan = Row(vm, "00000000-0000-0000-0000-000000000000");

                    // The background read may well have finished by now, but its result is queued on
                    // the dispatcher and cannot have been applied yet. If this ever fails, the
                    // refresh is mutating the bound list off the UI thread - the exact bug this
                    // design exists to avoid.
                    Assert.NotEqual(expectedTitle, stale.Title);

                    Assert.True(Sta.PumpUntil(() => stale.Title == expectedTitle), "the refresh never landed");

                    Assert.Equal("A name I typed myself", renamed.Title);
                    Assert.Equal("No transcript for this one", orphan.Title);
                    Assert.Matches(@"^\d+:\d+$", stale.TitleStamp);
                    Assert.False(RefreshRunning(vm), "the refresh flag must be released so history can refresh again");

                    string onDisk = File.ReadAllText(sandbox.JsonPath);
                    Assert.Contains(expectedTitle, onDisk);
                    Assert.Matches(@"""titleStamp"":\s*""\d+:\d+""", onDisk);
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }

        [Fact]
        public void A_rename_typed_while_a_refresh_is_in_flight_wins()
        {
            Sta.Run(() =>
            {
                using var sandbox = new HistorySandbox();

                var vm = new ChatSessionViewModel();
                vm.Initialize(null, Cwd); // populates SessionHistory, now filtered to this cwd - the fixture rows are seeded under it

                try
                {
                    SessionHistoryEntry stale = Row(vm, FixtureSessionId);
                    Assert.True(Sta.PumpUntil(() => stale.Title == sandbox.ExpectedTitle), "the first refresh never landed");

                    // Back to a stale, unstamped state, so a fresh refresh genuinely wants to
                    // change it - then rename after the refresh starts but before the apply pumps.
                    ResetToStale(stale);

                    vm.OpenSessionHistory();
                    Assert.True(vm.IsSessionHistoryVisible);

                    vm.CommitSessionEntryTitle(stale, "Typed while it was loading");
                    Assert.True(stale.HasUserTitle, "committing a title must mark the row as user-named");

                    Sta.Pump();

                    Assert.Equal("Typed while it was loading", stale.Title);
                    Assert.True(string.IsNullOrEmpty(stale.TitleStamp),
                        "the row must be left unstamped, so a later un-rename still re-reads");
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }

        [Fact]
        public void The_identical_refresh_does_change_the_title_when_nothing_was_typed()
        {
            // The control for the race test above. Without it, that test could be passing
            // vacuously: a refresh that computed no update at all would also leave the title alone.
            // Same starting state, same call, no rename - the title must move.
            Sta.Run(() =>
            {
                using var sandbox = new HistorySandbox();

                var vm = new ChatSessionViewModel();
                vm.Initialize(null, Cwd); // populates SessionHistory, now filtered to this cwd - the fixture rows are seeded under it

                try
                {
                    SessionHistoryEntry stale = Row(vm, FixtureSessionId);
                    Assert.True(Sta.PumpUntil(() => stale.Title == sandbox.ExpectedTitle), "the first refresh never landed");

                    ResetToStale(stale);

                    vm.OpenSessionHistory();

                    Assert.True(Sta.PumpUntil(() => stale.Title == sandbox.ExpectedTitle),
                        "with no rename in the way, the refresh must replace the title");
                }
                finally
                {
                    vm.Dispose();
                }
            });
        }

        // ─── helpers ────────────────────────────────────────────────────────────────────────────

        private static SessionHistoryEntry Row(ChatSessionViewModel vm, string sessionId)
            => vm.SessionHistory.Single(e => e.SessionId == sessionId);

        private static void ResetToStale(SessionHistoryEntry entry)
        {
            entry.Title = "stale again";
            entry.TitleStamp = "";
            entry.HasUserTitle = false;
        }

        private static bool RefreshRunning(ChatSessionViewModel vm)
        {
            FieldInfo field = typeof(ChatSessionViewModel)
                .GetField("_titleRefreshRunning", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new MissingFieldException("ChatSessionViewModel._titleRefreshRunning is gone.");

            return (bool)field.GetValue(vm)!;
        }

        /// <summary>
        /// Points <see cref="SessionHistoryStore"/> at a throwaway file, seeds it with the three
        /// rows that cover the three branches of the apply, and puts the real path back afterwards.
        /// It also records the user's own history file's timestamp so the test can prove it was
        /// never written.
        /// </summary>
        private sealed class HistorySandbox : IDisposable
        {
            private readonly string _directory;
            private readonly string _realPath;
            private readonly DateTime? _realWrittenBefore;

            public string JsonPath { get; }

            public string ExpectedTitle { get; }

            public HistorySandbox()
            {
                _realPath = (string)StorePathField.GetValue(null)!;
                _realWrittenBefore = File.Exists(_realPath) ? File.GetLastWriteTimeUtc(_realPath) : null;

                _directory = Path.Combine(Path.GetTempPath(), "teron-phase-f-vm-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_directory);
                JsonPath = Path.Combine(_directory, "sessions.json");

                StorePathField.SetValue(null, JsonPath);
                Assert.Equal(JsonPath, (string)StorePathField.GetValue(null)!);

                // The generated title this session actually has on disk - what the refresh has to
                // arrive at. If it is gone, the fixture rather than the product is the problem.
                SessionTitleReader.Result? onDisk = SessionTitleReader.Read(Cwd, FixtureSessionId);
                Skip.Unless(onDisk != null, $"No title on disk for {FixtureSessionId} under {Cwd} - the fixture transcript is gone.");
                ExpectedTitle = onDisk!.Title;

                File.WriteAllText(JsonPath, Seed(), new UTF8Encoding(false));
            }

            private static string Seed() => $@"[
  {{ ""id"": ""{FixtureSessionId}"", ""title"": ""Read the meta-procedure file and tell me what…"", ""lastUsed"": ""2026-08-29T18:46:19Z"", ""cwd"": ""{Escaped(Cwd)}"", ""userTitle"": false, ""titleStamp"": """" }},
  {{ ""id"": ""{FixtureSessionId}-renamed"", ""title"": ""A name I typed myself"", ""lastUsed"": ""2026-08-29T18:46:19Z"", ""cwd"": ""{Escaped(Cwd)}"", ""userTitle"": true, ""titleStamp"": """" }},
  {{ ""id"": ""00000000-0000-0000-0000-000000000000"", ""title"": ""No transcript for this one"", ""lastUsed"": ""2026-08-29T18:46:19Z"", ""cwd"": ""{Escaped(Cwd)}"", ""userTitle"": false, ""titleStamp"": """" }}
]";

            private static string Escaped(string path) => path.Replace(@"\", @"\\");

            public void Dispose()
            {
                StorePathField.SetValue(null, _realPath);

                DateTime? after = File.Exists(_realPath) ? File.GetLastWriteTimeUtc(_realPath) : null;
                Assert.Equal(_realWrittenBefore, after);

                try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
            }
        }
    }
}
