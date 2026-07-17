using System;
using System.Threading;

namespace PAWS
{
    /// <summary>
    /// Ensures only one PAWS process runs per Windows sign-in session (the OneDrive/Dropbox model): a
    /// second launch (double-clicking the shortcut again, Windows re-running the sign-in autostart entry
    /// while the tray instance is already up, etc.) must not spin up a second set of sync engines and
    /// tray icon — two processes independently registering the same on-demand sync roots and driving the
    /// same Drive account would race each other and could corrupt local/remote state. Uses a named Mutex
    /// to detect an existing instance and a named <see cref="EventWaitHandle"/> to ask it to come to the
    /// foreground — a full IPC channel (a named pipe) would be overkill for the one thing a second launch
    /// actually needs to say: "someone tried to start me again, please show yourself".
    /// <para>Session-local names (no <c>Global\</c> prefix) are deliberate: a second Windows user signed
    /// in via Fast User Switching should still get their own instance, only a second launch by the SAME
    /// user should be bounced.</para>
    /// </summary>
    internal static class SingleInstanceGuard
    {
        private const string MutexName = "PAWS-30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64-SingleInstance";
        private const string ActivateEventName = "PAWS-30d8b2a4-6f1e-4c93-9c2a-1f7b5e0d3a64-Activate";

        // Held for the lifetime of the primary process — releasing/disposing it is what lets a future
        // launch detect "no one's running" again after this instance exits.
        private static Mutex? _mutex;

        /// <summary>
        /// Tries to become the single instance. Returns true if this process should proceed normally (no
        /// one else is running); false if another instance already holds the lock — the existing
        /// instance has already been signalled to activate, and this process should exit immediately
        /// without constructing anything else (no WinUI <c>Application</c>, no sync engines, no tray icon).
        /// </summary>
        public static bool TryBecomePrimary()
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
            {
                return true;
            }

            SignalExistingInstance();

            // We never owned it (createdNew is false) — dispose without releasing.
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        private static void SignalExistingInstance()
        {
            try
            {
                using var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName);
                activateEvent.Set();
            }
            catch
            {
                // Best-effort: if the running instance's listener isn't registered yet (a narrow window
                // right at its own startup) the user just needs to click the tray icon once themselves.
            }
        }

        /// <summary>
        /// Starts a background listener for activation requests from a future second launch, invoking
        /// <paramref name="onActivateRequested"/> on a background thread each time one arrives — the
        /// caller is responsible for marshalling back to the UI thread. Only the instance that got
        /// <see langword="true"/> from <see cref="TryBecomePrimary"/> should call this.
        /// </summary>
        public static void ListenForActivation(Action onActivateRequested)
        {
            var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

            var thread = new Thread(() =>
            {
                while (true)
                {
                    activateEvent.WaitOne();
                    onActivateRequested();
                }
            })
            {
                IsBackground = true,
                Name = "PAWS-SingleInstance-Listener",
            };
            thread.Start();
        }
    }
}
