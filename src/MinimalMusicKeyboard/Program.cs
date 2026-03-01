using MinimalMusicKeyboard.Core;

namespace MinimalMusicKeyboard;

/// <summary>
/// Application entry point. Performs single-instance guard before handing off to WinUI3.
/// The named mutex is held for the entire process lifetime via the using block — it is
/// released naturally when Application.Start() returns (i.e., when the app exits).
/// </summary>
class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var guard = new SingleInstanceGuard();
        if (!guard.TryAcquire())
        {
            // Another instance is already running — exit immediately.
            // The running instance handles bringing itself to front independently.
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();

        Microsoft.UI.Xaml.Application.Start(p =>
        {
            // Required: set synchronization context so async/await marshals to the UI thread.
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);

            _ = new App();
        });

        // SingleInstanceGuard.Dispose() runs here (step 5 of architecture Section 6 shutdown).
    }
}
