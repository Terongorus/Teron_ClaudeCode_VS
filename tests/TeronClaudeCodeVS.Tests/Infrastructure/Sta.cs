using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;

namespace TeronClaudeCodeVS.Tests.Infrastructure
{
    /// <summary>
    /// Runs a body on a dedicated STA thread with a live <see cref="Dispatcher"/>.
    /// <para>
    /// WPF types throw on a multi-threaded-apartment thread, and xUnit's threads are MTA. The usual
    /// answer is the Xunit.StaFact package; this is four dozen lines instead of a dependency, and
    /// it also gives the body a real dispatcher - which the chat control needs, because several of
    /// its handlers marshal back with <c>Dispatcher.BeginInvoke</c> and would otherwise queue work
    /// onto a message loop that never runs.
    /// </para>
    /// </summary>
    internal static class Sta
    {
        public static void Run(Action body, int timeoutSeconds = 60)
        {
            ExceptionDispatchInfo? failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    // Without this, SynchronizationContext.Current is null on a thread that merely
                    // owns a Dispatcher rather than running one. The control's drop handler is
                    // `async void` over a Task.Run, so its continuation would then resume on a
                    // thread-pool thread and mutate a bound ObservableCollection off the UI thread -
                    // which is exactly the bug class these tests exist to catch, faked by the
                    // harness. WPF installs this context itself when a real message loop starts.
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

                    body();

                    // Let anything the body posted with BeginInvoke actually run before the
                    // dispatcher is torn down - otherwise a handler that marshals to the UI thread
                    // silently never executes and the test passes for the wrong reason.
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
                finally
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            if (!thread.Join(TimeSpan.FromSeconds(timeoutSeconds)))
                throw new TimeoutException($"STA body did not finish within {timeoutSeconds}s.");

            failure?.Throw();
        }

        public static T Run<T>(Func<T> body, int timeoutSeconds = 60)
        {
            T result = default!;
            Run(() => { result = body(); }, timeoutSeconds);
            return result;
        }

        /// <summary>
        /// Pumps the calling thread's dispatcher for a fixed span, then returns.
        /// <para>
        /// Use this - not <see cref="PumpUntil"/> - when the assertion is that something did NOT
        /// happen. Waiting on a condition that is supposed to stay false would return the instant
        /// it was checked and prove nothing; this gives the queued work a real chance to run first.
        /// </para>
        /// </summary>
        public static void Pump(int milliseconds = 1500)
        {
            var frame = new DispatcherFrame();

            var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher.CurrentDispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(milliseconds),
            };

            timer.Tick += (s, e) =>
            {
                timer.Stop();
                frame.Continue = false;
            };

            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        /// <summary>
        /// Pumps the calling thread's dispatcher until <paramref name="condition"/> holds or the
        /// timeout expires. Returns whether it held - callers assert on that rather than on a
        /// bare sleep, so a slow machine does not turn into a flaky failure and a genuinely broken
        /// path still fails instead of hanging.
        /// </summary>
        public static bool PumpUntil(Func<bool> condition, int timeoutMs = 5000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                    return true;

                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(15);
            }

            return condition();
        }
    }
}
