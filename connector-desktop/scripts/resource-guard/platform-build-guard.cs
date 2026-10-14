using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PlatformBuild {
    // Windows 10+: atomic Job List assignment prevents an unowned child even if
    // the supervisor is killed during CreateProcess. No breakaway is allowed.
    public static class Guard {
        public const string MutexName = @"Global\Platform.HeavyBuild.v1";
        const ulong GiB = 1024UL * 1024 * 1024;
        [StructLayout(LayoutKind.Sequential)] struct Security { public int Length; public IntPtr Descriptor; public int Inherit; }
        [StructLayout(LayoutKind.Sequential)] public struct Memory {
            public uint Length, Load;
            public ulong TotalPhysical, AvailablePhysical, TotalCommit, AvailableCommit, TotalVirtual, AvailableVirtual, AvailableExtended;
        }
        [StructLayout(LayoutKind.Sequential)] struct BasicLimits {
            public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet;
            public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] struct Limits {
            public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [StructLayout(LayoutKind.Sequential)] struct Cpu { public uint Flags, Rate; }
        [StructLayout(LayoutKind.Sequential)] struct Accounting {
            public long UserTime, KernelTime, PeriodUser, PeriodKernel;
            public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Startup {
            public int Size; public string Reserved, Desktop, Title; public uint X,Y,XSize,YSize,XChars,YChars,Fill,Flags;
            public short Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] struct StartupEx { public Startup Info; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr Process, Thread; public uint Id, ThreadId; }
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GlobalMemoryStatusEx(ref Memory m);
        [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text,uint revision,out IntPtr descriptor,out uint size);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateMutexEx(ref Security security,string name,uint flags,uint access);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReleaseMutex(IntPtr mutex);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateJobObject(IntPtr security,string name);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr OpenJobObject(uint access,bool inherit,string name);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool result);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool SetInformationJobObject(IntPtr job,int kind,IntPtr data,uint size);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int kind,out Accounting data,uint size,IntPtr returned);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int kind,out Limits data,uint size,IntPtr returned);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateJobObject(IntPtr job,uint code);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
        [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,bool inherit,uint flags,IntPtr environment,string directory,ref StartupEx startup,out ProcessInfo info);
        [DllImport("kernel32.dll", SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
        [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int kind);
        static void Check(bool ok,string operation) { if(!ok) throw new Win32Exception(Marshal.GetLastWin32Error(),operation); }
        static void Set<T>(IntPtr job,int kind,T value) {
            int size=Marshal.SizeOf(typeof(T)); IntPtr p=Marshal.AllocHGlobal(size);
            try { Marshal.StructureToPtr(value,p,false); Check(SetInformationJobObject(job,kind,p,(uint)size),"Set job limits"); }
            finally { Marshal.FreeHGlobal(p); }
        }
        public static Memory ReadMemory() { var m=new Memory(); m.Length=(uint)Marshal.SizeOf(typeof(Memory)); Check(GlobalMemoryStatusEx(ref m),"Read memory"); return m; }
        public static bool IsProtected() {
            string name=Environment.GetEnvironmentVariable("PLATFORM_BUILD_JOB");
            if(name==null || !name.StartsWith(@"Local\Platform.Build.")) return false;
            IntPtr job=OpenJobObject(4,false,name); if(job==IntPtr.Zero) return false;
            try { bool inside; Check(IsProcessInJob(GetCurrentProcess(),job,out inside),"Check build membership"); return inside; }
            finally { CloseHandle(job); }
        }
        public static string Quote(string value) {
            // CommandLineToArgvW / CRT escaping; no shell interpolation.
            var b=new StringBuilder("\""); int slashes=0;
            foreach(char c in value) {
                if(c=='\\') { slashes++; continue; }
                if(c=='\"') { b.Append('\\',slashes*2+1); b.Append(c); }
                else { b.Append('\\',slashes); b.Append(c); }
                slashes=0;
            }
            b.Append('\\',slashes*2); return b.Append('"').ToString();
        }
        static void Log(string message) { Console.WriteLine("BUILD_GUARD utc="+DateTime.UtcNow.ToString("O")+" "+message); }
        static bool Cancelled(string path) { return !String.IsNullOrEmpty(path) && File.Exists(path); }
        static uint Active(IntPtr job) {
            Accounting a; Check(QueryInformationJobObject(job,1,out a,(uint)Marshal.SizeOf(typeof(Accounting)),IntPtr.Zero),"Query build processes"); return a.ActiveProcesses;
        }
        public static int Run(string file,string[] args,string directory,string label,int waitSeconds,string cancelFile) {
            return RunCore(file,args,directory,label,waitSeconds,cancelFile,8*GiB,6*GiB,35,MutexName,ReadMemory,true);
        }
        public static ulong AdmittedBudget(Memory m) {
            // Reserve and early-stop margin are never loaned to the build.
            ulong held=6*GiB+GiB/2;
            if(m.AvailablePhysical<held+6*GiB) return 0;
            ulong available=((m.AvailablePhysical-held)/(1024*1024))*(1024*1024);
            ulong budget=Math.Min(8*GiB,available);
            return m.AvailableCommit>=budget+4*GiB ? budget : 0;
        }
        // Explicit parameters permit small, deterministic integration tests without
        // allocating real pressure on the workstation. Production entry uses Run.
        public static int RunWithPolicy(string file,string[] args,string directory,string label,int waitSeconds,string cancelFile,
            ulong budget,ulong reserve,uint cpuPercent,string mutexName,Func<Memory> sample) {
            return RunCore(file,args,directory,label,waitSeconds,cancelFile,budget,reserve,cpuPercent,mutexName,sample,false);
        }
        static int RunCore(string file,string[] args,string directory,string label,int waitSeconds,string cancelFile,
            ulong budget,ulong reserve,uint cpuPercent,string mutexName,Func<Memory> sample,bool adaptive) {
            if(budget==0 || reserve==0 || cpuPercent<1 || cpuPercent>100 || waitSeconds<0) throw new ArgumentOutOfRangeException("policy");
            IntPtr descriptor=IntPtr.Zero, mutex=IntPtr.Zero, job=IntPtr.Zero, list=IntPtr.Zero, jobSlot=IntPtr.Zero;
            bool locked=false, initialized=false; ProcessInfo child=new ProcessInfo();
            string previousJob=Environment.GetEnvironmentVariable("PLATFORM_BUILD_JOB");
            string previousNode=Environment.GetEnvironmentVariable("NODE_OPTIONS");
            var timer=Stopwatch.StartNew(); long lastLog=-60000;
            ulong minimumFree=ulong.MaxValue;
            try {
                uint ignored;
                // AU may wait/release only. Admin/SYSTEM retain full control.
                Check(ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;0x00100001;;;AU)(A;;GA;;;SY)(A;;GA;;;BA)",1,out descriptor,out ignored),"Mutex security");
                var security=new Security { Length=Marshal.SizeOf(typeof(Security)), Descriptor=descriptor };
                mutex=CreateMutexEx(ref security,mutexName,0,0x00100001); Check(mutex!=IntPtr.Zero,"Open machine build lock");
                for(;;) {
                    if(Cancelled(cancelFile)) { Log("cancelled-before-start"); return 75; }
                    if(!locked) {
                        uint state=WaitForSingleObject(mutex,500);
                        if(state==0 || state==0x80) locked=true;
                        else if(state!=258) Check(false,"Wait machine build lock");
                    }
                    var m=sample();
                    if(locked) {
                        if(adaptive) { ulong selected=AdmittedBudget(m); if(selected!=0) { budget=selected; break; } }
                        else if(m.AvailablePhysical>=reserve+budget && m.AvailableCommit>=budget+4*GiB) break;
                    }
                    if(timer.ElapsedMilliseconds-lastLog>=30000) {
                        Log("waiting label="+label+" reason="+(locked?"memory":"another-build")+" freeGiB="+(m.AvailablePhysical/(double)GiB).ToString("F2",System.Globalization.CultureInfo.InvariantCulture)); lastLog=timer.ElapsedMilliseconds;
                    }
                    if(timer.Elapsed.TotalSeconds>=waitSeconds) { Log("admission-timeout no-child-started"); return 75; }
                    if(locked) Thread.Sleep(500);
                }
                string jobName=@"Local\Platform.Build."+Guid.NewGuid().ToString("N");
                job=CreateJobObject(IntPtr.Zero,jobName); Check(job!=IntPtr.Zero,"Create build job");
                var limits=new Limits();
                limits.Basic.Flags=0x2000|0x200|0x20; // kill-on-close, aggregate committed memory, priority
                limits.Basic.Priority=0x4000; // below normal
                limits.JobMemory=new UIntPtr(budget); Set(job,9,limits);
                Set(job,15,new Cpu { Flags=1|4, Rate=cpuPercent*100 });
                IntPtr size=IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
                list=Marshal.AllocHGlobal(size); Check(InitializeProcThreadAttributeList(list,1,0,ref size),"Initialize attributes"); initialized=true;
                jobSlot=Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobSlot,job);
                Check(UpdateProcThreadAttribute(list,0,new IntPtr(0x2000D),jobSlot,new IntPtr(IntPtr.Size),IntPtr.Zero,IntPtr.Zero),"Assign atomic build job");
                var startup=new StartupEx(); startup.Info.Size=Marshal.SizeOf(typeof(StartupEx)); startup.Attributes=list;
                startup.Info.Flags=0x100; startup.Info.Input=GetStdHandle(-10); startup.Info.Output=GetStdHandle(-11); startup.Info.Error=GetStdHandle(-12);
                Environment.SetEnvironmentVariable("PLATFORM_BUILD_JOB",jobName);
                // Leave at least one GiB inside the aggregate cap for PowerShell,
                // npm and native allocations outside the V8 managed heap.
                ulong heapMiB=budget>GiB ? Math.Min(6144UL,(budget-GiB)/(1024*1024)) : 256UL;
                Environment.SetEnvironmentVariable("NODE_OPTIONS","--max-old-space-size="+heapMiB);
                var command=new StringBuilder(Quote(file)); foreach(var arg in args) command.Append(" ").Append(Quote(arg));
                Check(CreateProcess(file,command,IntPtr.Zero,IntPtr.Zero,true,0x80000|0x4|0x08000000,IntPtr.Zero,directory,ref startup,out child),"Create protected build");
                Check(ResumeThread(child.Thread)!=uint.MaxValue,"Resume protected build");
                Log("started label="+label+" pid="+child.Id+" job="+jobName+" budgetGiB="+(budget/(double)GiB)+" reserveGiB="+(reserve/(double)GiB)+" cpuPercent="+cpuPercent);
                lastLog=-60000;
                for(;;) {
                    var m=sample(); minimumFree=Math.Min(minimumFree,m.AvailablePhysical);
                    // 512 MiB early-stop margin above the six-GiB reserve.
                    if(m.AvailablePhysical<reserve+GiB/2 || m.AvailableCommit<4*GiB || Cancelled(cancelFile)) {
                        Log("stopping reason="+(Cancelled(cancelFile)?"cancel-file":"memory-reserve")+" freeGiB="+(m.AvailablePhysical/(double)GiB));
                        Check(TerminateJobObject(job,75),"Stop protected build"); return 75;
                    }
                    uint state=WaitForSingleObject(child.Process,500);
                    if(state==0) {
                        uint code; Check(GetExitCodeProcess(child.Process,out code),"Read build exit");
                        Log("finished exit="+code+" minimumFreeGiB="+(minimumFree/(double)GiB)); return unchecked((int)code);
                    }
                    if(state!=258) Check(false,"Wait protected build");
                    if(timer.ElapsedMilliseconds-lastLog>=30000) { Log("running label="+label+" freeGiB="+(m.AvailablePhysical/(double)GiB)); lastLog=timer.ElapsedMilliseconds; }
                }
            } finally {
                Environment.SetEnvironmentVariable("PLATFORM_BUILD_JOB",previousJob);
                Environment.SetEnvironmentVariable("NODE_OPTIONS",previousNode);
                // Keep the machine lock until all descendants (including daemons)
                // have drained, even after the root exits successfully.
                try {
                    if(job!=IntPtr.Zero) {
                        Limits observed;
                        if(QueryInformationJobObject(job,9,out observed,(uint)Marshal.SizeOf(typeof(Limits)),IntPtr.Zero))
                            Log("peakJobGiB="+(observed.PeakJobMemory.ToUInt64()/(double)GiB).ToString("F3",System.Globalization.CultureInfo.InvariantCulture));
                        Check(TerminateJobObject(job,75),"Drain protected build");
                        while(Active(job)!=0) Thread.Sleep(50);
                    }
                } finally {
                    if(child.Thread!=IntPtr.Zero) CloseHandle(child.Thread);
                    if(child.Process!=IntPtr.Zero) CloseHandle(child.Process);
                    if(initialized) DeleteProcThreadAttributeList(list);
                    if(list!=IntPtr.Zero) Marshal.FreeHGlobal(list);
                    if(jobSlot!=IntPtr.Zero) Marshal.FreeHGlobal(jobSlot);
                    if(job!=IntPtr.Zero) CloseHandle(job);
                    if(locked) ReleaseMutex(mutex);
                    if(mutex!=IntPtr.Zero) CloseHandle(mutex);
                    if(descriptor!=IntPtr.Zero) LocalFree(descriptor);
                }
            }
        }
    }
}
