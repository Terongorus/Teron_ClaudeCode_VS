using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using TeronClaudeCodeVS.Core;
using TeronClaudeCodeVS.Tests.Infrastructure;
using TeronClaudeCodeVS.ViewModels;
using Xunit;

namespace TeronClaudeCodeVS.Tests.Phases
{
    /// <summary>
    /// Phase J (FEAT-8 voice dictation, FEAT-9 running/cloud sessions), ported from
    /// <c>comparison-audit/scripts/phase-j-unit.ps1</c>.
    /// <para>
    /// Dictation is actually exercised, not stubbed: a sentence is synthesised to a real .wav and
    /// fed through <see cref="VoiceInput"/>'s real pipeline - the same <c>SpeechRecognitionEngine</c>,
    /// the same <c>DictationGrammar</c>, the same event plumbing the microphone path uses, with one
    /// line different (<c>SetInputToWaveFile</c> instead of <c>SetInputToDefaultAudioDevice</c>). A
    /// test that mocked the engine would prove only that the mock works, and nobody can speak into a
    /// headless CI run - so this is the honest substitute, paired with a silence CONTROL.
    /// </para>
    /// <para>
    /// The session parser is fed real captured CLI output, in two captures rather than one, because
    /// the field set changes with the session's state: a background agent has <c>pid</c>/<c>status</c>
    /// while alive and neither once stopped. See <c>../fixtures/README.md</c>.
    /// </para>
    /// </summary>
    public sealed class VoiceAndSessionsTests
    {
        private const string ThisFolder = @"d:\Projects\Visual Studio Projects\Teron_Extensions";

        // The exact cwd baked into agents-all.json's background-agent row at capture time - not a
        // stand-in. A shorter placeholder here would still exercise the "another folder" branch,
        // but Assert.Contains(BgFolder, ...) below has to match the fixture's own string byte for
        // byte, or it fails on the harness rather than on anything the product got wrong.
        private const string BgFolder =
            @"C:\Users\kkole\AppData\Local\Temp\claude\d--Projects-Visual-Studio-Projects-Teron-Extensions\a0084635-226d-4e83-a751-65bdbaa155fd\scratchpad\bgtest";

        private static string LiveJson => Fixtures.Read("agents-live-background.json");
        private static string StoppedJson => Fixtures.Read("agents-all.json");

        // ─── FEAT-9: the session list, parsed from real CLI output ─────────────────────────────

        [Fact]
        public void Three_sessions_parse_from_the_live_capture_with_the_background_agent_distinct()
        {
            DateTime now = DateTime.UtcNow;
            List<AgentSessionEntry> live = AgentSessionsViewModel.Parse(LiveJson, ThisFolder, now);

            Assert.Equal(3, live.Count);

            AgentSessionEntry bgLive = Assert.Single(live, e => e.Kind == "background");
            Assert.Equal("e6e765fd", bgLive.ShortId);
            Assert.Equal("reply to pong", bgLive.Name);
            Assert.True(bgLive.IsRunning);
            Assert.Equal(25328, bgLive.Pid);
            Assert.Equal("idle", bgLive.Status);
            Assert.Equal("done", bgLive.State);

            AgentSessionEntry[] interactive = live.Where(e => e.Kind == "interactive").ToArray();
            Assert.Equal(2, interactive.Length);
            Assert.All(interactive, e => Assert.Null(e.ShortId));
            // CONTROL: the same read DOES find a short id when the JSON has one.
            Assert.NotNull(bgLive.ShortId);
            Assert.Null(interactive[0].Status);
            Assert.Null(interactive[0].State);
        }

        [Fact]
        public void The_same_agent_after_being_stopped_keeps_its_state_but_loses_its_pid_and_status()
        {
            DateTime now = DateTime.UtcNow;
            AgentSessionEntry bgLive = Assert.Single(
                AgentSessionsViewModel.Parse(LiveJson, ThisFolder, now), e => e.Kind == "background");
            AgentSessionEntry bgStopped = Assert.Single(
                AgentSessionsViewModel.Parse(StoppedJson, ThisFolder, now), e => e.Kind == "background");

            Assert.Equal(bgLive.SessionId, bgStopped.SessionId);
            Assert.False(bgStopped.IsRunning);
            Assert.Null(bgStopped.Pid);
            Assert.Null(bgStopped.Status);
            Assert.Equal("done", bgStopped.State);

            // This pair is the whole reason there are two fixtures: pid and status are optional in
            // fact, not in theory, and a parser that required either would pass the live capture
            // and fail this one.
            Assert.NotNull(bgLive.Pid);
            Assert.NotNull(bgLive.Status);
        }

        [Fact]
        public void What_each_row_is_allowed_to_do_depends_on_whether_and_where_it_is_running()
        {
            DateTime now = DateTime.UtcNow;
            List<AgentSessionEntry> live = AgentSessionsViewModel.Parse(LiveJson, ThisFolder, now);
            AgentSessionEntry bgLive = Assert.Single(live, e => e.Kind == "background");
            AgentSessionEntry interactive = live.First(e => e.Kind == "interactive");
            AgentSessionEntry bgStopped = Assert.Single(
                AgentSessionsViewModel.Parse(StoppedJson, ThisFolder, now), e => e.Kind == "background");

            Assert.False(interactive.CanOpenHere, "a live interactive session cannot be opened here - something is running it");
            Assert.Contains($"running right now (pid {interactive.Pid})", interactive.OpenHereBlockedReason);
            Assert.Null(interactive.TerminalArgs);
            Assert.False(interactive.CanOpenInTerminal, "nothing joins a live interactive session");

            // CONTROL: a LIVE BACKGROUND agent does offer a terminal command, via the CLI's own attach.
            Assert.True(bgLive.CanOpenInTerminal);
            Assert.Equal("attach", bgLive.TerminalArgs![0]);
            Assert.Equal("e6e765fd", bgLive.TerminalArgs[1]);

            Assert.Equal("--resume", bgStopped.TerminalArgs![0]);
            Assert.Equal(bgStopped.SessionId, bgStopped.TerminalArgs[1]);
            Assert.Contains("claude --resume", bgStopped.TerminalCommandText);
            Assert.Contains(BgFolder, bgStopped.TerminalCommandText);

            Assert.False(bgStopped.CanOpenHere, "a stopped agent from ANOTHER folder still cannot be opened here");
            Assert.Contains("was started in", bgStopped.OpenHereBlockedReason);
            Assert.DoesNotContain("pid", bgStopped.OpenHereBlockedReason);

            // CONTROL for both of the above: re-parse the SAME capture as though the IDE were open
            // on the agent's own folder. Nothing about the row changes except the fact under test.
            AgentSessionEntry bgRehomed = Assert.Single(
                AgentSessionsViewModel.Parse(StoppedJson, BgFolder, now), e => e.Kind == "background");

            Assert.True(bgRehomed.CanOpenHere);
            Assert.Null(bgRehomed.OpenHereBlockedReason);
            Assert.Equal(bgStopped.SessionId, bgRehomed.SessionId);
        }

        [Fact]
        public void Sessions_in_the_open_folder_sort_first_and_then_by_recency()
        {
            DateTime now = DateTime.UtcNow;
            List<AgentSessionEntry> stopped = AgentSessionsViewModel.Parse(StoppedJson, ThisFolder, now);

            Assert.True(stopped[0].IsCurrentFolder);
            Assert.True(stopped[1].IsCurrentFolder);
            Assert.False(stopped[2].IsCurrentFolder);
            Assert.True(stopped[0].StartedUtc >= stopped[1].StartedUtc);

            // CONTROL: the newest row overall is the background one, and it is NOT first - so the
            // folder rule is genuinely outranking the time rule rather than the two agreeing by luck.
            Assert.True(stopped[2].StartedUtc > stopped[0].StartedUtc);
            Assert.Equal("reply to pong", stopped[2].Name);
        }

        [Theory]
        [InlineData(@"D:\Projects\X", @"d:\projects\x", true)]
        [InlineData(@"d:\a\b\", @"d:\a\b", true)]
        [InlineData(@"d:/a/b", @"d:\a\b", true)]
        [InlineData(@"d:\a\b", @"d:\a\c", false)]
        [InlineData("", @"d:\a", false)]
        public void Folder_matching_ignores_case_trailing_separator_and_separator_style(string a, string b, bool expected)
        {
            Assert.Equal(expected, AgentSessionsViewModel.IsSameFolder(a, b));
        }

        [Fact]
        public void StartedAt_is_read_as_epoch_milliseconds_and_ages_use_the_rewind_pickers_wording()
        {
            AgentSessionEntry bgLive = Assert.Single(
                AgentSessionsViewModel.Parse(LiveJson, ThisFolder, DateTime.UtcNow), e => e.Kind == "background");

            Assert.Equal(
                DateTimeOffset.FromUnixTimeMilliseconds(1788205201603).UtcDateTime.Year,
                bgLive.StartedUtc.Year);

            Assert.Matches(@"^(just now|\d+[mhd] ago)$", bgLive.RelativeAge);
        }

        [Fact]
        public void A_row_announces_itself_by_name_and_its_detail_line_reads_as_one_sentence()
        {
            AgentSessionEntry bgLive = Assert.Single(
                AgentSessionsViewModel.Parse(LiveJson, ThisFolder, DateTime.UtcNow), e => e.Kind == "background");

            Assert.Equal("reply to pong", bgLive.ToString());
            Assert.StartsWith("background", bgLive.DetailLine);
            Assert.Contains("done", bgLive.DetailLine);
            Assert.EndsWith("ago", bgLive.DetailLine);
        }

        [Fact]
        public void Empty_output_parses_to_an_empty_list_rather_than_throwing()
        {
            Assert.Empty(AgentSessionsViewModel.Parse("[]", ThisFolder, DateTime.UtcNow));
        }

        // ─── FEAT-9: the cloud id rule, transcribed from the CLI's own validator ───────────────

        [Theory]
        [InlineData("session_abc123", "session_abc123")]
        [InlineData("cse_abc123", "cse_abc123")]
        [InlineData("https://claude.ai/code/session_abc123", "session_abc123")]
        [InlineData("https://claude.ai/code/cse_abc123", "cse_abc123")]
        [InlineData("  session_abc123\n", "session_abc123")]
        public void Accepted_cloud_id_forms_normalise_correctly(string pasted, string expected)
        {
            Assert.Equal(expected, AgentSessionsViewModel.NormalizeCloudId(pasted));
        }

        [Theory]
        [InlineData("00000000-0000-0000-0000-000000000000")]   // looks like a session id, is not one
        [InlineData("abc123")]
        [InlineData("session_")]
        [InlineData("session_abc!123")]
        [InlineData("")]
        [InlineData("https://claude.ai/")]
        public void Rejected_cloud_id_forms_normalise_to_null(string pasted)
        {
            Assert.Null(AgentSessionsViewModel.NormalizeCloudId(pasted));
        }

        [Fact]
        public void The_cloud_hint_text_explains_what_to_paste_and_where_it_will_open()
        {
            Assert.Matches(@"session_.*cse_.*claude\.ai/code", AgentSessionsViewModel.DescribeCloudInput(""));
            Assert.Contains("terminal", AgentSessionsViewModel.DescribeCloudInput("session_abc123"));
            Assert.Contains("cannot stream into this panel", AgentSessionsViewModel.DescribeCloudInput("session_abc123"));

            // Borrows the CLI's own rejection sentence rather than composing a new one.
            Assert.Equal("That is not a cloud session ID or URL.", AgentSessionsViewModel.DescribeCloudInput("nope"));
        }

        // ─── FEAT-8: is dictation possible on this machine at all ──────────────────────────────

        [Fact]
        public void The_probe_answers_without_throwing_and_this_machine_has_a_recognizer()
        {
            VoiceAvailability availability = VoiceInput.Probe();

            Assert.NotNull(availability);
            Skip.Unless(availability.IsAvailable,
                $"No speech recognizer on this machine (reason: {availability.Reason}) - see checklist item D3/D4.");
            Assert.Null(availability.Reason);
        }

        [Fact]
        public void CONTROL_an_unavailable_probe_is_not_available_and_carries_its_reason()
        {
            VoiceAvailability unavailable = VoiceAvailability.Unavailable("no recognizer here");

            Assert.False(unavailable.IsAvailable);
            Assert.Equal("no recognizer here", unavailable.Reason);
        }

        // ─── FEAT-8: recognition, through the real pipeline ────────────────────────────────────

        [Fact]
        public void A_spoken_sentence_is_recognised_through_the_real_pipeline()
        {
            Skip.Unless(VoiceInput.Probe().IsAvailable, "No speech recognizer on this machine.");

            // The PowerShell original needed powershell.exe's default STA apartment for this exact
            // pipeline and said so explicitly; an xUnit test thread carries no such guarantee (it is
            // effectively MTA with no message loop), and running this without Sta.Run once produced
            // a run that sat idle - not crashing, not progressing - which is the SAPI/COM apartment
            // hazard the original comment warned about, not a hang in VoiceInput itself.
            Sta.Run(() =>
            {
                string wav = Path.Combine(Path.GetTempPath(), "phase-j-dictation-" + Guid.NewGuid().ToString("N") + ".wav");

                try
                {
                    Synthesize(wav, "please add a unit test for the login page");
                    Assert.True(new FileInfo(wav).Length > 1000, "the synthesised wave file is suspiciously small");

                    (List<string> heard, List<bool> listening) = RunRecognition(wav, waitSeconds: 30, stopOnFirstResult: true);

                    string recognized = string.Join(" ", heard);
                    Assert.NotEmpty(listening);   // it raised ListeningChanged when it started
                    Assert.NotEmpty(heard);       // it recognised something
                    Assert.Matches(new Regex("add a unit test for the login page", RegexOptions.IgnoreCase), recognized);
                }
                finally
                {
                    DeleteBestEffort(wav);
                }
            }, timeoutSeconds: 60);
        }

        [Fact]
        public void CONTROL_the_same_pipeline_hears_nothing_in_silence()
        {
            // Without this, "it recognised the sentence" above could be a harness that reports
            // success for any audio at all. Same STA requirement as the test above.
            Skip.Unless(VoiceInput.Probe().IsAvailable, "No speech recognizer on this machine.");

            Sta.Run(() =>
            {
                string wav = Path.Combine(Path.GetTempPath(), "phase-j-silence-" + Guid.NewGuid().ToString("N") + ".wav");

                try
                {
                    // A bare empty PromptBuilder writes a literal zero-byte file - no WAV header
                    // at all - which SetInputToWaveFile correctly refuses as invalid, and that
                    // refusal (not silence) was this test's first measured failure. An explicit
                    // silent break produces real, well-formed PCM silence, which is the actual
                    // thing this CONTROL is supposed to feed the recognizer.
                    var format = new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
                    var silence = new PromptBuilder();
                    silence.AppendBreak(TimeSpan.FromSeconds(2));
                    using (var synth = new SpeechSynthesizer())
                    {
                        synth.SetOutputToWaveFile(wav, format);
                        synth.Speak(silence);
                    }
                    Assert.True(new FileInfo(wav).Length > 0, "the silent wave file must not be empty");

                    (List<string> heard, _) = RunRecognition(wav, waitSeconds: 3, stopOnFirstResult: false);

                    Assert.Empty(heard);
                }
                finally
                {
                    DeleteBestEffort(wav);
                }
            }, timeoutSeconds: 30);
        }

        /// <summary>
        /// The engine's own <c>Dispose</c> can still be finishing its release of the wave file
        /// handle for a moment after <see cref="VoiceInput.Stop"/> returns - the same race the
        /// PowerShell original tolerated with <c>-ErrorAction SilentlyContinue</c> on its own
        /// cleanup. A leftover temp file is not a test failure; only what was recognised is.
        /// </summary>
        private static void DeleteBestEffort(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }

        /// <summary>
        /// Drives <see cref="VoiceInput"/> exactly as the mic path does, differing only in where the
        /// audio comes from. Subscribes with plain C# event handlers on a background thread and
        /// hands results back through a thread-safe list - the direct equivalent of the PowerShell
        /// original's no-<c>-Action</c> <c>Register-ObjectEvent</c>, which existed there because an
        /// <c>-Action</c> block's own scope hid results from the caller, and because invoking a
        /// PowerShell scriptblock from VoiceInput's recognition thread (no runspace) crashed the
        /// process outright. Neither hazard applies to a plain delegate, but the result is collected
        /// the same defensive way: appended under a lock from whatever thread raised the event, read
        /// back only after <see cref="VoiceInput.Stop"/> has returned.
        /// </summary>
        private static (List<string> Heard, List<bool> ListeningEvents) RunRecognition(
            string wavePath, int waitSeconds, bool stopOnFirstResult)
        {
            var heard = new List<string>();
            var listening = new List<bool>();
            var gate = new object();

            using var voice = new VoiceInput();

            voice.TextRecognized += (s, text) => { lock (gate) { heard.Add(text); } };
            voice.ListeningChanged += (s, isListening) => { lock (gate) { listening.Add(isListening); } };

            string? startError = voice.StartFromWaveFile(wavePath);
            Assert.Null(startError);

            // Must actively pump this thread's message queue while waiting, not block on it.
            // SpeechRecognitionEngine wraps a SAPI COM engine that is apartment-affine to the
            // thread that constructed it; on an STA thread (which Sta.Run puts this on, for
            // VoiceInput's own reasons - see its class comment), a cross-apartment callback
            // cannot be delivered until that thread's message queue is drained. A raw blocking
            // wait (ManualResetEventSlim.Wait, Thread.Sleep) never drains it, so no callback
            // arrives - not eventually, not ever. That is what produced this test's first two
            // measured failures: 30s of nothing heard, and (unbounded, before a timeout existed
            // at all) the run that had to be killed after twelve minutes idle. Sta.PumpUntil
            // actively pumps, which is what a real message loop does and what let the same
            // pipeline resolve in ~230ms in an isolated, non-STA PowerShell probe.
            if (stopOnFirstResult)
            {
                Sta.PumpUntil(() => { lock (gate) return heard.Count > 0; }, waitSeconds * 1000);
            }
            else
            {
                Sta.Pump(waitSeconds * 1000);
            }

            voice.Stop();
            Sta.Pump(500);   // lets a straggling event land before it is read

            lock (gate)
                return (new List<string>(heard), new List<bool>(listening));
        }

        private static void Synthesize(string wavPath, string sentence)
        {
            using var synth = new SpeechSynthesizer();
            synth.SetOutputToWaveFile(wavPath);
            synth.Rate = -1;
            synth.Speak(sentence);
        }

        // ─── FEAT-8 / FEAT-9: what the view model exposes ───────────────────────────────────────

        [Fact]
        public void The_mic_is_disabled_and_explains_why_until_it_has_been_probed()
        {
            Sta.Run(() =>
            {
                using var vm = new ChatSessionViewModel();

                Assert.False(vm.IsVoiceAvailable);
                Assert.Contains("has not been checked", vm.VoiceTooltipText);

                vm.ProbeVoiceAvailability();

                if (!VoiceInput.Probe().IsAvailable)
                    return;   // matches the CONTROL_an_unavailable_probe test's own skip condition

                Assert.True(vm.IsVoiceAvailable, "after probing, the mic should be available on this machine");
                Assert.Equal("Tap or hold to record · Ctrl+D", vm.VoiceTooltipText);
            });
        }

        [Fact]
        public void The_dictation_status_line_reflects_listening_and_the_running_hypothesis()
        {
            Sta.Run(() =>
            {
                using var vm = new ChatSessionViewModel();

                Assert.False(vm.IsDictating);
                Assert.False(vm.HasVoiceStatus);
                Assert.Equal("", vm.VoiceStatusText);

                vm.IsDictating = true;
                Assert.Equal("🎤 Listening…", vm.VoiceStatusText);

                vm.VoiceHypothesis = "add a unit";
                Assert.Equal("🎤 add a unit", vm.VoiceStatusText);

                vm.IsDictating = false;
                // CONTROL: the status line disappears again when it stops.
                Assert.False(vm.HasVoiceStatus);
            });
        }

        [Fact]
        public void History_opens_on_Local_and_selecting_a_tab_deselects_the_others()
        {
            Sta.Run(() =>
            {
                using var vm = new ChatSessionViewModel();

                Assert.True(vm.IsLocalTab);
                Assert.False(vm.IsRunningTab);
                Assert.False(vm.IsCloudTab);

                vm.SelectedHistoryTab = ChatSessionViewModel.HistoryTab.Running;
                Assert.True(vm.IsRunningTab);
                Assert.False(vm.IsLocalTab);
                Assert.False(vm.IsCloudTab);

                vm.SelectedHistoryTab = ChatSessionViewModel.HistoryTab.Cloud;
                Assert.True(vm.IsCloudTab);
                Assert.False(vm.IsRunningTab);
            });
        }

        [Fact]
        public void The_cloud_open_button_is_gated_on_a_genuinely_valid_id()
        {
            Sta.Run(() =>
            {
                using var vm = new ChatSessionViewModel();

                Assert.False(vm.CanOpenCloudSession);

                vm.CloudSessionInput = "not-an-id";
                Assert.False(vm.CanOpenCloudSession);

                // CONTROL: a real link enables it, so the gate is doing work.
                vm.CloudSessionInput = "https://claude.ai/code/session_abc123";
                Assert.True(vm.CanOpenCloudSession);
                Assert.Contains("terminal", vm.CloudHintText);
            });
        }

        [Fact]
        public void The_running_session_list_starts_empty_and_unloaded_which_are_different_states()
        {
            Sta.Run(() =>
            {
                using var vm = new ChatSessionViewModel();

                Assert.Empty(vm.AgentSessions.Sessions);
                Assert.False(vm.AgentSessions.HasLoaded);
                // An unloaded list is not "empty" - those are different states.
                Assert.False(vm.AgentSessions.IsEmpty);
            });
        }

        // ─── The XAML says what the code says ───────────────────────────────────────────────────

        [Fact]
        public void The_mic_and_history_tab_markup_matches_what_the_code_behind_actually_wires_up()
        {
            string xaml = File.ReadAllText(Fixtures.ProjectFile("Core", "ClaudeCodeChatControl.xaml"));
            string code = File.ReadAllText(Fixtures.ProjectFile("Core", "ClaudeCodeChatControl.xaml.cs"));

            Assert.Contains("AutomationId=\"MicButton\"", xaml);
            Assert.Contains("IsEnabled=\"{Binding IsVoiceAvailable}\"", xaml);
            Assert.Contains("ToolTip=\"{Binding VoiceTooltipText}\"", xaml);

            Assert.Contains("e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control", code);
            Assert.Contains("MicTapThreshold", code);

            // A Button whose only handlers are mouse handlers cannot be pressed by a keyboard, a
            // screen reader or UI Automation. Both paths have to exist, and the flag is what stops
            // them cancelling each other.
            Assert.Contains("Click=\"OnMicClicked\"", xaml);
            Assert.Contains("_micGestureHandled", code);

            foreach (string tab in new[] { "HistoryLocalTabButton", "HistoryRunningTabButton", "HistoryCloudTabButton" })
                Assert.Contains($"AutomationId=\"{tab}\"", xaml);

            foreach (string id in new[] { "AgentSessionsList", "AgentSessionsEmptyState", "AgentSessionsError" })
                Assert.Contains($"AutomationId=\"{id}\"", xaml);

            Assert.Contains(AgentSessionsViewModel.EmptyStateText, xaml);

            Assert.Contains("AutomationId=\"CloudGapNote\"", xaml);
            Assert.Contains("no command that lists your cloud sessions", xaml);
            Assert.Contains("refuses the streaming output format", xaml);
        }
    }
}
