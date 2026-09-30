using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Link.Desktop.Windows;

/// <summary>
/// A Windows job that ends every process in it when it is closed, or when Link exits and Windows closes it: an agent
/// and whatever it started never outlive Link.
/// </summary>
internal sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle _job;

    public ProcessJob()
    {
        _job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = { LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose }
        };
        if (!NativeMethods.SetInformationJobObject(
                _job,
                NativeMethods.JobObjectExtendedLimitInformationClass,
                ref limits,
                Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            _job.Dispose();
            throw new Win32Exception(error);
        }
    }

    /// <summary>Puts a just-started process in the job; the processes it starts from now on join it too.</summary>
    public void Add(Process process)
    {
        if (!NativeMethods.AssignProcessToJobObject(_job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>Ends every process still in the job.</summary>
    public void Dispose() => _job.Dispose();
}
