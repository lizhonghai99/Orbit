using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Orbit.Agents.Coder.Coder;

/// <summary>
/// Windows Job Object wrapper: kill-on-close, active process cap, and commit memory limit.
/// Non-Windows platforms: no-op (caller still relies on process-tree Kill).
/// </summary>
internal sealed class WindowsJobObject : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    private WindowsJobObject(IntPtr handle) => _handle = handle;

    public static WindowsJobObject? TryCreate(int maxProcesses = 32, long maxMemoryBytes = 2048L * 1024 * 1024)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return TryCreateWindows(maxProcesses, maxMemoryBytes);
    }

    [SupportedOSPlatform("windows")]
    private static WindowsJobObject? TryCreateWindows(int maxProcesses, long maxMemoryBytes)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                                 | JOB_OBJECT_LIMIT_ACTIVE_PROCESS
                                 | JOB_OBJECT_LIMIT_JOB_MEMORY
                                 | JOB_OBJECT_LIMIT_PROCESS_MEMORY,
                    ActiveProcessLimit = (uint)Math.Max(1, maxProcesses)
                },
                JobMemoryLimit = (UIntPtr)(ulong)Math.Max(16L * 1024 * 1024, maxMemoryBytes),
                ProcessMemoryLimit = (UIntPtr)(ulong)Math.Max(16L * 1024 * 1024, maxMemoryBytes)
            };

            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, fDeleteOld: false);
                if (!SetInformationJobObject(handle, JobObjectInfoClass.ExtendedLimitInformation, ptr, (uint)size))
                {
                    CloseHandle(handle);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            return new WindowsJobObject(handle);
        }
        catch
        {
            CloseHandle(handle);
            return null;
        }
    }

    public bool TryAssign(Process process)
    {
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero || process.HasExited)
        {
            return false;
        }

        return AssignProcessToJobObject(_handle, process.Handle);
    }

    /// <summary>Test helper: read back active-process and job-memory limits.</summary>
    internal bool TryGetConfiguredLimits(out uint activeProcessLimit, out ulong jobMemoryBytes)
    {
        activeProcessLimit = 0;
        jobMemoryBytes = 0;
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero)
        {
            return false;
        }

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                    _handle,
                    JobObjectInfoClass.ExtendedLimitInformation,
                    ptr,
                    (uint)size,
                    out _))
            {
                return false;
            }

            var info = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(ptr);
            activeProcessLimit = info.BasicLimitInformation.ActiveProcessLimit;
            jobMemoryBytes = info.JobMemoryLimit.ToUInt64();
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;
    private const uint JOB_OBJECT_LIMIT_JOB_MEMORY = 0x00000200;
    private const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoClass infoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob,
        JobObjectInfoClass infoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength,
        out uint lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
