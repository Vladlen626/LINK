using System.Diagnostics;
using System.Text;
using Link.Desktop.Models;

namespace Link.Desktop.Windows;

public sealed class CurrentApplicationService
{
    private readonly int _linkProcessId = Environment.ProcessId;

    public IntPtr GetExternalForegroundWindow()
    {
        var handle = NativeMethods.GetForegroundWindow();
        return IsExternalWindow(handle) ? handle : IntPtr.Zero;
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
