using System;
using System.Globalization;
using System.Speech.Recognition;

namespace TeronClaudeCodeVS.Core
{
    /// <summary>Why the mic is disabled, when it is. The reason is shown to the user verbatim.</summary>
    internal sealed class VoiceAvailability
    {
        private VoiceAvailability(bool isAvailable, string? reason, string? recognizerName)
        {
            IsAvailable = isAvailable;
            Reason = reason;
            RecognizerName = recognizerName;
        }

        public bool IsAvailable { get; }

        /// <summary>Null when available; otherwise a sentence fit for a tooltip.</summary>
        public string? Reason { get; }

        /// <summary>The recognizer that would be used, e.g. "Microsoft Speech Recognizer 8.0 for Windows (English - US)".</summary>
        public string? RecognizerName { get; }

        public static VoiceAvailability Available(string recognizerName) =>
            new VoiceAvailability(true, null, recognizerName);

        public static VoiceAvailability Unavailable(string reason) =>
            new VoiceAvailability(false, reason, null);
    }

    /// <summary>
    /// FEAT-8. Dictation into the composer, using the speech recognizer Windows already ships.
    ///
    /// <para><b>Why <c>System.Speech</c> and not a cloud transcription API.</b> Baseline's mic sends
    /// audio somewhere; ours does not, and that difference is deliberate rather than a shortcut.
    /// <c>System.Speech.Recognition</c> is a .NET Framework assembly wrapping SAPI, so recognition
    /// happens on this machine, offline, with no key to configure and no audio leaving it. The
    /// tradeoff is accuracy - the desktop recognizer is far weaker than a modern hosted model - and
    /// that is the honest state of this feature rather than something the UI hides.</para>
    ///
    /// <para><b>Availability is a real question, not a formality.</b> Two independent things can be
    /// missing: the recognizer (a Windows optional feature, absent on some Server SKUs and some
    /// non-English installs) and an audio input device. They fail at different moments and read
    /// differently to a user, so they are reported separately. <see cref="Probe"/> answers the first
    /// without touching the microphone; the second can only be discovered by asking for the device,
    /// so it surfaces on the first <see cref="Start"/> and is reported through
    /// <see cref="Failed"/>.</para>
    ///
    /// <para><b>Events arrive on a worker thread.</b> <c>RecognizeAsync</c> raises everything on a
    /// thread-pool thread; nothing here touches WPF, and every consumer marshals to the dispatcher
    /// itself. Keeping that boundary in the caller rather than capturing a Dispatcher here is what
    /// lets the headless test drive this class with no UI at all.</para>
    /// </summary>
    internal sealed class VoiceInput : IDisposable
    {
        private SpeechRecognitionEngine? _engine;
        private bool _disposed;

        /// <summary>A final, committed recognition. Raised on a worker thread.</summary>
        public event EventHandler<string>? TextRecognized;

        /// <summary>An in-progress guess, for a live hint while the user is still speaking.</summary>
        public event EventHandler<string>? TextHypothesized;

        /// <summary>Recognition stopped for a reason the user should see. Raised on a worker thread.</summary>
        public event EventHandler<string>? Failed;

        /// <summary>Listening started or stopped. Raised on a worker thread.</summary>
        public event EventHandler<bool>? ListeningChanged;

        public bool IsListening { get; private set; }

        /// <summary>
        /// Whether dictation could work at all, answered without opening the microphone.
        ///
        /// Deliberately does not construct the engine with the parameterless constructor: that picks
        /// the recognizer for the current culture and throws when there is none, which turns "no
        /// English recognizer on a German Windows" into an exception rather than an answer. A
        /// recognizer for the current culture is preferred and any installed one is accepted.
        /// </summary>
        public static VoiceAvailability Probe()
        {
            try
            {
                RecognizerInfo? recognizer = SelectRecognizer();
                return recognizer == null
                    ? VoiceAvailability.Unavailable(
                        "Dictation needs a Windows speech recognizer, and none is installed. " +
                        "Add one in Settings ▸ Time & language ▸ Speech.")
                    : VoiceAvailability.Available(recognizer.Description);
            }
            catch (Exception ex)
            {
                // InstalledRecognizers touches the SAPI registry; a broken speech stack throws here
                // rather than returning an empty list, and a disabled mic button with a reason is a
                // better outcome than a tool window that fails to load.
                return VoiceAvailability.Unavailable("The Windows speech recognizer could not be queried: " + ex.Message);
            }
        }

        private static RecognizerInfo? SelectRecognizer()
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            if (installed == null || installed.Count == 0) return null;

            string current = CultureInfo.CurrentUICulture.Name;
            foreach (RecognizerInfo info in installed)
            {
                if (string.Equals(info.Culture.Name, current, StringComparison.OrdinalIgnoreCase))
                    return info;
            }

            // A recognizer in the wrong language still dictates - badly, but it dictates. Silently
            // refusing would be worse than letting the user hear the result and decide.
            return installed[0];
        }

        /// <summary>
        /// Begins listening on the default audio device. Returns null on success, or the reason it
        /// could not start - which is the only place "there is no microphone" can be discovered.
        /// </summary>
        public string? Start()
        {
            if (_disposed) return "Dictation has already been shut down.";
            if (IsListening) return null;

            try
            {
                SpeechRecognitionEngine engine = CreateEngine();
                engine.SetInputToDefaultAudioDevice();
                _engine = engine;
                engine.RecognizeAsync(RecognizeMode.Multiple);
                SetListening(true);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                // What SetInputToDefaultAudioDevice throws when there is no capture device at all.
                Cleanup();
                return "No microphone is available for dictation. " + ex.Message;
            }
            catch (Exception ex)
            {
                Cleanup();
                return "Dictation could not start: " + ex.Message;
            }
        }

        /// <summary>
        /// The same pipeline, reading a wave file instead of the microphone.
        ///
        /// This exists so recognition can be proven without a person speaking: the Phase J unit
        /// check synthesises a sentence to a .wav and asserts this class returns it. It is the
        /// production code path with one line different, which is the point - a test that stubbed
        /// the engine would prove only that the stub works.
        /// </summary>
        public string? StartFromWaveFile(string wavePath)
        {
            if (_disposed) return "Dictation has already been shut down.";
            if (IsListening) return null;

            try
            {
                SpeechRecognitionEngine engine = CreateEngine();
                engine.SetInputToWaveFile(wavePath);
                _engine = engine;
                engine.RecognizeAsync(RecognizeMode.Multiple);
                SetListening(true);
                return null;
            }
            catch (Exception ex)
            {
                Cleanup();
                return "Dictation could not read " + wavePath + ": " + ex.Message;
            }
        }

        private SpeechRecognitionEngine CreateEngine()
        {
            RecognizerInfo? info = SelectRecognizer();
            SpeechRecognitionEngine engine = info != null
                ? new SpeechRecognitionEngine(info)
                : new SpeechRecognitionEngine();

            // Free-form dictation rather than a command grammar: the composer takes prose.
            engine.LoadGrammar(new DictationGrammar());

            engine.SpeechRecognized += OnSpeechRecognized;
            engine.SpeechHypothesized += OnSpeechHypothesized;
            engine.RecognizeCompleted += OnRecognizeCompleted;
            return engine;
        }

        /// <summary>
        /// Stops listening, letting the recognizer finish whatever it is mid-way through.
        ///
        /// <c>RecognizeAsyncStop</c> rather than <c>RecognizeAsyncCancel</c> on purpose: cancelling
        /// discards the utterance in flight, which loses the last few words of anyone who stops
        /// talking and releases the button in the same motion.
        /// </summary>
        public void Stop()
        {
            SpeechRecognitionEngine? engine = _engine;
            if (engine == null) return;

            try
            {
                engine.RecognizeAsyncStop();
            }
            catch (Exception ex)
            {
                Failed?.Invoke(this, "Dictation could not be stopped cleanly: " + ex.Message);
            }
            finally
            {
                SetListening(false);
            }
        }

        private void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            string text = e.Result?.Text ?? "";
            if (text.Length > 0) TextRecognized?.Invoke(this, text);
        }

        private void OnSpeechHypothesized(object sender, SpeechHypothesizedEventArgs e)
        {
            string text = e.Result?.Text ?? "";
            if (text.Length > 0) TextHypothesized?.Invoke(this, text);
        }

        private void OnRecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            SetListening(false);

            if (e.Error != null)
                Failed?.Invoke(this, "Dictation stopped: " + e.Error.Message);
        }

        private void SetListening(bool value)
        {
            if (IsListening == value) return;
            IsListening = value;
            ListeningChanged?.Invoke(this, value);
        }

        private void Cleanup()
        {
            SpeechRecognitionEngine? engine = _engine;
            _engine = null;
            if (engine == null) return;

            engine.SpeechRecognized -= OnSpeechRecognized;
            engine.SpeechHypothesized -= OnSpeechHypothesized;
            engine.RecognizeCompleted -= OnRecognizeCompleted;

            try { engine.RecognizeAsyncCancel(); } catch { /* already stopped */ }
            try { engine.Dispose(); } catch { /* nothing left to release */ }

            SetListening(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
        }
    }
}
