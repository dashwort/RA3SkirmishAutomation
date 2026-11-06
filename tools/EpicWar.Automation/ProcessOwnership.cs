using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EpicWar.Automation;

internal static class ProcessOwnership
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size,Usage,Pid; public UIntPtr Heap; public uint Module,Threads,Parent;
        public int Priority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Name;
    }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    internal static Process WaitForEngine(Process launcher,string expectedImage,TimeSpan timeout,CancellationToken cancellationToken=default)
    {
        expectedImage=Path.GetFullPath(expectedImage);
        var timer=Stopwatch.StartNew();
        var descendants=new HashSet<uint>{(uint)launcher.Id};
        var earliest=launcher.StartTime.ToUniversalTime().AddSeconds(-1);
        while(timer.Elapsed<timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries=Snapshot();
            bool changed;
            do
            {
                changed=false;
                foreach(var item in entries)
                    if(descendants.Contains(item.Parent)&&descendants.Add(item.Pid))changed=true;
            }while(changed);
            foreach(var pid in descendants)
            {
                Process? process=null;
                try
                {
                    process=Process.GetProcessById((int)pid);
                    if(process.HasExited || process.StartTime.ToUniversalTime()<earliest)continue;
                    if(string.Equals(process.MainModule?.FileName,expectedImage,StringComparison.OrdinalIgnoreCase))return process;
                }
                catch(ArgumentException) { }
                catch(InvalidOperationException) { }
                catch(System.ComponentModel.Win32Exception) { }
                finally
                {
                    // Returned process ownership transfers to the caller.
                    if(process!=null && !string.Equals(SafePath(process),expectedImage,StringComparison.OrdinalIgnoreCase))process.Dispose();
                }
            }
            if(launcher.HasExited && descendants.Count==1)
                throw new InvalidOperationException($"RA3 launcher exited before returning an owned game process (exit {launcher.ExitCode}).");
            Thread.Sleep(100);
        }
        throw new TimeoutException("The RA3 launcher did not start an owned RA3 1.12 engine before the deadline.");
    }
    private static string? SafePath(Process p){try{return p.MainModule?.FileName;}catch{return null;}}
    private static List<ProcessEntry> Snapshot()
    {
        var handle=CreateToolhelp32Snapshot(2,0);
        if(handle==new IntPtr(-1))throw new System.ComponentModel.Win32Exception();
        try
        {
            var result=new List<ProcessEntry>();var entry=new ProcessEntry {Size=(uint)Marshal.SizeOf<ProcessEntry>(),Name=""};
            if(Process32FirstW(handle,ref entry))do{result.Add(entry);}while(Process32NextW(handle,ref entry));
            return result;
        }
        finally{CloseHandle(handle);}
    }
}
