using System.Diagnostics;
using System.Text;
using Link.Desktop.Models;

namespace Link.Desktop.Windows;

/// <summary>
/// Knows which application the user works in. The work target is latched when the user invokes Link and
/// stays put while they talk to Link, glance at the chat or Link hides itself for a capture.
/// </summary>
public sealed class CurrentApplicationService : IDisposable
{
    // Focus shorter than this is passing through (Alt+Tab, Link hiding for a capture), not working there.
    private static readonly TimeSpan SettledFocus = TimeSpan.FromSeconds(2);

    // The taskbar, desktop and system surfaces are where Link is invoked from, not what the user works on.
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Progman",
        "WorkerW",
        "XamlExplorerHostIslandWindow",
        "ForegroundStaging",
        "MultitaskingViewFrame",
        "Windows.UI.Core.CoreWindow"
    };

    private readonly int _linkProcessId = Environment.ProcessId;
    private readonly NativeMethods.WinEventProc _foregroundChanged;
    private readonly IntPtr _foregroundHook;
    private IntPtr _recentWindow;
    private long _recentSince;
    private long? _recentUntil;
    private bool _disposed;

    public CurrentApplicationService()
    {
        _foregroundChanged = OnForegroundChanged;
        // Out-of-context events arrive through this thread's message loop; nothing is injected into other apps.
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            IntPtr.Zero,
            _foregroundChanged,
            0,
            0,
            NativeMethods.WinEventOutOfContext);
        NoteForeground(NativeMethods.GetForegroundWindow());
        WorkTarget = _recentWindow;
    }

    /// <summary>The window the user invoked Link from; its app name, title and screenshot go with requests.</summary>
    public IntPtr WorkTarget { get; private set; }

    /// <summary>A global hotkey was pressed: the application in front is what the user means.</summary>
    public void LatchForeground()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (IsWorkWindow(window)) WorkTarget = window;
    }

    /// <summary>
    /// Link was brought to the front: the application the user came from becomes the target once they have
    /// actually worked in it. A window <paramref name="isChatWindow"/> recognizes by its title never does.
    /// </summary>
    public void LatchRecent(Func<string, bool> isChatWindow)
    {
        if (_recentWindow == WorkTarget || !IsWorkWindow(_recentWindow)) return;
        var focusedFor = TimeSpan.FromMilliseconds((_recentUntil ?? Environment.TickCount64) - _recentSince);
        if (focusedFor < SettledFocus && IsWorkWindow(WorkTarget)) return;
        if (isChatWindow(GetWindowTitle(_recentWindow))) return;
        WorkTarget = _recentWindow;
    }

    public CurrentApplicationContext? GetContext(IntPtr handle)
    {
        if (!IsExternalWindow(handle)) return null;

        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return new CurrentApplicationContext
            {
                ProcessName = process.ProcessName.Trim(),
                WindowTitle = GetWindowTitle(handle)
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public bool IsExternalWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !NativeMethods.IsWindow(handle)) return false;
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        return processId != 0 && processId != _linkProcessId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
        GC.SuppressFinalize(this);
    }

    private void OnForegroundChanged(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint threadId,
        uint eventTime) => NoteForeground(window);

    private void NoteForeground(IntPtr window)
    {
        if (IsWorkWindow(window))
        {
            _recentWindow = window;
            _recentSince = Environment.TickCount64;
            _recentUntil = null;
        }
        else
        {
            // Link, the taskbar or a system surface took the focus: time in the last app ends here.
            _recentUntil ??= Environment.TickCount64;
        }
    }

    private bool IsWorkWindow(IntPtr handle) =>
        IsExternalWindow(handle) && !ShellWindowClasses.Contains(GetClassName(handle));

    private static string GetClassName(IntPtr handle)
    {
        var name = new StringBuilder(256);
        return NativeMethods.GetClassName(handle, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var length = NativeMethods.GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;

        var title = new StringBuilder(length + 1);
        return NativeMethods.GetWindowText(handle, title, title.Capacity) > 0
            ? title.ToString().Trim()
            : string.Empty;
    }
}
