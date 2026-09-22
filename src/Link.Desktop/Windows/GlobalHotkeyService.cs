using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using Link.Desktop.Models;

namespace Link.Desktop.Windows;

public sealed class GlobalHotkeyService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _callback;
    private readonly HashSet<int> _pressedKeys = [];
    private Dictionary<HotkeyAction, HotkeyGesture> _gestures = [];
    private IntPtr _hook;
    private bool _pushToTalkActive;

    public GlobalHotkeyService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _callback = OnKeyboardEvent;
    }

    public event Action<HotkeyAction>? Pressed;
    public event Action? PushToTalkStarted;
    public event Action? PushToTalkEnded;

    public bool IsSuspended { get; set; }

    public void Start(HotkeySettings settings)
    {
        Stop();
        _gestures = new Dictionary<HotkeyAction, HotkeyGesture>
        {
            [HotkeyAction.ShowHide] = HotkeyGesture.Parse(settings.ShowHide),
            [HotkeyAction.PushToTalk] = HotkeyGesture.Parse(settings.PushToTalk),
            [HotkeyAction.CaptureDesktop] = HotkeyGesture.Parse(settings.CaptureDesktop),
            [HotkeyAction.CaptureActiveWindow] = HotkeyGesture.Parse(settings.CaptureActiveWindow),
            [HotkeyAction.CaptureRegion] = HotkeyGesture.Parse(settings.CaptureRegion)
        };

        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WhKeyboardLl,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Global hotkey hook could not be installed.");
        }
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        _pressedKeys.Clear();
        _pushToTalkActive = false;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private IntPtr OnKeyboardEvent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && !IsSuspended)
        {
            var message = wParam.ToInt32();
            var virtualKey = Marshal.ReadInt32(lParam);
            if (message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
            {
                HandleKeyDown(virtualKey);
            }
            else if (message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp)
            {
                HandleKeyUp(virtualKey);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void HandleKeyDown(int virtualKey)
    {
        if (!_pressedKeys.Add(virtualKey))
        {
            return;
        }

        foreach (var pair in _gestures)
        {
            if (!pair.Value.Matches(virtualKey))
            {
                continue;
            }

            if (pair.Key == HotkeyAction.PushToTalk)
            {
                _pushToTalkActive = true;
                _dispatcher.BeginInvoke(() => PushToTalkStarted?.Invoke());
            }
            else
            {
                var action = pair.Key;
                _dispatcher.BeginInvoke(() => Pressed?.Invoke(action));
            }

            break;
        }
    }

    private void HandleKeyUp(int virtualKey)
    {
        _pressedKeys.Remove(virtualKey);
        if (_pushToTalkActive &&
            _gestures.TryGetValue(HotkeyAction.PushToTalk, out var gesture) &&
            gesture.VirtualKey == virtualKey)
        {
            _pushToTalkActive = false;
            _dispatcher.BeginInvoke(() => PushToTalkEnded?.Invoke());
        }
    }
}

public enum HotkeyAction
{
    ShowHide,
    PushToTalk,
    CaptureDesktop,
    CaptureActiveWindow,
    CaptureRegion
}

public sealed record HotkeyGesture(int VirtualKey, bool Control, bool Alt, bool Shift, bool Windows)
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
            parts.Any(part => part.Equals("Win", StringComparison.OrdinalIgnoreCase)));
    }

    public bool Matches(int virtualKey) =>
        VirtualKey == virtualKey &&
        Control == IsDown(0x11) &&
        Alt == IsDown(0x12) &&
        Shift == IsDown(0x10) &&
        Windows == (IsDown(0x5B) || IsDown(0x5C));

    private static bool IsDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
