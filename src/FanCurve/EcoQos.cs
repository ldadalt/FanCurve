using System.Runtime.InteropServices;

namespace FanCurve;

internal static class EcoQos
{
    public static bool TryEnable()
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = 1,
            ControlMask = 0x1,
            StateMask = 0x1,
        };
        return SetProcessInformation(GetCurrentProcess(), 4, ref state, (uint)Marshal.SizeOf<ProcessPowerThrottlingState>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr process,
        int informationClass,
        ref ProcessPowerThrottlingState information,
        uint informationLength);
}
