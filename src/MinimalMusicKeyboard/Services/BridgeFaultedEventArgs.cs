namespace MinimalMusicKeyboard.Services;

/// <summary>
/// Event arguments for the <see cref="Vst3BridgeBackend.BridgeFaulted"/> event.
/// </summary>
public sealed class BridgeFaultedEventArgs : EventArgs
{
    /// <summary>Human-readable description of the fault condition.</summary>
    public string Reason { get; }

    /// <summary>The exception that caused the fault, or <see langword="null"/> if not exception-driven.</summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Initialises a new <see cref="BridgeFaultedEventArgs"/> instance.
    /// </summary>
    /// <param name="reason">Human-readable fault description.</param>
    /// <param name="exception">Optional causal exception.</param>
    public BridgeFaultedEventArgs(string reason, Exception? exception = null)
    {
        Reason = reason;
        Exception = exception;
    }
}
