using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Connector.JobModules;

/// <summary>Keeps every Windows child process owned by one conversion attempt.</summary>
internal sealed class WindowsJobObject : IDisposable
{
    private const uint JobObjectBasicAccountingInformationClass = 1;
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle _handle;

    private WindowsJobObject(SafeFileHandle handle) => _handle = handle;

    public static WindowsJobObject? CreateKillOnClose()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create the IFC worker job object.");
        var info = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>(); var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformationClass, memory, (uint)size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to configure the IFC worker job object.");
            return new WindowsJobObject(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    public void Assign(SafeFileHandle processHandle)
    {
        if (!AssignProcessToJobObject(_handle, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to assign the IFC worker to its job object.");
    }

    public void RequestTermination()
    {
        if (!_handle.IsClosed && !_handle.IsInvalid) _ = TerminateJobObject(_handle, 1);
    }

    public async Task TerminateAndWaitAsync()
    {
        RequestTermination();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (GetActiveProcessCount() != 0)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for the IFC worker job to terminate.");
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private uint GetActiveProcessCount()
    {
        var size = Marshal.SizeOf<JobObjectBasicAccounting>();
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(_handle, JobObjectBasicAccountingInformationClass, memory, (uint)size, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the IFC worker job object.");
            return Marshal.PtrToStructure<JobObjectBasicAccounting>(memory).ActiveProcesses;
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, uint infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, uint infoClass, IntPtr info, uint length, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformation { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public IntPtr Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation { public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed; }
}
