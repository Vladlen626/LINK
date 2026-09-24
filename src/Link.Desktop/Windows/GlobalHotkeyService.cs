using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Link.Desktop.Models;

namespace Link.Desktop.Windows;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int FirstHotkeyId = 0x4C10;
    private readonly Dispatcher _dispatcher;
    private readonly IntPtr _windowHandle;
    private readonly HwndSource _source;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardCallback;
    private readonly Dictionary<int, HotkeyAction> _registeredActions = [];
    private IntPtr _keyboardHook;
    private HotkeyGesture? _pushToTalk;
    private bool _pushToTalkActive;
    private bool _isSuspended;
    private bool _disposed;

    public GlobalHotkeyService(Window owner, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _windowHandle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_windowHandle) ?? throw new InvalidOperationException("Link window source is unavailable.");
        _source.AddHook(WindowProcedure);
        _keyboardCallback = OnKeyboardEvent;
    }

    public event Action<HotkeyAction>? Pressed;
    public event Action? PushToTalkStarted;
    public event Action? PushToTalkEnded;

    public bool IsSuspended
    {
        get => _isSuspended;
        set
        {
            _isSuspended = value;
            if (value) ResetPushToTalk();
        }
    }

    public IReadOnlyList<string> Start(HotkeySettings settings)
    {
        Stop();
        var failures = new List<string>();
        var gestures = new Dictionary<HotkeyAction, HotkeyGesture>
        {
            [HotkeyAction.ShowHide] = HotkeyGesture.Parse(settings.ShowHide),
            [HotkeyAction.PushToTalk] = HotkeyGesture.Parse(settings.PushToTalk),
            [HotkeyAction.Send] = HotkeyGesture.Parse(settings.Send),
            [HotkeyAction.CaptureDesktop] = HotkeyGesture.Parse(settings.CaptureDesktop),
            [HotkeyAction.CaptureActiveWindow] = HotkeyGesture.Parse(settings.CaptureActiveWindow),
            [HotkeyAction.CaptureRegion] = HotkeyGesture.Parse(settings.CaptureRegion)
        };

        if (gestures.Values.Any(gesture => !gesture.IsSafeGlobalShortcut))
        {
            throw new InvalidOperationException("Global shortcuts must include a modifier or use a function key.");
        }

        try
        {
            var id = FirstHotkeyId;
            foreach (var pair in gestures.Where(pair => pair.Key != HotkeyAction.PushToTalk))
            {
                var gesture = pair.Value;
                if (!NativeMethods.RegisterHotKey(
                        _windowHandle,
                        id,
                        gesture.NativeModifiers | NativeMethods.ModNoRepeat,
                        (uint)gesture.VirtualKey))
                {
                    failures.Add($"{GetActionName(pair.Key)} ({gesture.DisplayText}) is already in use");
                    id++;
                    continue;
                }

                _registeredActions[id] = pair.Key;
                id++;
            }

            _pushToTalk = gestures[HotkeyAction.PushToTalk];
            _keyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhKeyboardLl,
                _keyboardCallback,
                NativeMethods.GetModuleHandle(null),
                0);

            if (_keyboardHook == IntPtr.Zero)
            {
                failures.Add(new Win32Exception(Marshal.GetLastWin32Error(), "Push-to-talk hook could not be installed.").Message);
            }

            return failures;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private static string GetActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.ShowHide => "Show/hide",
        HotkeyAction.Send => "Send prompt",
        HotkeyAction.CaptureDesktop => "Desktop capture",
        HotkeyAction.CaptureActiveWindow => "Window capture",
        HotkeyAction.CaptureRegion => "Region capture",
        _ => action.ToString()
    };

    public void Stop()
    {
        foreach (var id in _registeredActions.Keys.ToArray())
        {
            NativeMethods.UnregisterHotKey(_windowHandle, id);
        }
        _registeredActions.Clear();

        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        _pushToTalk = null;
        ResetPushToTalk();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _source.RemoveHook(WindowProcedure);
        GC.SuppressFinalize(this);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotkey && !IsSuspended && _registeredActions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }

        return IntPtr.Zero;
    }

    private IntPtr OnKeyboardEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !IsSuspended && _pushToTalk is not null)
        {
            var message = wParam.ToInt32();
            var virtualKey = Marshal.ReadInt32(lParam);
            if (message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
            {
                if (!_pushToTalkActive && _pushToTalk.Matches(virtualKey))
                {
                    _pushToTalkActive = true;
                    _dispatcher.BeginInvoke(() => PushToTalkStarted?.Invoke());
                }
            }
            else if ((message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp) &&
                     _pushToTalkActive && _pushToTalk.VirtualKey == virtualKey)
            {
                ResetPushToTalk();
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void ResetPushToTalk()
    {
        if (!_pushToTalkActive) return;
        _pushToTalkActive = false;
        if (_dispatcher.CheckAccess()) PushToTalkEnded?.Invoke();
        else _dispatcher.BeginInvoke(() => PushToTalkEnded?.Invoke());
    }
}

public enum HotkeyAction
{
    ShowHide,
    PushToTalk,
    Send,
    CaptureDesktop,
    CaptureActiveWindow,
    CaptureRegion
}

public sealed record HotkeyGesture(int VirtualKey, bool Control, bool Alt, bool Shift, bool Windows, string DisplayText)
{
    public static HotkeyGesture Parse(string value)
    {
        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !Enum.TryParse<Key>(parts[^1], true, out var key))
        {
            throw new FormatException($"Invalid hotkey: {value}");
        }

        return new HotkeyGesture(
            KeyInterop.VirtualKeyFromKey(key),
            parts.Any(part => part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)),
            parts.Any(part => part.Equals("Alt", StringComparison.OrdinalIgnoreCase)),
            parts.Any(part => part.Equals("Shift", StringComparison.OrdinalIgnoreCase)),
            parts.Any(part => part.Equals("Win", StringComparison.OrdinalIgnoreCase)),
            value);
    }

    public bool IsSafeGlobalShortcut
    {
        get
        {
            if (Control || Alt || Shift || Windows) return true;
            var key = KeyInterop.KeyFromVirtualKey(VirtualKey);
            return key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll or Key.Insert;
        }
    }

    public uint NativeModifiers =>
        (Control ? NativeMethods.ModControl : 0) |
        (Alt ? NativeMethods.ModAlt : 0) |
        (Shift ? NativeMethods.ModShift : 0) |
        (Windows ? NativeMethods.ModWin : 0);

    public bool Matches(int virtualKey) =>
        VirtualKey == virtualKey &&
        Control == IsDown(0x11) &&
        Alt == IsDown(0x12) &&
        Shift == IsDown(0x10) &&
        Windows == (IsDown(0x5B) || IsDown(0x5C));

    private static bool IsDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
