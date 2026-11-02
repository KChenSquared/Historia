namespace Historia.Diagnostics;

/// <summary>The few machine facts the frame benchmark records or waits on, read from Win32.</summary>
internal static class FrameBenchPlatform
{
    /// <summary>Assumed when the display's refresh rate cannot be read.</summary>
    public const int FallbackRefreshHz = 60;

    /// <summary>
    /// The primary display's current refresh rate (<c>EnumDisplaySettings</c>, current mode), and
    /// whether it fell back to <see cref="FallbackRefreshHz"/> because it could not be read.
    /// </summary>
    public static (int Hz, bool Fallback) PrimaryRefreshRate()
    {
        if (!OperatingSystem.IsWindows())
            return (FallbackRefreshHz, true);
        try
        {
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            // A null device name is the primary display.
            if (EnumDisplaySettingsW(null, EnumCurrentSettings, ref mode) && mode.dmDisplayFrequency > 1)
                return (mode.dmDisplayFrequency, false);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return (FallbackRefreshHz, true);
    }

    /// <summary>
    /// CPU time used so far by this process plus its live direct children — the git processes
    /// Historia spawns. A child that exited between samples drops out of the sum, which only ever
    /// makes a sampled window look busier, never idler, once it is gone.
    /// </summary>
    public static TimeSpan ProcessTreeCpu()
    {
        using var self = Process.GetCurrentProcess();
        var total = self.TotalProcessorTime;
        foreach (var pid in ChildProcessIds(self.Id))
        {
            try
            {
                using var child = Process.GetProcessById(pid);
                total += child.TotalProcessorTime;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited, or not ours to read.
            }
        }
        return total;
    }

    private static List<int> ChildProcessIds(int parentId)
    {
        var children = new List<int>();
        if (!OperatingSystem.IsWindows())
            return children;

        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return children;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
            {
                if (entry.th32ParentProcessID == (uint)parentId && entry.th32ProcessID != (uint)parentId)
                    children.Add((int)entry.th32ProcessID);
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return children;
    }

    private const int EnumCurrentSettings = -1;
    private const uint SnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevMode devMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
