using System.Runtime.InteropServices;

namespace FanCurve.Core;

/// <summary>
/// Bytes privados do processo atual. Não usa <see cref="Process"/>:
/// no Windows essas propriedades enumeram todos os processos.
/// </summary>
public static class PrivateBytes
{
    public static long Get()
    {
        var counters = new ProcessMemoryCountersEx
        {
            cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>(),
        };
        if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb))
        {
            throw new IOException("GetProcessMemoryInfo falhou.", Marshal.GetLastWin32Error());
        }

        return (long)counters.PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr process,
        ref ProcessMemoryCountersEx counters,
        uint size);
}
