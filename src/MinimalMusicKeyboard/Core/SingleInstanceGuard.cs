using System.Security.Principal;

namespace MinimalMusicKeyboard.Core;

/// <summary>
/// Enforces single-instance via a named Mutex scoped to the current user's SID.
/// Using a per-user name prevents conflicts if multiple Windows users run the app simultaneously.
/// The Mutex name matches the architecture: Global\MinimalMusicKeyboard-{UserSid}.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    /// <summary>
    /// Attempts to acquire the global named Mutex.
    /// Returns true if this is the first instance; false if another instance already holds it.
    /// </summary>
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "default";
        var name = $"Global\\MinimalMusicKeyboard-{sid}";

        _mutex = new Mutex(initiallyOwned: true, name: name, out _ownsMutex);
        return _ownsMutex;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsMutex && _mutex is not null)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* already released or abandoned */ }
        }

        _mutex?.Dispose();
        _mutex = null;

        GC.SuppressFinalize(this);
    }
}
