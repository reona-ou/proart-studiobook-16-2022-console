// 只读探针：确认这台机器上 CPU / GPU 功耗到底能从哪拿到真值。
// 全程不做任何 DEVS 写入，不改变风扇档位。
//   sensprobe            全部四项
//   sensprobe nvml       只测 NVML
//   sensprobe ec         只扫 EC + 负载对照
//   sensprobe winio      只探 AsusWinIO64.dll 的只读 MSR
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

static class Nv
{
    [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] public static extern int nvmlShutdown();
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetCount_v2(out uint n);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(int idx, out IntPtr h);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetName(IntPtr h, StringBuilder buf, uint len);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperature(IntPtr h, int sensor, out uint t);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetFanSpeed(IntPtr h, out uint pct);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetFanSpeed_v2(IntPtr h, uint sensor, out uint pct);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerUsage(IntPtr h, out uint mw);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPowerManagementLimit(IntPtr h, out uint mw);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr h, out uint mw);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTotalEnergyConsumption(IntPtr h, out ulong mj);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetUtilizationRates(IntPtr h, out UTIL u);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetMemoryInfo(IntPtr h, out MEM m);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetPerformanceState(IntPtr h, out int pstate);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetClockInfo(IntPtr h, int clock, out uint mhz);
    [DllImport("nvml.dll")] public static extern int nvmlDeviceGetTemperatureSensorCount(IntPtr h, out int n);
    [DllImport("nvml.dll")] public static extern int nvmlErrorString(int err, out IntPtr s);

    [StructLayout(LayoutKind.Sequential)] public struct UTIL { public uint gpu, memory; }
    [StructLayout(LayoutKind.Sequential)] public struct MEM { public ulong total, free, used; }

    public static string Err(int r)
    {
        try
        {
            if (nvmlErrorString(r, out IntPtr p) == 0 && p != IntPtr.Zero) return Marshal.PtrToStringAnsi(p);
        }
        catch { }
        return "code " + r;
    }
}

static class EcRaw
{
    const string FILE_NAME = @"\\.\\ATKACPI";
    const uint CONTROL_CODE = 0x0022240C;
    const uint DSTS = 0x53545344;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string n, uint a, uint s, IntPtr sec, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inB, uint inSz, byte[] outB, uint outSz, ref uint ret, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static IntPtr _h = new IntPtr(-1);
    public static int IoctlFails;

    public static bool Open()
    {
        if (_h != new IntPtr(-1)) return true;
        _h = CreateFileW(FILE_NAME, 0x80000000 | 0x40000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        return _h != new IntPtr(-1);
    }

    public static void Close() { if (_h != new IntPtr(-1)) CloseHandle(_h); _h = new IntPtr(-1); }

    // 返回 16 字节原始响应；IOCTL 失败返回 null（EC 被华硕服务抢占时会短暂失败）
    public static byte[] Read(uint id, uint status, int retry = 2)
    {
        for (int attempt = 0; attempt <= retry; attempt++)
        {
            if (!Open()) return null;
            byte[] inB = new byte[16];
            BitConverter.GetBytes(DSTS).CopyTo(inB, 0);
            BitConverter.GetBytes(8u).CopyTo(inB, 4);
            BitConverter.GetBytes(id).CopyTo(inB, 8);
            BitConverter.GetBytes(status).CopyTo(inB, 12);
            byte[] outB = new byte[16];
            uint ret = 0;
            if (DeviceIoControl(_h, CONTROL_CODE, inB, (uint)inB.Length, outB, (uint)outB.Length, ref ret, IntPtr.Zero))
                return outB;
            IoctlFails++;
            Thread.Sleep(15);
        }
        return null;
    }

    public static int? Val(uint id)
    {
        var r = Read(id, 0);
        return r is null ? null : BitConverter.ToInt32(r, 0);
    }
}

static class Win
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad; public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile; public ulong ullTotalVirtual;
        public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetLogicalProcessorInformationEx(int relation, IntPtr buf, ref uint len);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string lpDevice, uint i, ref DISPLAY_DEVICE d, uint flags);
}

sealed class Probe
{
    static readonly StringBuilder Rep = new();

    static void Line(string s)
    {
        Console.WriteLine(s);
        Rep.AppendLine(s);
    }

    static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string only = argv.Length > 0 ? argv[0].ToLowerInvariant() : "";

        Line("elevated=" + new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator));
        Line("os=" + Environment.OSVersion.VersionString + "  dotnet=" + Environment.Version);
        Line("cpu=" + Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "?"));
        Line("logicalCores=" + Environment.ProcessorCount);
        var ms = new Win.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win.MEMORYSTATUSEX>() };
        if (Win.GlobalMemoryStatusEx(ref ms))
            Line("ram=" + (ms.ullTotalPhys / 1024 / 1024 / 1024) + " GB  load=" + ms.dwMemoryLoad + "%");
        Line("-- display adapters (EnumDisplayDevices, EDD_GET_DEVICE_INTERFACE_NAME) --");
        for (uint i = 0; ; i++)
        {
            var d = new Win.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<Win.DISPLAY_DEVICE>() };
            if (!Win.EnumDisplayDevices(null, i, ref d, 0x00000001)) break;
            Line("  [" + i + "] " + d.DeviceString + " | flags=0x" + d.StateFlags.ToString("X") + " | " + d.DeviceID);
        }

        if (only is "" or "nvml") { Nvml(); }
        if (only is "" or "ec") { Ec(); }
        if (only is "" or "winio") { WinIo.Run(Line, argv.Contains("call")); }

        try
        {
            string f = System.IO.Path.Combine(AppContext.BaseDirectory, "sensprobe.txt");
            System.IO.File.WriteAllText(f, Rep.ToString());
            Console.WriteLine("\nreport -> " + f);
        }
        catch { }
        return 0;
    }

    static void Nvml()
    {
        Line("\n== NVML（GPU 侧唯一免权限的真实来源） ==");
        int ini = Nv.nvmlInit_v2();
        Line("nvmlInit_v2 = " + Nv.Err(ini));
        if (ini != 0) return;
        Nv.nvmlDeviceGetCount_v2(out uint n);
        Line("deviceCount = " + n);
        for (int i = 0; i < (int)n; i++)
        {
            if (Nv.nvmlDeviceGetHandleByIndex_v2(i, out IntPtr h) != 0) { Line("  dev" + i + ": no handle"); continue; }
            var sb = new StringBuilder(96);
            Nv.nvmlDeviceGetName(h, sb, 96);
            Line("  dev" + i + " name = " + sb);
            Report("    temp GPU", () => { int r = Nv.nvmlDeviceGetTemperature(h, 0, out uint t); return (r, t + " C"); });
            Report("    temp MEMORY", () => { int r = Nv.nvmlDeviceGetTemperature(h, 1, out uint t); return (r, t + " C"); });
            Report("    sensorCount", () => { int r = Nv.nvmlDeviceGetTemperatureSensorCount(h, out int c); return (r, "" + c); });
            Report("    powerUsage", () => { int r = Nv.nvmlDeviceGetPowerUsage(h, out uint mw); return (r, mw / 1000.0 + " W"); });
            Report("    powerLimit", () => { int r = Nv.nvmlDeviceGetPowerManagementLimit(h, out uint mw); return (r, mw / 1000.0 + " W"); });
            Report("    enforcedLimit", () => { int r = Nv.nvmlDeviceGetEnforcedPowerLimit(h, out uint mw); return (r, mw / 1000.0 + " W"); });
            Report("    totalEnergy", () => { int r = Nv.nvmlDeviceGetTotalEnergyConsumption(h, out ulong mj); return (r, mj / 1000.0 + " J"); });
            Report("    fanSpeed(v1)", () => { int r = Nv.nvmlDeviceGetFanSpeed(h, out uint p); return (r, p + " %"); });
            Report("    fanSpeed(v2 s0)", () => { int r = Nv.nvmlDeviceGetFanSpeed_v2(h, 0, out uint p); return (r, p + " %"); });
            Report("    fanSpeed(v2 s1)", () => { int r = Nv.nvmlDeviceGetFanSpeed_v2(h, 1, out uint p); return (r, p + " %"); });
            Report("    utilization", () =>
            {
                int r = Nv.nvmlDeviceGetUtilizationRates(h, out Nv.UTIL u);
                return (r, "gpu " + u.gpu + "% mem " + u.memory + "%");
            });
            Report("    memory", () =>
            {
                int r = Nv.nvmlDeviceGetMemoryInfo(h, out Nv.MEM m);
                return (r, m.used / 1048576 + " / " + m.total / 1048576 + " MiB");
            });
            Report("    pstate", () => { int r = Nv.nvmlDeviceGetPerformanceState(h, out int p); return (r, "P" + p); });
            Report("    clock SM", () => { int r = Nv.nvmlDeviceGetClockInfo(h, 1, out uint m); return (r, m + " MHz"); });
            Report("    clock MEM", () => { int r = Nv.nvmlDeviceGetClockInfo(h, 2, out uint m); return (r, m + " MHz"); });
        }
        Nv.nvmlShutdown();
    }

    static void Report(string label, Func<(int, string)> f)
    {
        try
        {
            var (r, v) = f();
            Line(label + " = " + (r == 0 ? v : "FAIL(" + Nv.Err(r) + ")"));
        }
        catch (Exception ex) { Line(label + " -> " + ex.GetType().Name + " " + ex.Message); }
    }

    // EC 扫描：先看哪些设备 ID 应答，再在 CPU 满载/空闲两种状态下比较，
    // 找出随负载明显上升的通道（若存在，就是免权限的 CPU 功率读数）
    static void Ec()
    {
        Line("\n== ATKACPI EC 扫描（只读 DSTS，不写） ==");
        if (!EcRaw.Open()) { Line("打开 \\\\.\\ATKACPI 失败"); return; }

        var ranges = new (uint from, uint to, string tag)[]
        {
            (0x00110000, 0x00110060, "fan/mode"),
            (0x00120000, 0x00120100, "sensors/power"),
            (0x00090000, 0x00090040, "mode2"),
        };

        var ids = new List<uint>();
        var baseVal = new Dictionary<uint, int>();
        foreach (var (from, to, tag) in ranges)
        {
            int answered = 0;
            for (uint id = from; id <= to; id++)
            {
                var r = EcRaw.Read(id, 0, retry: 0);
                if (r is null) continue;
                answered++;
                ids.Add(id);
                baseVal[id] = BitConverter.ToInt32(r, 0);
            }
            Line("  " + tag + " 0x" + from.ToString("X8") + "-0x" + to.ToString("X8") + " 应答 " + answered + " 个");
        }
        Line("  IOCTL 失败次数=" + EcRaw.IoctlFails + "（华硕后台服务占用 EC 时会失败）");

        Line("\n  应答明细（静态读三次，值稳定的先滤掉）");
        foreach (uint id in ids)
        {
            var vs = new List<int>();
            for (int k = 0; k < 3; k++)
            {
                var v = EcRaw.Val(id);
                if (v.HasValue) vs.Add(v.Value);
                Thread.Sleep(120);
            }
            string raw = BitConverter.ToString(EcRaw.Read(id, 0) ?? new byte[16]);
            Line(string.Format("    0x{0:X8} d0={1,7} d1={2,7} raw={3}", id,
                vs.Count > 0 ? vs[0] : int.MinValue,
                BitConverter.ToInt32(EcRaw.Read(id, 0) ?? new byte[16], 4), raw));
        }

        Line("\n== 负载对照：8 线程满载 ~14 秒，看哪些通道随 CPU 功率上升 ==");
        var watch = new Dictionary<uint, (int idle, int load, int back)>();
        foreach (uint id in ids) watch[id] = (baseVal[id], 0, 0);

        void Sample(string phase)
        {
            foreach (uint id in ids)
            {
                int? v = EcRaw.Val(id);
                if (!v.HasValue) continue;      // EC 被占用时单个失败，不影响其余通道
                var t = watch[id];
                if (phase == "load") watch[id] = (t.idle, v.Value, t.back);
                else watch[id] = (t.idle, t.load, v.Value);
            }
        }

        Sample("idle");
        using (var cts = new CancellationTokenSource())
        {
            var token = cts.Token;
            var workers = new List<Task>();
            for (int i = 0; i < 8; i++)
                workers.Add(Task.Run(() => { double a = 0; while (!token.IsCancellationRequested) for (int k = 0; k < 200000; k++) a += Math.Sqrt(k); GC.KeepAlive(a); }, token));

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 14) { Sample("load"); Thread.Sleep(1200); }
            cts.Cancel();
            try { Task.WaitAll(workers.ToArray(), 4000); } catch { }
        }
        Thread.Sleep(4000);
        Sample("back");
        Thread.Sleep(3000);
        Sample("back");

        foreach (uint id in ids)
        {
            var (idle, load, back) = watch[id];
            int d = load - Math.Max(idle, back);
            Line(string.Format("    0x{0:X8} idle={1,10} load={2,10} back={3,10}  Δload={4,8}{5}",
                id, idle, load, back, d, d > 2 ? "   <-- 随 CPU 负载上升" : ""));
        }
        EcRaw.Close();
        Line("  done（未发出任何 DEVS 写调用）");
    }
}
