using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Connector.JobModules;

/// <summary>Owns one worker process and every descendant created by it.</summary>
internal sealed class OwnedWorkerProcess : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private static readonly IntPtr ProcThreadAttributeHandleList = new(0x00020002);

    private readonly Process? _portableProcess;
    private readonly SafeFileHandle? _windowsProcessHandle;
    private readonly WindowsJobObject? _jobObject;

    private OwnedWorkerProcess(Process? portableProcess, SafeFileHandle? windowsProcessHandle, StreamWriter standardInput, StreamReader standardOutput, StreamReader standardError, WindowsJobObject? jobObject)
    {
        _portableProcess = portableProcess;
        _windowsProcessHandle = windowsProcessHandle;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
        _jobObject = jobObject;
    }

    public StreamWriter StandardInput { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public int ExitCode
    {
        get
        {
            if (_windowsProcessHandle is null) return _portableProcess!.ExitCode;
            if (!GetExitCodeProcess(_windowsProcessHandle, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read the IFC worker exit code.");
            return unchecked((int)exitCode);
        }
    }

    public static OwnedWorkerProcess Start(ProcessStartInfo startInfo)
    {
        if (!OperatingSystem.IsWindows()) return StartPortable(startInfo);
        var jobObject = WindowsJobObject.CreateKillOnClose()!;
        try
        {
            return StartWindowsSuspended(startInfo, jobObject);
        }
        catch
        {
            jobObject.Dispose();
            throw;
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _windowsProcessHandle is null
            ? _portableProcess!.WaitForExitAsync(cancellationToken)
            : WaitForWindowsExitAsync(_windowsProcessHandle, cancellationToken);

    public void RequestTermination()
    {
        if (_jobObject is not null) _jobObject.RequestTermination();
        else KillPortableProcessTree(_portableProcess!);
    }

    public async Task StopAsync()
    {
        if (_jobObject is not null)
        {
            await _jobObject.TerminateAndWaitAsync().ConfigureAwait(false);
        }
        else
        {
            // Non-Windows fallback: .NET terminates the exact process tree started above.
            KillPortableProcessTree(_portableProcess!);
        }

        await WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public void Dispose()
    {
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _jobObject?.Dispose();
        _windowsProcessHandle?.Dispose();
        _portableProcess?.Dispose();
    }

    private static OwnedWorkerProcess StartPortable(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
            return new OwnedWorkerProcess(process, null, process.StandardInput, process.StandardOutput, process.StandardError, null);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static OwnedWorkerProcess StartWindowsSuspended(ProcessStartInfo startInfo, WindowsJobObject jobObject)
    {
        if (startInfo.UseShellExecute || !startInfo.RedirectStandardInput || !startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
            throw new InvalidOperationException("The IFC worker requires redirected standard streams without shell execution.");
        if (!string.IsNullOrEmpty(startInfo.Arguments))
            throw new InvalidOperationException("The IFC worker must use ProcessStartInfo.ArgumentList.");

        SafeFileHandle? childStdIn = null, parentStdIn = null, parentStdOut = null, childStdOut = null, parentStdErr = null, childStdErr = null;
        SafeFileHandle? processHandle = null, threadHandle = null;
        StreamWriter? standardInput = null;
        StreamReader? standardOutput = null, standardError = null;
        IntPtr attributeList = IntPtr.Zero, inheritedHandles = IntPtr.Zero, environment = IntPtr.Zero;
        var attributeListInitialized = false;
        var processCreated = false;
        var assignedToJob = false;

        try
        {
            CreateRedirectPipe(out childStdIn, out parentStdIn, parentReads: false);
            CreateRedirectPipe(out childStdOut, out parentStdOut, parentReads: true);
            CreateRedirectPipe(out childStdErr, out parentStdErr, parentReads: true);

            var attributeBytes = IntPtr.Zero;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            if (attributeBytes == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to size the IFC worker process attribute list.");
            attributeList = Marshal.AllocHGlobal(attributeBytes);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to initialize the IFC worker process attribute list.");
            attributeListInitialized = true;

            inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(inheritedHandles, 0, childStdIn.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, childStdOut.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size * 2, childStdErr.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, inheritedHandles, new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to constrain IFC worker handle inheritance.");

            environment = BuildEnvironmentBlock(startInfo);
            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = childStdIn.DangerousGetHandle(),
                    StandardOutput = childStdOut.DangerousGetHandle(),
                    StandardError = childStdErr.DangerousGetHandle()
                },
                AttributeList = attributeList
            };
            var commandLine = BuildCommandLine(startInfo);
            var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory;
            if (!CreateProcessW(startInfo.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    CreateNoWindow | CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                    environment, workingDirectory, ref startup, out var created))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to start the IFC worker process.");

            processCreated = true;
            processHandle = new SafeFileHandle(created.Process, ownsHandle: true);
            threadHandle = new SafeFileHandle(created.Thread, ownsHandle: true);
            jobObject.Assign(processHandle);
            assignedToJob = true;

            standardInput = new StreamWriter(new FileStream(parentStdIn, FileAccess.Write, 4096, isAsync: false), startInfo.StandardInputEncoding ?? new UTF8Encoding(false)) { AutoFlush = true };
            parentStdIn = null;
            standardOutput = new StreamReader(new FileStream(parentStdOut, FileAccess.Read, 4096, isAsync: false), startInfo.StandardOutputEncoding ?? Encoding.UTF8, true);
            parentStdOut = null;
            standardError = new StreamReader(new FileStream(parentStdErr, FileAccess.Read, 4096, isAsync: false), startInfo.StandardErrorEncoding ?? Encoding.UTF8, true);
            parentStdErr = null;

            if (ResumeThread(threadHandle) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resume the IFC worker process.");

            var owned = new OwnedWorkerProcess(null, processHandle, standardInput, standardOutput, standardError, jobObject);
            processHandle = null;
            return owned;
        }
        catch
        {
            standardInput?.Dispose();
            standardOutput?.Dispose();
            standardError?.Dispose();
            if (processCreated)
            {
                if (assignedToJob) jobObject.RequestTermination();
                else if (processHandle is not null) _ = TerminateProcess(processHandle, 1);
            }
            throw;
        }
        finally
        {
            childStdIn?.Dispose();
            childStdOut?.Dispose();
            childStdErr?.Dispose();
            parentStdIn?.Dispose();
            parentStdOut?.Dispose();
            parentStdErr?.Dispose();
            threadHandle?.Dispose();
            processHandle?.Dispose();
            if (attributeListInitialized) DeleteProcThreadAttributeList(attributeList);
            if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    private static void CreateRedirectPipe(out SafeFileHandle childHandle, out SafeFileHandle parentHandle, bool parentReads)
    {
        var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!CreatePipe(out var readHandle, out var writeHandle, ref attributes, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to create IFC worker standard stream pipes.");
        childHandle = parentReads ? writeHandle : readHandle;
        parentHandle = parentReads ? readHandle : writeHandle;
        if (!SetHandleInformation(parentHandle, HandleFlagInherit, 0))
        {
            childHandle.Dispose();
            parentHandle.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to secure IFC worker standard stream pipes.");
        }
    }

    private static IntPtr BuildEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var entries = startInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}={pair.Value}");
        return Marshal.StringToHGlobalUni(string.Join('\0', entries) + "\0\0");
    }

    private static StringBuilder BuildCommandLine(ProcessStartInfo startInfo)
    {
        var commandLine = new StringBuilder(QuoteArgument(startInfo.FileName));
        foreach (var argument in startInfo.ArgumentList) commandLine.Append(' ').Append(QuoteArgument(argument));
        return commandLine;
    }

    private static string QuoteArgument(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Process arguments cannot contain null characters.", nameof(value));
        if (value.Length > 0 && value.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return value;

        var quoted = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1).Append(character);
                backslashes = 0;
                continue;
            }
            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static void KillPortableProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task WaitForWindowsExitAsync(SafeFileHandle processHandle, CancellationToken cancellationToken)
    {
        var addedReference = false;
        processHandle.DangerousAddRef(ref addedReference);
        try
        {
            await Task.Run(() =>
            {
                using var processExited = new EventWaitHandle(false, EventResetMode.ManualReset);
                processExited.SafeWaitHandle = new SafeWaitHandle(processHandle.DangerousGetHandle(), ownsHandle: false);
                if (!cancellationToken.CanBeCanceled)
                {
                    processExited.WaitOne();
                    return;
                }

                if (WaitHandle.WaitAny([processExited, cancellationToken.WaitHandle]) == 1)
                    throw new OperationCanceledException(cancellationToken);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (addedReference) processHandle.DangerousRelease();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SecurityAttributes pipeAttributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}
