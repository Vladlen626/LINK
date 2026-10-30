namespace Link.Desktop.Windows;

/// <summary>
/// Holds the computer out of sleep while Link works for a user who is away; the screen may still go dark. Windows keeps
/// the hold per thread: it is set and let go on the UI thread.
/// </summary>
internal static class KeepAwake
{
    private static bool _held;

    public static void Hold(bool hold)
    {
        if (hold == _held) return;
        _held = hold;
        NativeMethods.SetThreadExecutionState(hold ? NativeMethods.EsContinuous | NativeMethods.EsSystemRequired : NativeMethods.EsContinuous);
    }
}
