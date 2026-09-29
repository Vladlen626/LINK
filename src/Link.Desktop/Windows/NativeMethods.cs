using System.Runtime.InteropServices;
using System.Text;

namespace Link.Desktop.Windows;

internal static class NativeMethods
{
    internal const int GwlExStyle = -20;
    internal const int WhKeyboardLl = 13;
    internal const int WmKeyDown = 0x0100;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmSysKeyUp = 0x0105;
    internal const int WmHotkey = 0x0312;
    internal const int WmNcLButtonDown = 0x00A1;
    internal const int WmNcLButtonDblClk = 0x00A3;
    internal const int WmSizing = 0x0214;
    internal const int HtCaption = 2;
    internal const int HtBottom = 15;
    internal const int HtBottomLeft = 16;
    internal const int HtBottomRight = 17;
    internal const uint EventSystemForeground = 0x0003;
    internal const uint WinEventOutOfContext = 0x0000;
    internal const int DwmwaExtendedFrameBounds = 9;
    internal const int DwmwaUseImmersiveDarkMode = 20;
    internal const int DwmwaSystemBackdropType = 38;
    internal const int DwmSystemBackdropTypeTransientWindow = 3;
    internal const int WcaAccentPolicy = 19;
    internal const int AccentDisabled = 0;
    internal const int AccentEnableBlurBehind = 3;
    internal const int AccentEnableAcrylicBlurBehind = 4;
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const uint ModNoRepeat = 0x4000;
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const int WmExitSizeMove = 0x0232;
    internal const int WmDisplayChange = 0x007E;
    internal const long WsExToolWindow = 0x00000080;
    internal const long WsExNoActivate = 0x08000000;
    internal const uint MonitorDefaultToPrimary = 0x00000001;
    internal const uint MonitorDefaultToNearest = 0x00000002;
    internal const int WaveMapper = -1;
    internal const int WaveFormatPcm = 1;
    internal const int WaveMapped = 0x0004;
    internal const int CallbackEvent = 0x00050000;
    internal const int WaveHeaderDone = 0x00000001;
    internal const int DrvQueryFunctionInstanceId = 0x0811;
    internal const int DrvQueryFunctionInstanceIdSize = 0x0812;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref DwmMargins margins);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(
        IntPtr hWnd,
        int attribute,
        out NativeRect value,
        int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("winmm.dll")]
    internal static extern int waveInGetNumDevs();

    [DllImport("winmm.dll")]
    internal static extern int waveInOpen(
        out IntPtr handle,
        int deviceId,
        ref WaveFormat format,
        IntPtr callback,
        IntPtr instance,
        int flags);

    [DllImport("winmm.dll")]
    internal static extern int waveInPrepareHeader(IntPtr handle, IntPtr header, int headerSize);

    [DllImport("winmm.dll")]
    internal static extern int waveInUnprepareHeader(IntPtr handle, IntPtr header, int headerSize);

    [DllImport("winmm.dll")]
    internal static extern int waveInAddBuffer(IntPtr handle, IntPtr header, int headerSize);

    [DllImport("winmm.dll")]
    internal static extern int waveInStart(IntPtr handle);

    [DllImport("winmm.dll")]
    internal static extern int waveInReset(IntPtr handle);

    [DllImport("winmm.dll")]
    internal static extern int waveInClose(IntPtr handle);

    // A device id cast to a handle queries the device without opening it.
    [DllImport("winmm.dll")]
    internal static extern int waveInMessage(IntPtr deviceId, int message, out int value, IntPtr unused);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    internal static extern int waveInMessage(IntPtr deviceId, int message, [Out] char[] buffer, IntPtr bufferBytes);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    internal static extern int waveInGetErrorText(int error, StringBuilder text, int textLength);

    [DllImport("winmm.dll")]
    internal static extern int waveOutOpen(
        out IntPtr handle,
        int deviceId,
        ref WaveFormat format,
        IntPtr callback,
        IntPtr instance,
        int flags);

    [DllImport("winmm.dll")]
    internal static extern int waveOutPrepareHeader(IntPtr handle, IntPtr header, int headerSize);

    [DllImport("winmm.dll")]
    internal static extern int waveOutUnprepareHeader(IntPtr handle, IntPtr header, int headerSize);

    [DllImport("winmm.dll")]
    internal static extern int waveOutWrite(IntPtr handle, IntPtr header, int headerSize);

    [DllImport("winmm.dll")]
    internal static extern int waveOutReset(IntPtr handle);

    [DllImport("winmm.dll")]
    internal static extern int waveOutClose(IntPtr handle);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    internal static extern int waveOutGetErrorText(int error, StringBuilder text, int textLength);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant value);

    internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    internal delegate void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr hwnd,
        int objectId,
        int childId,
        uint threadId,
        uint eventTime);
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat
{
    public ushort FormatTag;
    public ushort Channels;
    public int SamplesPerSecond;
    public int AverageBytesPerSecond;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort ExtraSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveHeader
{
    public IntPtr Data;
    public int BufferLength;
    public int BytesRecorded;
    public IntPtr User;
    public int Flags;
    public int Loops;
    public IntPtr Next;
    public IntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort ValueType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr Value;
    public IntPtr Value2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MonitorInfo
{
    public int Size;
    public NativeRect Monitor;
    public NativeRect Work;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DwmMargins
{
    public int Left;
    public int Right;
    public int Top;
    public int Bottom;
}
