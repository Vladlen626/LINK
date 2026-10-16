using System.Runtime.InteropServices;

namespace Link.Desktop.Windows;

/// <summary>How long the user has not touched this computer's keyboard or mouse.</summary>
internal static class UserIdle
{
    public static TimeSpan Time()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // Both are milliseconds since startup in 32 bits; the difference survives their wrap after 49 days.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }
}
