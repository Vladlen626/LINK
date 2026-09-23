using System.Runtime.InteropServices;
using Link.Desktop.Models;

namespace Link.Desktop.Windows;

/// <summary>
/// Lists microphones through the Core Audio endpoint API (full names, stable ids) and maps
/// an endpoint id to the legacy waveIn device index used for recording.
/// </summary>
public static class AudioInputDevices
{
    private const int CaptureFlow = 1;
    private const int ConsoleRole = 0;
    private const int ActiveState = 1;
    private const int ReadAccess = 0;
    private const ushort StringValueType = 31;
    private static readonly PropertyKey FriendlyNameKey = new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        PropertyId = 14
    };

    public static IReadOnlyList<AudioInputDevice> GetAll()
    {
        var devices = new List<AudioInputDevice>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            enumerator.EnumAudioEndpoints(CaptureFlow, ActiveState, out var collection);
            try
            {
                collection.GetCount(out var count);
                for (var index = 0; index < count; index++)
                {
                    collection.Item(index, out var device);
                    try
                    {
                        device.GetId(out var id);
                        devices.Add(new AudioInputDevice(id, GetFriendlyName(device) ?? "Microphone"));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return devices.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static string? GetDefaultName()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            enumerator.GetDefaultAudioEndpoint(CaptureFlow, ConsoleRole, out var device);
            try
            {
                return GetFriendlyName(device);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (COMException)
        {
            // No active capture endpoint.
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Returns the waveIn index of the endpoint, or -1 when it is not connected.</summary>
    public static int FindWaveInDevice(string endpointId)
    {
        var count = NativeMethods.waveInGetNumDevs();
        for (var deviceId = 0; deviceId < count; deviceId++)
        {
            if (string.Equals(GetWaveInEndpointId(deviceId), endpointId, StringComparison.OrdinalIgnoreCase))
            {
                return deviceId;
            }
        }

        return -1;
    }

    private static string? GetWaveInEndpointId(int deviceId)
    {
        var device = new IntPtr(deviceId);
        if (NativeMethods.waveInMessage(device, NativeMethods.DrvQueryFunctionInstanceIdSize, out var bytes, IntPtr.Zero) != 0 ||
            bytes <= 2)
        {
            return null;
        }

        var buffer = new char[bytes / sizeof(char)];
        return NativeMethods.waveInMessage(device, NativeMethods.DrvQueryFunctionInstanceId, buffer, new IntPtr(bytes)) == 0
            ? new string(buffer).TrimEnd('\0')
            : null;
    }

    private static string? GetFriendlyName(IMMDevice device)
    {
        device.OpenPropertyStore(ReadAccess, out var properties);
        try
        {
            var key = FriendlyNameKey;
            properties.GetValue(ref key, out var value);
            try
            {
                return value.ValueType == StringValueType ? Marshal.PtrToStringUni(value.Value) : null;
            }
            finally
            {
                NativeMethods.PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(properties);
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    // Vtable order matters: members up to the last one used are declared in native order.
    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        void GetCount(out int count);
        void Item(int index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid interfaceId, int context, IntPtr parameters, out IntPtr instance);
        void OpenPropertyStore(int access, out IPropertyStore properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }
}
