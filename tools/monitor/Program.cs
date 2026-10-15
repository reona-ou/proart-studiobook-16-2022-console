using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Principal;
using Microsoft.Win32;

internal static class Program
{
    [STAThread]
    static void Main(string[] argv)
    {
        bool cli = argv.Contains("--once") || argv.Contains("--writetest");
        using var mutex = new Mutex(true, @"Local\AsusHwMonitor", out bool fresh);
        if (!fresh && !cli) return;
        ApplicationConfiguration.Initialize();
        if (argv.Contains("--writetest")) { WriteTest.Run(argv); return; }
        if (argv.Contains("--once")) { Sensors.DumpOnce(); return; }
        Application.Run(new MainForm());
    }
}

internal static class Config
{
    public static int CpuWarn = 92;
    public static int GpuWarn = 85;
    public static int StallRpm = 1200;
    public static int PollMs = 2000;

    public static bool AutoEnabled;
    public static int QuietBelow = 55;
    public static int TurboAbove = 90;
    public static int Hysteresis = 4;
    public static int MinDwellSec = 30;
    public static bool AllowTurbo;
    public static bool UseGpu = true;
    public static bool OnlyOnAc;

    static string File => System.IO.Path.Combine(System.AppContext.BaseDirectory, "monitor.json");

    public static void Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return;
            using var d = JsonDocument.Parse(System.IO.File.ReadAllText(File));
            var r = d.RootElement;
            int Gi(string k, int def) => r.TryGetProperty(k, out var v) && v.TryGetInt32(out var o) ? o : def;
            bool Bb(string k, bool def) => r.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.True : def;
            CpuWarn = Gi("cpuWarn", CpuWarn); GpuWarn = Gi("gpuWarn", GpuWarn);
            StallRpm = Gi("stallRpm", StallRpm); PollMs = Gi("pollMs", PollMs);
            AutoEnabled = Bb("autoEnabled", AutoEnabled); QuietBelow = Gi("quietBelow", QuietBelow);
            TurboAbove = Gi("turboAbove", TurboAbove); Hysteresis = Gi("hysteresis", Hysteresis);
            MinDwellSec = Gi("minDwellSec", MinDwellSec); AllowTurbo = Bb("allowTurbo", AllowTurbo);
            UseGpu = Bb("useGpu", UseGpu); OnlyOnAc = Bb("onlyOnAc", OnlyOnAc);
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            var o = new
            {
                cpuWarn = CpuWarn, gpuWarn = GpuWarn, stallRpm = StallRpm, pollMs = PollMs,
                autoEnabled = AutoEnabled, quietBelow = QuietBelow, turboAbove = TurboAbove,
                hysteresis = Hysteresis, minDwellSec = MinDwellSec, allowTurbo = AllowTurbo,
                useGpu = UseGpu, onlyOnAc = OnlyOnAc
            };
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

internal static class Sensors
{
    const string ATK_NAME = @"\\.\\ATKACPI";
    const uint CONTROL_CODE = 0x0022240C;
    const uint DSTS = 0x53545344;
    const uint DEVS = 0x53564544;
    const uint MODE = 0x00110019;

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

    static IntPtr _atk = new IntPtr(-1);
    static DateTime _nextOpen = DateTime.MinValue;
    static PerformanceCounter _cpu;
    static bool _nvmlTried;
    static IntPtr _nvmlDev;

    public static string ModeName(int? m) => m switch { 0 => "默认", 1 => "安静", 2 => "高效", 3 => "全速", _ => null };

    static bool Open()
    {
        if (_atk != new IntPtr(-1)) return true;
        if (DateTime.Now < _nextOpen) return false;
        _nextOpen = DateTime.Now.AddSeconds(20);
        _atk = CreateFileW(ATK_NAME, 0x80000000 | 0x40000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        return _atk != new IntPtr(-1);
    }

    static void Drop()
    {
        if (_atk != new IntPtr(-1)) CloseHandle(_atk);
        _atk = new IntPtr(-1);
        _nextOpen = DateTime.Now.AddSeconds(45);
    }

    static byte[] Call(uint method, uint id, uint value)
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

    public static int? CpuFanRpm()
    {
        var r = Call(DSTS, 0x00110013, 0);
        return r is null ? null : (BitConverter.ToInt32(r, 0) & 0xFFFF) * 100;
    }

    public static int? GpuFanRpm()
    {
        var r = Call(DSTS, 0x00110014, 0);
        return r is null ? null : (BitConverter.ToInt32(r, 0) & 0xFFFF) * 100;
    }

    public static double? CpuTemp()
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

    public static double? GpuTemp()
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

    public static bool OnAc()
    {
        try { return GetSystemPowerStatus(out var s) && s.ACLineStatus != 0; }
        catch { return true; }
    }

    public static string Battery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return null;
            string ac = s.ACLineStatus == 0 ? "电池" : "接电源";
            int pct = s.BatteryLifePercent == 255 ? -1 : s.BatteryLifePercent;
            return pct < 0 ? ac : ac + " " + pct + "%";
        }
        catch { return null; }
    }

    public static double? Control()
    {
        var c = CpuTemp();
        if (!Config.UseGpu) return c;
        var g = GpuTemp();
        if (!c.HasValue) return g;
        if (!g.HasValue) return c;
        return Math.Max(c.Value, g.Value);
    }

    public static void DumpOnce()
    {
        var sb = new System.Text.StringBuilder();
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        sb.AppendLine("elevated = " + admin);
        sb.AppendLine("cpuTemp  = " + (CpuTemp()?.ToString("F1") ?? "N/A") + " C");
        sb.AppendLine("gpuTemp  = " + (GpuTemp()?.ToString("F0") ?? "N/A") + " C");
        sb.AppendLine("cpuRpm   = " + (CpuFanRpm()?.ToString() ?? "N/A"));
        sb.AppendLine("gpuRpm   = " + (GpuFanRpm()?.ToString() ?? "N/A"));
        var m = ReadMode();
        sb.AppendLine("mode     = " + (m.HasValue ? m + " (" + ModeName(m) + ")" : "N/A"));
        sb.AppendLine("power    = " + (Battery() ?? "N/A"));
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Write(sb);
        try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "selftest.txt"), sb.ToString()); } catch { }
    }
}

// 三段阶梯 + 滞回 + 最小停留
internal sealed class AutoEngine
{
    DateTime _lastSwitch = DateTime.MinValue;

    public string LastError { get; private set; }
    public int? Target { get; private set; }

    public int? Desired(double? ctrl)
    {
        if (!Config.AutoEnabled || !ctrl.HasValue) return null;
        double t = ctrl.Value;
        if (t <= Config.QuietBelow - Config.Hysteresis) return 1;
        if (Config.AllowTurbo && t >= Config.TurboAbove - Config.Hysteresis) return 3;
        if (t >= Config.QuietBelow + Config.Hysteresis) return 2;
        return null;
    }

    public void Tick(double? ctrl, int? current)
    {
        if (!Config.AutoEnabled || current is not int cur) return;
        var want = Desired(ctrl);
        if (!want.HasValue) return;
        Target = want;
        if (want.Value == cur) return;
        if (Config.OnlyOnAc && !Sensors.OnAc() && want.Value > 2) return;
        if ((DateTime.Now - _lastSwitch).TotalSeconds < Config.MinDwellSec) return;

        int? st = Sensors.SetMode(want.Value);
        if (st is null) { LastError = "EC 接口被占用，稍后自动重试"; return; }
        if (st != 1) { LastError = "EC 拒绝切档，状态字=" + st; return; }
        LastError = null;
        _lastSwitch = DateTime.Now;
    }
}

internal static class WriteTest
{
    public static void Run(string[] argv)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (argv.Contains("CYCLE")) { Cycle(argv); return; }
        if (!argv.Contains("CONFIRM"))
        {
            Console.WriteLine("该测试会写 EC 风扇档位（等价于在 Creator Hub 里点一下档位），执行完必定恢复原档位。");
            Console.WriteLine("用法: AsusMonitor.exe --writetest CONFIRM [1|2]");
            return;
        }
        int target = argv.Length > 2 && int.TryParse(argv[2], out int t) && (t == 1 || t == 2) ? t : 1;

        var orig = Sensors.ReadMode();
        if (!orig.HasValue) { Console.WriteLine("读不到当前档位（EC 被占用），不执行写入。"); return; }
        Console.WriteLine("当前档位 = " + orig + " (" + Sensors.ModeName(orig) + ")");
        Console.WriteLine("写入前  CPU风扇=" + Sensors.CpuFanRpm() + "  GPU风扇=" + Sensors.GpuFanRpm() + "  CPU温度=" + Sensors.CpuTemp()?.ToString("F1"));

        Console.WriteLine("写入 DEVS(0x00110019, " + target + " = " + Sensors.ModeName(target) + ")");
        int? st = Sensors.SetMode(target);
        Console.WriteLine("  EC 状态字 = " + (st?.ToString() ?? "null(接口不可用)") + (st == 1 ? " -> 已接受" : ""));
        Thread.Sleep(4000);
        var back = Sensors.ReadMode();
        Console.WriteLine("  回读 = " + (back?.ToString() ?? "N/A") + " (" + Sensors.ModeName(back) + ")  生效=" + (back == target));
        Console.WriteLine("  切换后 CPU风扇=" + Sensors.CpuFanRpm() + "  GPU风扇=" + Sensors.GpuFanRpm() + "  CPU温度=" + Sensors.CpuTemp()?.ToString("F1"));

        Console.WriteLine("恢复原档位 DEVS(0x00110019, " + orig + ")");
        int? st2 = Sensors.SetMode(orig.Value);
        Console.WriteLine("  EC 状态字 = " + (st2?.ToString() ?? "null"));
        Thread.Sleep(1500);
        var fin = Sensors.ReadMode();
        Console.WriteLine("  最终 = " + (fin?.ToString() ?? "N/A") + " (" + Sensors.ModeName(fin) + ")  已恢复=" + (fin == orig));
        Console.WriteLine("  最终转速 CPU风扇=" + Sensors.CpuFanRpm() + "  GPU风扇=" + Sensors.GpuFanRpm());
    }

    // 同一恒定负载下，把三档各测一组数据，最后恢复原档位
    static void Cycle(string[] argv)
    {
        var orig = Sensors.ReadMode();
        if (!orig.HasValue) { Console.WriteLine("EC 接口不可用（被占用），取消测试。"); return; }
        Console.WriteLine("起始档位 = " + orig + " (" + Sensors.ModeName(orig) + ")");
        Console.WriteLine("方法：4 线程恒定数学负载；每档写入后稳定 6 秒，再连续采 6 次（间隔 1.2 秒）");

        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var workers = new List<Task>();
        for (int i = 0; i < 4; i++)
        {
            workers.Add(Task.Run(() =>
            {
                double acc = 0;
                while (!token.IsCancellationRequested)
                    for (int k = 0; k < 200000; k++) acc += Math.Sqrt(k);
                GC.KeepAlive(acc);
            }, token));
        }

        var report = new System.Text.StringBuilder();
        try
        {
            foreach (var (mode, name) in new[] { (1, "安静"), (2, "高效"), (3, "全速") })
            {
                int? st = Sensors.SetMode(mode);
                if (st != 1)
                {
                    string bad = name + "(档" + mode + ") EC 拒绝写入，状态=" + (st?.ToString() ?? "null");
                    Console.WriteLine(bad); report.AppendLine(bad);
                    continue;
                }
                Thread.Sleep(6000);
                var cRpm = new List<int>();
                var gRpm = new List<int>();
                double cT = 0, gT = 0;
                for (int s = 0; s < 6; s++)
                {
                    var c = Sensors.CpuFanRpm(); var g = Sensors.GpuFanRpm();
                    var ct = Sensors.CpuTemp(); var gt = Sensors.GpuTemp();
                    if (c.HasValue) cRpm.Add(c.Value);
                    if (g.HasValue) gRpm.Add(g.Value);
                    if (ct.HasValue) cT = ct.Value;
                    if (gt.HasValue) gT = gt.Value;
                    Thread.Sleep(1200);
                }
                string line = name + "(档" + mode + ")  CPU风扇 " + Stat(cRpm) + "   GPU风扇 " + Stat(gRpm) +
                              "   CPU " + cT.ToString("F1") + "°C   GPU " + gT.ToString("F0") + "°C";
                Console.WriteLine(line);
                report.AppendLine(line);
            }
        }
        finally
        {
            cts.Cancel();
            try { Task.WaitAll(workers.ToArray(), 5000); } catch { }
            int? back = Sensors.SetMode(orig.Value);
            Thread.Sleep(1500);
            var fin = Sensors.ReadMode();
            string line2 = "恢复档位 -> 写入状态=" + (back?.ToString() ?? "null") + " 回读=" + (fin?.ToString() ?? "N/A") +
                           " (" + Sensors.ModeName(fin) + ") 已恢复=" + (fin == orig);
            Console.WriteLine(line2);
            report.AppendLine(line2);
            Console.WriteLine("恢复后转速 CPU=" + Sensors.CpuFanRpm() + " GPU=" + Sensors.GpuFanRpm());
        }
        try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "mode_survey.txt"), report.ToString()); } catch { }
    }

    static string Stat(List<int> v) => v.Count == 0 ? "N/A" : v.Min() + "~" + v.Max() + " RPM（中位 " + Median(v) + "）";
    static int Median(List<int> v) { var a = v.OrderBy(x => x).ToArray(); return a[a.Length / 2]; }
}

internal sealed class MainForm : Form
{
    static readonly Font Small7 = new Font("Segoe UI", 7.5f);
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _timer = new();
    readonly List<double> _hist = new();
    readonly Panel _chart = new();
    readonly Label _text = new();
    readonly AutoEngine _engine = new();
    bool _warnedCpu, _warnedGpu, _allowClose = true;
    DateTime _lastWarn = DateTime.MinValue;

    public MainForm()
    {
        Config.Load();
        Text = "华硕硬件监控";
        ClientSize = new Size(400, 290);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;

        _chart.Dock = DockStyle.Top;
        _chart.Height = 124;
        _chart.Paint += (s, e) => Draw(e.Graphics);
        _text.Dock = DockStyle.Fill;
        _text.Font = new Font("Segoe UI", 9.5f);
        _text.Padding = new Padding(8);
        Controls.Add(_text);
        Controls.Add(_chart);

        _tray.Icon = SystemIcons.Application;
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (s, e) => Toggle());
        var autoItem = new ToolStripMenuItem("启用自动切档") { CheckOnClick = true, Checked = Config.AutoEnabled };
        autoItem.Click += (s, e) => { Config.AutoEnabled = autoItem.Checked; Config.Save(); };
        menu.Items.Add(autoItem);
        menu.Items.Add("切档与报警设置...", null, (s, e) => Settings());
        menu.Items.Add("重新检测传感器", null, (s, e) => { _hist.Clear(); Poll(); });
        var start = new ToolStripMenuItem("开机自启") { CheckOnClick = true, Checked = Autostart.Enabled() };
        start.Click += (s, e) => Autostart.Set(start.Checked);
        menu.Items.Add(start);
        menu.Items.Add("退出", null, (s, e) => { Config.Save(); _allowClose = true; _tray.Visible = false; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => Toggle();

        _timer.Interval = Config.PollMs;
        _timer.Tick += (s, e) => Poll();
        _timer.Start();
        Load += (s, e) => Poll();
        FormClosing += (s, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing && !_allowClose) { e.Cancel = true; Hide(); }
        };
    }

    void Toggle()
    {
        if (Visible) { _allowClose = false; Hide(); _allowClose = true; }
        else { Show(); BringToFront(); }
    }

    void Settings()
    {
        using var f = new Form
        {
            Text = "切档与报警",
            ClientSize = new Size(300, 400),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen
        };
        var quiet = Num("安静阈值 °C", Config.QuietBelow, 14, 0, 100);
        var turbo = Num("全速阈值 °C", Config.TurboAbove, 44, 40, 100);
        var hyst = Num("滞回 °C", Config.Hysteresis, 74, 0, 30);
        var dwell = Num("最小停留 秒", Config.MinDwellSec, 104, 5, 600);
        var cpuWarn = Num("CPU 报警 °C", Config.CpuWarn, 134, 40, 110);
        var allowTurbo = new CheckBox { Text = "允许自动进入全速档", Left = 14, Top = 170, AutoSize = true, Checked = Config.AllowTurbo };
        var useGpu = new CheckBox { Text = "控制量取 CPU/GPU 较大值", Left = 14, Top = 194, AutoSize = true, Checked = Config.UseGpu };
        var onlyAc = new CheckBox { Text = "仅接电源时才允许升档", Left = 14, Top = 218, AutoSize = true, Checked = Config.OnlyOnAc };
        var info = new Label
        {
            Left = 14, Top = 246, Width = 275, Height = 90,
            Text = "规则：控制量 ≤ 安静阈值-滞回 -> 安静\r\n      控制量 ≥ 安静阈值+滞回 -> 高效\r\n      控制量 ≥ 全速阈值-滞回 -> 全速（需勾选）\r\n两次切档之间至少间隔「最小停留」秒。"
        };
        var ok = new Button { Text = "保存", DialogResult = DialogResult.OK, Left = 105, Top = 346 };
        f.Controls.AddRange(new Control[] {
            quiet.Item1, quiet.Item2, turbo.Item1, turbo.Item2, hyst.Item1, hyst.Item2,
            dwell.Item1, dwell.Item2, cpuWarn.Item1, cpuWarn.Item2,
            allowTurbo, useGpu, onlyAc, info, ok });
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            Config.QuietBelow = (int)quiet.Item2.Value;
            Config.TurboAbove = (int)turbo.Item2.Value;
            Config.Hysteresis = (int)hyst.Item2.Value;
            Config.MinDwellSec = (int)dwell.Item2.Value;
            Config.CpuWarn = (int)cpuWarn.Item2.Value;
            Config.AllowTurbo = allowTurbo.Checked;
            Config.UseGpu = useGpu.Checked;
            Config.OnlyOnAc = onlyAc.Checked;
            if (Config.TurboAbove <= Config.QuietBelow) Config.TurboAbove = Math.Min(100, Config.QuietBelow + 10);
            Config.Save();
            _warnedCpu = _warnedGpu = false;
        }
    }

    static (Label, NumericUpDown) Num(string label, int val, int top, int min, int max)
    {
        var n = new NumericUpDown
        {
            Left = 190, Top = top, Width = 90,
            Minimum = min, Maximum = max,
            Value = Math.Min(max, Math.Max(min, val))
        };
        return (new Label { Text = label, Left = 14, Top = top + 3, AutoSize = true }, n);
    }

    void Poll()
    {
        var cpu = Sensors.CpuTemp();
        var gpu = Sensors.GpuTemp();
        var mode = Sensors.ReadMode();
        var cr = Sensors.CpuFanRpm();
        var gr = Sensors.GpuFanRpm();
        var ctrl = Sensors.Control();

        _engine.Tick(ctrl, mode);

        if (cpu.HasValue) { _hist.Add(cpu.Value); if (_hist.Count > 150) _hist.RemoveAt(0); }

        string auto = !Config.AutoEnabled ? "自动切档：关闭（手动）"
            : "自动切档：≤" + (Config.QuietBelow - Config.Hysteresis) + "°安静 / ≥" + (Config.QuietBelow + Config.Hysteresis) + "°高效" +
              (Config.AllowTurbo ? " / ≥" + (Config.TurboAbove - Config.Hysteresis) + "°全速" : "（不自动进全速）") +
              "  停留" + Config.MinDwellSec + "s  目标=" + (Sensors.ModeName(_engine.Target) ?? "-");
        _text.Text = "CPU " + F(cpu) + "   GPU " + F(gpu) + "   控制量 " + F(ctrl) + "\r\n" +
                     "风扇 CPU " + R(cr) + "  GPU " + R(gr) + "   当前档位 " + (Sensors.ModeName(mode) ?? "N/A") + "\r\n" +
                     (Sensors.Battery() ?? "") + "\r\n" + auto +
                     (_engine.LastError is null ? "" : "\r\n" + _engine.LastError);
        _tray.Text = "CPU " + F(cpu) + " | GPU " + F(gpu) + " | " + (Sensors.ModeName(mode) ?? "N/A") + " | " + R(cr) + "/" + R(gr);
        _chart.Invalidate();

        bool canWarn = (DateTime.Now - _lastWarn).TotalSeconds > 90;
        if (cpu.HasValue && cpu.Value >= Config.CpuWarn && !_warnedCpu && canWarn)
        {
            _warnedCpu = true; _lastWarn = DateTime.Now;
            _tray.ShowBalloonTip(3000, "CPU " + cpu.Value.ToString("F0") + "°C", "超过报警阈值 " + Config.CpuWarn + "°C", ToolTipIcon.Warning);
        }
        if (cpu.HasValue && cpu.Value < Config.CpuWarn - 6) _warnedCpu = false;
        if (gpu.HasValue && gpu.Value >= Config.GpuWarn && !_warnedGpu && canWarn)
        {
            _warnedGpu = true; _lastWarn = DateTime.Now;
            _tray.ShowBalloonTip(3000, "GPU " + gpu.Value.ToString("F0") + "°C", "超过报警阈值 " + Config.GpuWarn + "°C", ToolTipIcon.Warning);
        }
        if (gpu.HasValue && gpu.Value < Config.GpuWarn - 6) _warnedGpu = false;
        if (cr is > 0 && cpu > Config.CpuWarn - 2 && cr < Config.StallRpm && canWarn)
        {
            _lastWarn = DateTime.Now;
            _tray.ShowBalloonTip(4000, "CPU 风扇疑似异常", "温度 " + cpu.Value.ToString("F0") + "°C 但转速只有 " + cr + " RPM", ToolTipIcon.Error);
        }
    }

    static string F(double? v) => v.HasValue ? v.Value.ToString("F0") + "°C" : "N/A";
    static string R(int? v) => v.HasValue ? v + " RPM" : "N/A";

    void Draw(Graphics g)
    {
        g.Clear(Color.FromKnownColor(KnownColor.Control));
        if (_hist.Count < 2) { g.DrawString("等待数据…", Small7, Brushes.Gray, 8, 8); return; }
        const float lo = 30f, hi = 105f;
        int w = _chart.ClientSize.Width, h = _chart.ClientSize.Height;
        using var pen = new Pen(Color.SteelBlue, 1.6f);
        using var grid = new Pen(Color.FromArgb(60, Color.Gray));
        for (int t = 40; t <= 100; t += 20)
        {
            float y = h - (t - lo) / (hi - lo) * h;
            g.DrawLine(grid, 0, y, w, y);
            g.DrawString(t + "°", Small7, Brushes.Gray, 2, y - 8);
        }
        using (var line = new Pen(Color.FromArgb(150, Color.OrangeRed)))
        {
            float y = h - ((float)Config.QuietBelow - lo) / (hi - lo) * h;
            g.DrawLine(line, 0, y, w, y);
        }
        float step = (float)w / (_hist.Count - 1);
        PointF[] pts = _hist.Select((v, i) => new PointF(i * step, h - ((float)v - lo) / (hi - lo) * h)).ToArray();
        g.DrawLines(pen, pts);
        g.DrawString("峰值 " + _hist.Max().ToString("F0") + "°C", Small7, Brushes.DimGray, w - 90, 2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _tray.Dispose(); }
        base.Dispose(disposing);
    }
}

internal static class Autostart
{
    const string KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string NAME = "AsusHwMonitor";

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
