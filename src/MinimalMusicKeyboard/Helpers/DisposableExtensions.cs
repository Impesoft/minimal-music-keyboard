namespace MinimalMusicKeyboard.Helpers;

/// <summary>
/// Extension methods for safe disposal during shutdown sequences where
/// a failing Dispose must not prevent subsequent disposals.
/// </summary>
internal static class DisposableExtensions
{
    /// <summary>
    /// Disposes the object, swallowing any exception.
    /// Used in shutdown sequences where partial failure must not block subsequent steps.
    /// </summary>
    public static void SafeDispose(this IDisposable? disposable, string? label = null)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SafeDispose] {label ?? disposable?.GetType().Name}: {ex.Message}");
        }
    }
}
