namespace PAWS
{
    /// <summary>
    /// Custom entry point — the XAML-generated <c>Program.Main</c> is disabled (see
    /// <c>DISABLE_XAML_GENERATED_MAIN</c> in PAWS.csproj) so a second launch can be detected and bounced
    /// BEFORE any WinUI <see cref="Microsoft.UI.Xaml.Application"/> machinery, let alone the sync engines
    /// and tray icon, gets constructed. See <see cref="SingleInstanceGuard"/>. Otherwise identical to the
    /// generated entry point.
    /// </summary>
    public static class Program
    {
        [System.STAThread]
        private static void Main(string[] args)
        {
            if (!SingleInstanceGuard.TryBecomePrimary())
            {
                // Another instance is already running and has just been asked to come to the foreground.
                return;
            }

            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            global::Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
        }
    }
}
