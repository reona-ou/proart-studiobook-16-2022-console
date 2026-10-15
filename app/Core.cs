using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace AsusFanStudio;

internal static class Config
{
    public const int T_MIN = 30;
    public const int T_MAX = 105;

    public static bool AutoEnabled = true;
    public static bool AllowTurbo = true;

    // CPU 一组（沿用旧键名，你之前调过的值不丢）
    public static int QuietEnter = 65;
    public static int QuietExit = 75;
    public static int TurboEnter = 90;
    public static int TurboExit = 85;

    // GPU 一组
    public static int GpuQuietEnter = 50;
    public static int GpuQuietExit = 60;
    public static int GpuTurboEnter = 80;
    public static int GpuTurboExit = 70;
    public static int MinDwellSec = 25;
    public static int ConfirmUpSec = 3;      // 升档（更激进）需要条件持续满足的秒数
    public static int ConfirmDownSec = 15;    // 降档（更安静）持续满足的秒数，防抖
    public static bool UseGpu = true;
    public static bool OnlyOnAc = true;
    public static int TimedTurboSec = 120;
    public static int GpuEngageC = 60;      // GPU 达到此温度才纳入控制量

    // 窗口位置尺寸（DIP，0 表示未保存过）
    public static int WinX, WinY, WinW, WinH;

    // 配置放 %APPDATA%，装到 Program Files 也能写；旧版本写在 exe 旁边的文件仍可读
    static string NewFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProArtConsole", "fanstudio.json");
    static string OldFile => System.IO.Path.Combine(System.AppContext.BaseDirectory, "fanstudio.json");

    static void Order(int a, int b, Action<int> setA, Action<int> setB)
    {
        if (b < a) setB(a);
    }

    static void Group(int enter, Action<int> setEnter, ref int exit, Action<int> setExit, ref int tExit, Action<int> setTExit, ref int tEnter, Action<int> setTEnter)
    {
        enter = Math.Clamp(enter, T_MIN, T_MAX);
        exit = Math.Clamp(exit, T_MIN, T_MAX);
        tExit = Math.Clamp(tExit, T_MIN, T_MAX);
        tEnter = Math.Clamp(tEnter, T_MIN, T_MAX);
        // 把手之间必须留 1°C 间距：相等时"保持当前"的死区宽度为 0，档位会在阈值上下反复横跳，
        // 极端情况下全速档 t > tExit 恒成立就再也回不来
        if (exit <= enter) exit = Math.Min(enter + 1, T_MAX);
        if (tEnter <= tExit) tEnter = Math.Min(tExit + 1, T_MAX);
        if (tExit <= exit) tExit = Math.Min(exit + 1, T_MAX);
        if (tEnter <= tExit) tEnter = Math.Min(tExit + 1, T_MAX);
        setEnter(enter); setExit(exit); setTExit(tExit); setTEnter(tEnter);
    }

    public static void Clamp()
    {
        Group(QuietEnter, v => QuietEnter = v, ref QuietExit, v => QuietExit = v, ref TurboExit, v => TurboExit = v, ref TurboEnter, v => TurboEnter = v);
        Group(GpuQuietEnter, v => GpuQuietEnter = v, ref GpuQuietExit, v => GpuQuietExit = v, ref GpuTurboExit, v => GpuTurboExit = v, ref GpuTurboEnter, v => GpuTurboEnter = v);
        MinDwellSec = Math.Clamp(MinDwellSec, 0, 600);
        ConfirmUpSec = Math.Clamp(ConfirmUpSec, 0, 600);
        ConfirmDownSec = Math.Clamp(ConfirmDownSec, 0, 900);
        TimedTurboSec = Math.Clamp(TimedTurboSec, 10, 3600);
        GpuEngageC = Math.Clamp(GpuEngageC, 30, 95);
    }

    public static void Load()
    {
        try
        {
            string src = System.IO.File.Exists(NewFile) ? NewFile : (System.IO.File.Exists(OldFile) ? OldFile : null);
            if (src is null) return;
            using var d = JsonDocument.Parse(System.IO.File.ReadAllText(src));
            var r = d.RootElement;
            int Gi(string k, int def) => r.TryGetProperty(k, out var v) && v.TryGetInt32(out var o) ? o : def;
            bool Bb(string k, bool def) => r.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.True : def;
            AutoEnabled = Bb("autoEnabled", AutoEnabled);
            AllowTurbo = Bb("allowTurbo", AllowTurbo);
            QuietEnter = Gi("quietEnter", QuietEnter);
            QuietExit = Gi("quietExit", QuietExit);
            TurboEnter = Gi("turboEnter", TurboEnter);
            TurboExit = Gi("turboExit", TurboExit);
            MinDwellSec = Gi("minDwellSec", MinDwellSec);
            ConfirmUpSec = Gi("confirmUpSec", ConfirmUpSec); ConfirmDownSec = Gi("confirmDownSec", ConfirmDownSec);
            UseGpu = Bb("useGpu", UseGpu);
            OnlyOnAc = Bb("onlyOnAc", OnlyOnAc);
            TimedTurboSec = Gi("timedTurboSec", TimedTurboSec);
            GpuEngageC = Gi("gpuEngageC", GpuEngageC);
            GpuQuietEnter = Gi("gpuQuietEnter", GpuQuietEnter); GpuQuietExit = Gi("gpuQuietExit", GpuQuietExit);
            GpuTurboEnter = Gi("gpuTurboEnter", GpuTurboEnter); GpuTurboExit = Gi("gpuTurboExit", GpuTurboExit);
            WinX = Gi("winX", 0); WinY = Gi("winY", 0); WinW = Gi("winW", 0); WinH = Gi("winH", 0);
        }
        catch { }
        Clamp();
    }

    public static void Save()
    {
        Clamp();
        try
        {
            var o = new
            {
                autoEnabled = AutoEnabled, allowTurbo = AllowTurbo,
                quietEnter = QuietEnter, quietExit = QuietExit,
                turboEnter = TurboEnter, turboExit = TurboExit,
                minDwellSec = MinDwellSec, confirmUpSec = ConfirmUpSec, confirmDownSec = ConfirmDownSec, useGpu = UseGpu, onlyOnAc = OnlyOnAc,
                timedTurboSec = TimedTurboSec,
                gpuEngageC = GpuEngageC, gpuQuietEnter = GpuQuietEnter, gpuQuietExit = GpuQuietExit,
                gpuTurboEnter = GpuTurboEnter, gpuTurboExit = GpuTurboExit,
                winX = WinX, winY = WinY, winW = WinW, winH = WinH
            };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(NewFile));
            System.IO.File.WriteAllText(NewFile, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

internal static class Ec
{
    const string ATK_NAME = @"\\.\\ATKACPI";
    const uint CONTROL_CODE = 0x0022240C;
    const uint DSTS = 0x53545344;
    const uint DEVS = 0x53564544;
    const uint MODE = 0x00110019;
    const uint CPU_FAN = 0x00110013;
    const uint GPU_FAN = 0x00110014;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string n, uint a, uint s, IntPtr sec, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inB, uint inSz, byte[] outB, uint outSz, ref uint ret, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(int idx, out IntPtr handle);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr handle, int sensorType, out uint temp);

    [StructLayout(LayoutKind.Sequential)]
    struct POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusLevel; public uint BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out POWER_STATUS s);

    static readonly object _io = new();
    static IntPtr _atk = new IntPtr(-1);
    static DateTime _nextOpen = DateTime.MinValue;
    static PerformanceCounter _cpu;
    static bool _nvmlTried;
    static IntPtr _nvmlDev;

    public static string ModeName(int? m) => m switch { 0 => "默认", 1 => "安静", 2 => "高效", 3 => "全速", _ => "—" };

    static bool Open()
    {
        if (_atk != new IntPtr(-1)) return true;
        if (DateTime.Now < _nextOpen) return false;
        _nextOpen = DateTime.Now.AddSeconds(15);
        _atk = CreateFileW(ATK_NAME, 0x80000000 | 0x40000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        return _atk != new IntPtr(-1);
    }

    static void Drop()
    {
        if (_atk != new IntPtr(-1)) CloseHandle(_atk);
        _atk = new IntPtr(-1);
        _nextOpen = DateTime.Now.AddSeconds(15);
    }

    static byte[] Call(uint method, uint id, uint value)
    {
        lock (_io)
        {
            return CallLocked(method, id, value);
        }
    }

    static byte[] CallLocked(uint method, uint id, uint value)
    {
        if (!Open()) return null;
        byte[] inB = new byte[16];
        BitConverter.GetBytes(method).CopyTo(inB, 0);
        BitConverter.GetBytes(8u).CopyTo(inB, 4);
        BitConverter.GetBytes(id).CopyTo(inB, 8);
        BitConverter.GetBytes(value).CopyTo(inB, 12);
        byte[] outB = new byte[16];
        uint ret = 0;
        if (!DeviceIoControl(_atk, CONTROL_CODE, inB, (uint)inB.Length, outB, (uint)outB.Length, ref ret, IntPtr.Zero))
        {
            Drop();
            return null;
        }
        return outB;
    }

    public static int? ReadMode()
    {
        var r = Call(DSTS, MODE, 0);
        return r is null ? null : BitConverter.ToInt32(r, 0) & 0xFFFF;
    }

    public static int? SetMode(int mode)
    {
        var r = Call(DEVS, MODE, (uint)mode);
        return r is null ? null : BitConverter.ToInt32(r, 0);
    }

    public static int? CpuFan()
    {
        var r = Call(DSTS, CPU_FAN, 0);
        return r is null ? null : (BitConverter.ToInt32(r, 0) & 0xFFFF) * 100;
    }

    public static int? GpuFan()
    {
        var r = Call(DSTS, GPU_FAN, 0);
        return r is null ? null : (BitConverter.ToInt32(r, 0) & 0xFFFF) * 100;
    }

    public static double? CpuTemp()
    {
        lock (_io)
        {
            foreach (string inst in new[] { @"\_TZ.THRM", "_TZ.THRM" })
            {
                try
                {
                    _cpu ??= new PerformanceCounter("Thermal Zone Information", "Temperature", inst, readOnly: true);
                    return _cpu.NextValue() - 273.15;
                }
                catch { _cpu = null; }
            }
            return null;
        }
    }

    public static double? GpuTemp()
    {
        lock (_io)
        {
            try
            {
                if (!_nvmlTried)
                {
                    _nvmlTried = true;
                    if (nvmlInit_v2() == 0) nvmlDeviceGetHandleByIndex_v2(0, out _nvmlDev);
                }
                if (_nvmlDev == IntPtr.Zero) return null;
                return nvmlDeviceGetTemperature(_nvmlDev, 0, out uint t) == 0 ? (int)t : (double?)null;
            }
            catch { return null; }
        }
    }

    public static bool OnAc()
    {
        try { return GetSystemPowerStatus(out var s) && s.ACLineStatus != 0; }
        catch { return true; }
    }

    public static string BatteryText()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return null;
            string ac = s.ACLineStatus == 0 ? "电池供电" : "接电源";
            int pct = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;
            return pct < 0 ? ac : ac + " " + pct + "%";
        }
        catch { return null; }
    }
}

// 非对称滞回状态机 + 临时全速定时
internal sealed class Engine
{
    DateTime _lastSwitch = DateTime.MinValue;
    int? _pending;
    DateTime _pendingSince = DateTime.MinValue;
    DateTime _forceUntil = DateTime.MinValue;
    int? _preForce;

    public string Status { get; private set; } = "初始化";
    public int SwitchCount { get; private set; }
    public bool Forced => DateTime.Now < _forceUntil;
    public int ForcedLeftSec => Forced ? Math.Max(0, (int)Math.Ceiling((_forceUntil - DateTime.Now).TotalSeconds)) : 0;

    static int? Gear(double t, int? current, int qEnter, int qExit, int tExit, int tEnter)
    {
        bool turbo = Config.AllowTurbo;
        if (current == 3 && t > tExit && turbo) return 3;
        if (t <= qEnter) return 1;
        if (turbo && t >= tEnter) return 3;
        if (current == 1 && t < qExit) return 1;
        return 2;
    }

    public static int? CpuGear(double? cpu, int? current) =>
        cpu.HasValue ? Gear(cpu.Value, current, Config.QuietEnter, Config.QuietExit, Config.TurboExit, Config.TurboEnter) : null;

    // GPU 只有醒着且达到纳入阈值时才贡献档位，否则完全交给 CPU 那条
    public static int? GpuGear(double? gpu, int? current, bool asleep)
    {
        if (!Config.UseGpu || asleep || !gpu.HasValue) return null;
        if (gpu.Value < Config.GpuEngageC) return null;
        return Gear(gpu.Value, current, Config.GpuQuietEnter, Config.GpuQuietExit, Config.GpuTurboExit, Config.GpuTurboEnter);
    }

    public void Tick(double? cpu, double? gpu, bool gpuAsleep, int? current)
    {
        if (Forced)
        {
            if (current != 3) TrySet(3, "临时全速中");
            else Status = "临时全速剩余 " + ForcedLeftSec + " 秒";
            return;
        }
        if (current is not int cur)
        {
            Status = "读不到 EC 档位，稍后自动重试";
            return;
        }
        if (!Config.AutoEnabled) { Status = "自动切档已关闭（手动）"; return; }

        var cg = CpuGear(cpu, cur);
        var gg = GpuGear(gpu, cur, gpuAsleep);
        int? want;
        if (cg.HasValue && gg.HasValue) { want = cg > gg ? cg : gg; Driver = want == gg ? "GPU" : "CPU"; }
        else if (cg.HasValue) { want = cg; Driver = "CPU"; }
        else if (gg.HasValue) { want = gg; Driver = "GPU"; }
        else { want = null; Driver = "-"; }

        if (!want.HasValue || want.Value == cur)
        {
            bool wasPending = _pending is not null;
            _pending = null;
            Status = "保持 " + Ec.ModeName(cur) + (wasPending ? "（升降档请求已撤销）" : "");
            return;
        }
        if (Config.OnlyOnAc && !Ec.OnAc() && want.Value > cur) { Status = "电池供电，不升档"; return; }

        bool up = want.Value > cur;
        int need = up ? Config.ConfirmUpSec : Config.ConfirmDownSec;
        if (_pending != want)
        {
            _pending = want;
            _pendingSince = DateTime.Now;
            Status = (up ? "升档" : "降档") + "请求：" + Ec.ModeName(want.Value) + "，需持续 " + need + "s";
            return;
        }

        double held = (DateTime.Now - _pendingSince).TotalSeconds;
        if (held < need)
        {
            Status = (up ? "升档" : "降档") + "确认中 " + held.ToString("F0") + "/" + need + "s -> " + Ec.ModeName(want.Value);
            return;
        }

        double since = (DateTime.Now - _lastSwitch).TotalSeconds;
        if (since < Config.MinDwellSec)
        {
            Status = "最小停留 " + (int)(Config.MinDwellSec - since) + "s 后切到 " + Ec.ModeName(want.Value);
            return;
        }
        TrySet(want.Value, Driver);
        _pending = null;
    }

    public string Driver { get; private set; } = "-";

    void TrySet(int mode, string reason)
    {
        int? st = Ec.SetMode(mode);
        if (st is null) { Status = "EC 接口被占用，稍后自动重试"; return; }
        if (st != 1) { Status = "EC 拒绝切档，状态字=" + st; return; }
        _lastSwitch = DateTime.Now;
        SwitchCount++;
        _pending = null;
        Status = "按 " + reason + " 切到 " + Ec.ModeName(mode) + "（累计 " + SwitchCount + " 次）";
    }

    public void ManualSet(int mode)
    {
        _forceUntil = DateTime.MinValue;
        _preForce = null;
        TrySet(mode, "手动");
    }

    public void StartTimed(int seconds)
    {
        _preForce = Ec.ReadMode();
        _forceUntil = DateTime.Now.AddSeconds(seconds);
        Status = "临时全速 " + seconds + " 秒开始";
        TrySet(3, "临时");
    }

    public void CancelTimed()
    {
        if (!Forced) { Status = "没有进行中的临时全速"; return; }
        ReleaseForced("临时全速已取消");
    }

    public void ExpireIfDue()
    {
        if (_forceUntil == DateTime.MinValue || DateTime.Now < _forceUntil) return;
        ReleaseForced("定时结束");
    }

    // 退出临时全速要立刻落到当前温度对应的档位：用户点"取消"就是嫌它吵，
    // 再等降档确认秒/最小停留秒等于把误操作的后果留着
    void ReleaseForced(string reason)
    {
        _forceUntil = DateTime.MinValue;
        int? cur = Ec.ReadMode();

        if (!Config.AutoEnabled)
        {
            if (_preForce is int back && cur != back) TrySet(back, reason + "，回到临时前档位");
            else Status = reason + "（自动切档已关闭，保持 " + Ec.ModeName(cur) + "）";
            _preForce = null;
            return;
        }

        var cpu = Ec.CpuTemp();
        var gpu = Ec.GpuTemp();
        int? want = Pick(CpuGear(cpu, cur), GpuGear(gpu, cur, !gpu.HasValue || (Ec.GpuFan() ?? 0) == 0));
        _pending = null;
        _preForce = null;
        if (want is null || want == cur) { Status = reason + "，保持 " + Ec.ModeName(cur); return; }
        TrySet(want.Value, reason);
    }

    static int? Pick(int? a, int? b) => a.HasValue && b.HasValue ? (a > b ? a : b) : (a ?? b);
}

internal static class Autostart
{
    const string KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string NAME = "ProArtConsole";

    public static bool Enabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KEY);
        return k?.GetValue(NAME) != null;
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(KEY);
        if (on) k.SetValue(NAME, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
        else k.DeleteValue(NAME, false);
    }
}
