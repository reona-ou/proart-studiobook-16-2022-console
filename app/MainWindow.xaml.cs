using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace AsusFanStudio;

public sealed partial class MainWindow : Window
{
    readonly Engine _engine = new();
    readonly DispatcherTimer _timer = new();
    readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    BandEditor _cpuBand, _gpuBand;
    bool _syncing;
    bool _reallyExit;

    static readonly SolidColorBrush ChipQuiet = new(Palette.Quiet), ChipBalanced = new(Palette.Balanced),
        ChipTurbo = new(Palette.Turbo), ChipNone = new(Palette.Neutral);
    static readonly SolidColorBrush GearQuietOn = new(Palette.Soft(Palette.Quiet, 200)),
        GearBalancedOn = new(Palette.Soft(Palette.Balanced, 200)), GearTurboOn = new(Palette.Soft(Palette.Turbo, 200));
    static SolidColorBrush ChipBrush(int? m) => m switch { 1 => ChipQuiet, 2 => ChipBalanced, 3 => ChipTurbo, _ => ChipNone };
    static SolidColorBrush GearBrush(int? m) => m switch { 1 => GearQuietOn, 2 => GearBalancedOn, 3 => GearTurboOn, _ => null };

    IntPtr _trayWnd = IntPtr.Zero;
    IntPtr _hIcon = IntPtr.Zero;
    WndProcDelegate _wndProcKeepAlive;
    string _trayError;



    public MainWindow()
    {
        Hardware.Probe();
        InitializeComponent();
        SetWindowIcon();
        SizeAndPlaceWindow();
        ApplyGpuPresence();
        ShowHardware();

        _syncing = true;
        DwellBox.Value = Config.MinDwellSec;
        ConfirmUpBox.Value = Config.ConfirmUpSec;
        ConfirmDownBox.Value = Config.ConfirmDownSec;
        TimedBox.Value = Config.TimedTurboSec;
        GpuEngageBox.Value = Config.GpuEngageC;
        _syncing = false;
        _dirty = false;
        SyncSwitches();
        InitTray();

        _cpuBand = new BandEditor(BandCpu, LegendCpu,
            () => new[] { Config.QuietEnter, Config.QuietExit, Config.TurboExit, Config.TurboEnter },
            new Action<int>[] { v => Config.QuietEnter = v, v => Config.QuietExit = v, v => Config.TurboExit = v, v => Config.TurboEnter = v },
            new[] { "安静进入", "安静退出", "全速退出", "全速进入" }, MarkDirty);

        if (Hardware.HasDgpu)
            _gpuBand = new BandEditor(BandGpu, LegendGpu,
                () => new[] { Config.GpuQuietEnter, Config.GpuQuietExit, Config.GpuTurboExit, Config.GpuTurboEnter },
                new Action<int>[] { v => Config.GpuQuietEnter = v, v => Config.GpuQuietExit = v, v => Config.GpuTurboExit = v, v => Config.GpuTurboEnter = v },
                new[] { "安静进入", "安静退出", "全速退出", "全速进入" }, MarkDirty);

        _cpuBand.Rebuild();
        _gpuBand?.Rebuild();

        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (s, e) => Poll();
        _timer.Start();

        AppWindow.Closing += (s, e) =>
        {
            if (_reallyExit) { TrayRemove(); return; }
            e.Cancel = true;
            AppWindow.Hide();
        };

        Poll();

        var ca = Environment.GetCommandLineArgs();
        int at = Array.FindIndex(ca, a => a == "--timed");
        if (at > 0 && at + 1 < ca.Length && int.TryParse(ca[at + 1], out int sec))
        {
            _engine.StartTimed(Math.Clamp(sec, 10, 900));
            StatusText.Text = _engine.Status;
        }
    }

    void SyncSwitches()
    {
        _syncing = true;
        AutoSwitch.IsOn = Config.AutoEnabled;
        TurboSwitch.IsOn = Config.AllowTurbo;
        GpuSwitch.IsOn = Config.UseGpu;
        AcSwitch.IsOn = Config.OnlyOnAc;
        BootSwitch.IsOn = Autostart.Enabled();
        _syncing = false;
    }

    // 查不到独显就不该出现 GPU 的温度、功耗、转速、调节条和阈值项
    void ApplyGpuPresence()
    {
        if (Hardware.HasDgpu) return;
        GpuReadout.Visibility = Visibility.Collapsed;
        GpuBandCard.Visibility = Visibility.Collapsed;
        GpuSwitch.Visibility = Visibility.Collapsed;
        GpuEngageBox.Visibility = Visibility.Collapsed;
        FanLabel.Text = "风扇转速（CPU）";
    }

    void ShowHardware()
    {
        HardwareText.Text = string.Join("\n", Hardware.Items().Select(i => i.label + "：" + i.value));
        SourceText.Text = "读数来源：CPU 温度与占用 = 性能计数器，风扇转速 = ATKACPI EC"
            + (Hardware.HasDgpu ? "，GPU 温度与功耗 = NVML。" : "。");
    }

    bool _polling;

    // EC 的 IOCTL、性能计数器和 NVML 都是同步阻塞调用，全部挪到线程池取快照，
    // 否则被华硕后台服务抢占时整扇窗口会跟着卡住
    async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var snap = await System.Threading.Tasks.Task.Run(() => (
                cpu: Ec.CpuTemp(), gpu: Ec.GpuTemp(), gp: Ec.GpuPower(),
                usage: Ec.CpuUsage(), cf: Ec.CpuFan(), gf: Ec.GpuFan(),
                mode: Ec.ReadMode(), batt: Ec.BatteryText()));
            var cpu = snap.cpu; var gpu = snap.gpu; var cf = snap.cf; var gf = snap.gf; var mode = snap.mode;
            bool gpuAsleep = !gpu.HasValue || gf == 0;

            _engine.ExpireIfDue();
            _engine.Tick(cpu, gpu, gpuAsleep, mode);

            CpuText.Text = cpu.HasValue ? ((int)Math.Round(cpu.Value)) + "°C" : "N/A";
            CpuLoadText.Text = "占用 " + (snap.usage.HasValue ? (int)Math.Round(snap.usage.Value) + "%" : "N/A");
            GpuText.Text = gpuAsleep ? "GPU 休眠" : ((int)Math.Round(gpu.Value)) + "°C";
            GpuPowerText.Text = "功耗 " + (snap.gp.HasValue ? snap.gp.Value.ToString("F1") + " W" : "休眠中");
            FanText.Text = (cf.HasValue ? cf + "" : "N/A")
                + (Hardware.HasDgpu ? " / " + (gpuAsleep ? "休眠" : gf + "") : "") + " RPM";
            ModeText.Text = mode.HasValue ? Ec.ModeName(mode) : "接口占用";
            ModeChip.Background = ChipBrush(mode);
            Brush active = GearBrush(mode);
            GearQuiet.Background = mode == 1 ? active : null;
            GearBalanced.Background = mode == 2 ? active : null;
            GearTurbo.Background = mode == 3 ? active : null;

            var gpuGear = Engine.GpuGear(gpu, mode, gpuAsleep);
            string gpuState = !Hardware.HasDgpu ? "未检测到独显，GPU 项已隐藏"
                : gpuAsleep ? "独显休眠"
                : gpuGear.HasValue ? "GPU 判断中（≥" + Config.GpuEngageC + "°）"
                                   : "GPU 未参与（<" + Config.GpuEngageC + "°）";
            StatusText.Text = _engine.Status
                + (_dirty ? "｜有未保存改动" : "")
                + (mode.HasValue ? "｜EC 就绪" : "｜EC 被占用，稍后自动重试")
                + "｜" + gpuState
                + (_trayError is null ? "" : "｜托盘失败：" + _trayError);
            BatteryText.Text = snap.batt + (_trayError is null ? (_promoted ? "｜托盘图标已常驻" : "｜托盘图标在溢出浮窗(^)里，关窗不退出") : "｜托盘不可用");
            CountdownText.Text = _engine.Forced ? "临时全速剩余 " + _engine.ForcedLeftSec + " 秒" : "未启用";

            _cpuBand.SetMarker(cpu, "CPU");
            _gpuBand?.SetMarker(gpuAsleep ? null : gpu, "GPU");

            if (_trayWnd != IntPtr.Zero)
                TraySetTip("ProArt 性能控制台 CPU " + CpuText.Text + " " + ModeText.Text);
        }
        finally { _polling = false; }
    }

    // ---------- 控件事件 ----------
    void AutoSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Config.AutoEnabled = AutoSwitch.IsOn;
        Config.Save();
    }

    void TurboSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Config.AllowTurbo = TurboSwitch.IsOn;
        Config.Save();
        _cpuBand.Rebuild();
        _gpuBand?.Rebuild();
    }

    void GpuSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Config.UseGpu = GpuSwitch.IsOn;
        Config.Save();
    }

    void AcSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Config.OnlyOnAc = AcSwitch.IsOn;
        Config.Save();
    }

    void BootSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        try { Autostart.Set(BootSwitch.IsOn); } catch { BootSwitch.IsOn = false; }
    }

    void DwellBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || double.IsNaN(args.NewValue)) return;
        Config.MinDwellSec = (int)args.NewValue;
        MarkDirty();
    }

    void ConfirmUpBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || double.IsNaN(args.NewValue)) return;
        Config.ConfirmUpSec = (int)args.NewValue;
        MarkDirty();
    }

    void ConfirmDownBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || double.IsNaN(args.NewValue)) return;
        Config.ConfirmDownSec = (int)args.NewValue;
        MarkDirty();
    }

    void TimedBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || double.IsNaN(args.NewValue)) return;
        Config.TimedTurboSec = (int)args.NewValue;
        MarkDirty();
    }

    void GpuEngageBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing || double.IsNaN(args.NewValue)) return;
        Config.GpuEngageC = (int)args.NewValue;
        MarkDirty();
    }

    void Manual_Quiet(object sender, RoutedEventArgs e) => _engine.ManualSet(1);
    void Manual_Balanced(object sender, RoutedEventArgs e) => _engine.ManualSet(2);
    void Manual_Turbo(object sender, RoutedEventArgs e) => _engine.ManualSet(3);
    void StartTimed(object sender, RoutedEventArgs e) => _engine.StartTimed(Config.TimedTurboSec);
    void CancelTimed(object sender, RoutedEventArgs e) => _engine.CancelTimed();
    void Quick_30(object sender, RoutedEventArgs e) => Start(30);
    void Quick_60(object sender, RoutedEventArgs e) => Start(60);
    void Quick_120(object sender, RoutedEventArgs e) => Start(120);
    void Quick_300(object sender, RoutedEventArgs e) => Start(300);

    void Start(int sec)
    {
        _syncing = true;
        TimedBox.Value = sec;
        _syncing = false;
        Config.TimedTurboSec = sec;
        Config.Save();
        _engine.StartTimed(sec);
    }

    void ResetDefaults(object sender, RoutedEventArgs e)
    {
        Config.QuietEnter = 65; Config.QuietExit = 75; Config.TurboExit = 85; Config.TurboEnter = 90;
        Config.GpuQuietEnter = 50; Config.GpuQuietExit = 60; Config.GpuTurboExit = 70; Config.GpuTurboEnter = 80;
        Config.MinDwellSec = 25; Config.AllowTurbo = true; Config.AutoEnabled = true; Config.GpuEngageC = 60;
        Config.ConfirmUpSec = 3; Config.ConfirmDownSec = 15;
        SyncSwitches();
        RefreshNumericBoxes();
        _dirty = true;
        _cpuBand.Rebuild();
        _gpuBand?.Rebuild();
        StatusText.Text = "已填入默认值，点“保存设定”写入配置";
    }

    bool _dirty;

    void MarkDirty() => _dirty = true;

    void RefreshNumericBoxes()
    {
        _syncing = true;
        GpuEngageBox.Value = Config.GpuEngageC;
        ConfirmUpBox.Value = Config.ConfirmUpSec;
        ConfirmDownBox.Value = Config.ConfirmDownSec;
        DwellBox.Value = Config.MinDwellSec;
        TimedBox.Value = Config.TimedTurboSec;
        _syncing = false;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        Config.Save();
        _dirty = false;
        StatusText.Text = "设定已保存";
    }

    void Discard_Click(object sender, RoutedEventArgs e)
    {
        Config.Load();
        _dirty = false;
        SyncSwitches();
        RefreshNumericBoxes();
        _cpuBand.Rebuild();
        _gpuBand?.Rebuild();
        StatusText.Text = "已放弃未保存的改动";
    }

    void ExitApp(object sender, RoutedEventArgs e) => ExitAppNow();

    public void ShowWindow()
    {
        AppWindow.Show();
        Activate();
    }

    void ExitAppNow()
    {
        _reallyExit = true;
        RememberWindow();
        Config.Save();
        TrayRemove();
        FreeIcons();
        Application.Current.Exit();
    }

    void RememberWindow()
    {
        try
        {
            var s = AppWindow.Size;
            var q = AppWindow.Position;
            Config.WinW = s.Width; Config.WinH = s.Height; Config.WinX = q.X; Config.WinY = q.Y;
        }
        catch { }
    }

    // 按所在显示器工作区 + 实际 DPI 计算初始尺寸；有记忆则还原并夹回屏幕内
    void SizeAndPlaceWindow()
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = 1.0;
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) scale = dpi / 96.0;
        }
        catch { }

        int workL = 0, workT = 0, workW = 1920, workH = 1040;
        try
        {
            var mon = MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf<MONITORINFO>();
            if (GetMonitorInfo(mon, ref mi))
            {
                workL = mi.rcWork.Left; workT = mi.rcWork.Top;
                workW = mi.rcWork.Right - mi.rcWork.Left;
                workH = mi.rcWork.Bottom - mi.rcWork.Top;
            }
        }
        catch { }

        // 带 PerMonitorV2 清单后 AppWindow 用物理像素，直接按工作区算，不再除 scale
        double wDip = workW, hDip = workH;
        double xDip = workL, yDip = workT;

        int w, h, x, y;
        if (Config.WinW > 500 && Config.WinH > 400)
        {
            w = (int)Math.Min(Config.WinW, wDip - 30);
            h = (int)Math.Min(Config.WinH, hDip - 30);
            x = (int)Math.Clamp(Config.WinX, xDip, xDip + Math.Max(0, wDip - w));
            y = (int)Math.Clamp(Config.WinY, yDip, yDip + Math.Max(0, hDip - h));
        }
        else
        {
            w = (int)Math.Clamp(wDip * 0.62, 1280, 2100);
            h = (int)Math.Clamp(hDip * 0.88, 900, 1520);
            x = (int)(xDip + (wDip - w) / 2);
            y = (int)(yDip + (hDip - h) / 2);
        }
        try { AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h)); } catch { }
    }

    IntPtr _iconSmall = IntPtr.Zero, _iconBig = IntPtr.Zero;

    // 任务栏按钮和标题栏的图标：必须显式 WM_SETICON + 设 AppUserModelID，
    // 否则未打包 WinUI 应用会沿用宿主默认占位图
    void SetWindowIcon()
    {
        try
        {
            string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
            _iconSmall = LoadImageW(IntPtr.Zero, ico, 1 /*IMAGE_ICON*/, 16, 16, 0x10 /*LR_LOADFROMFILE*/);
            _iconBig = LoadImageW(IntPtr.Zero, ico, 1, 32, 32, 0x10);
            if (_iconSmall == IntPtr.Zero) _iconSmall = _iconBig;
            if (_iconBig == IntPtr.Zero) _iconBig = _iconSmall;
            if (_iconBig == IntPtr.Zero) return;

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SendMessageW(hwnd, WM_SETICON, new IntPtr(1 /*ICON_SMALL*/), _iconSmall);
            SendMessageW(hwnd, WM_SETICON, new IntPtr(2 /*ICON_BIG*/), _iconBig);
        }
        catch { }
    }

    void FreeIcons()
    {
        try
        {
            if (_iconSmall != IntPtr.Zero) DestroyIcon(_iconSmall);
            if (_iconBig != IntPtr.Zero && _iconBig != _iconSmall) DestroyIcon(_iconBig);
            _iconSmall = _iconBig = IntPtr.Zero;
        }
        catch { }
    }

        // ---------- 系统托盘（纯 Win32，不依赖第三方包） ----------
    void InitTray()
    {
        try
        {
            IntPtr hInst = GetModuleHandleW(null);
            _wndProcKeepAlive = WndProc;
            string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
            _hIcon = LoadImageW(IntPtr.Zero, ico, 1 /*IMAGE_ICON*/, 0, 0, 0x10 /*LR_LOADFROMFILE*/);
            if (_hIcon == IntPtr.Zero) throw new Exception("加载 tray.ico 失败 err=" + Marshal.GetLastWin32Error());

            var wc = new WNDCLASSEXW
            {
                cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProcKeepAlive,
                hInstance = hInst,
                hIcon = _hIcon,
                hIconSm = _hIcon,
                lpszClassName = "AsusFanStudioTrayWnd"
            };
            if (RegisterClassExW(ref wc) == 0) throw new Exception("注册托盘窗口类失败");

            // Win11 新托盘不认 HWND_MESSAGE，这里用真正的隐藏顶层窗口（不加 WS_VISIBLE，不进任务栏）
            _trayWnd = CreateWindowExW(0, "AsusFanStudioTrayWnd", "AsusFanStudioTray",
                0x80000000 /*WS_POPUP*/, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_trayWnd == IntPtr.Zero) throw new Exception("创建托盘宿主窗口失败 err=" + Marshal.GetLastWin32Error());

            var n = BuildNid(_hIcon, "ProArt Studiobook 16 性能控制台");
            if (!Shell_NotifyIconW(0 /*NIM_ADD*/, ref n)) throw new Exception("Shell_NotifyIcon 添加失败");

            RegisterHotKey(_trayWnd, 1, 0x0002 | 0x0008 /*CTRL|ALT*/, 0x31);
            RegisterHotKey(_trayWnd, 2, 0x0002 | 0x0008, 0x32);
            RegisterHotKey(_trayWnd, 3, 0x0002 | 0x0008, 0x33);

            // Win11 默认把新图标收进溢出浮窗：按宿主窗口句柄找到该图标的设置项，提升为常驻可见
            PromoteTrayIcon();
        }
        catch (Exception ex)
        {
            _trayError = ex.Message;
        }
    }

    bool _promoted;

    // Win11 把每个托盘图标的可见性记在 HKCU\Control Panel\NotifyIconSettings 下，
    // 用我们宿主窗口句柄匹配到那一项，写 IsPromoted=1 让它直接显示在任务栏上
    void PromoteTrayIcon()
    {
        _ = System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
        {
            try
            {
                using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\NotifyIconSettings", true);
                if (root is null) return;
                foreach (string name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name, true);
                    if (k?.GetValue("Hwnd") is not byte[] hw || hw.Length < 8) continue;
                    if (BitConverter.ToInt64(hw, 0) != _trayWnd.ToInt64()) continue;
                    k.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    _promoted = true;
                    break;
                }
            }
            catch { }
        });
    }

    NID BuildNid(IntPtr hIcon, string tip)
    {
        var n = new NID();
        n.cbSize = (uint)Marshal.SizeOf<NID>();
        n.hwnd = _trayWnd;
        n.uID = 1;
        n.uFlags = 0x1 | 0x2 | 0x4; // MESSAGE | ICON | TIP
        n.uCallbackMessage = WM_TRAY;
        n.hIcon = hIcon;
        n.szTip = tip;
        return n;
    }

    string _lastTip;

    void TraySetTip(string tip)
    {
        try
        {
            if (tip != null && tip.Length > 62) tip = tip.Substring(0, 62);
            if (tip == _lastTip) return;
            _lastTip = tip;
            var n = BuildNid(_hIcon, tip);
            Shell_NotifyIconW(1 /*NIM_MODIFY*/, ref n);
        }
        catch { }
    }

    void TrayRemove()
    {
        try
        {
            if (_trayWnd == IntPtr.Zero) return;
            var n = new NID { cbSize = (uint)Marshal.SizeOf<NID>(), hwnd = _trayWnd, uID = 1 };
            Shell_NotifyIconW(2 /*NIM_DELETE*/, ref n);
            DestroyWindow(_trayWnd);
            _trayWnd = IntPtr.Zero;
        }
        catch { }
    }

    IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            int id = (int)(long)wParam;
            if (id >= 1 && id <= 3) _ui.TryEnqueue(() => _engine.ManualSet(id));
            return IntPtr.Zero;
        }
        if (msg == WM_TRAY)
        {
            uint ev = unchecked((uint)(long)lParam);
            if (ev == WM_LBUTTONUP || ev == WM_LBUTTONDBLCLK) _ui.TryEnqueue(ShowWindow);
            else if (ev == WM_RBUTTONUP) _ui.TryEnqueue(ShowTrayMenu);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    void ShowTrayMenu()
    {
        if (_trayWnd == IntPtr.Zero) return;
        try
        {
            GetCursorPos(out POINT p);
            IntPtr menu = CreatePopupMenu();
            const uint MF_STRING = 0x3, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8;
            AppendMenuW(menu, MF_STRING, new IntPtr(1), "显示主界面");
            AppendMenuW(menu, MF_STRING, new IntPtr(2), "启用自动切档");
            if (Config.AutoEnabled) CheckMenuItem(menu, 2, MF_CHECKED);
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
            int cur = Ec.ReadMode() ?? -1;
            AppendMenuW(menu, MF_STRING, new IntPtr(11), "切到 安静");
            AppendMenuW(menu, MF_STRING, new IntPtr(12), "切到 高效");
            AppendMenuW(menu, MF_STRING, new IntPtr(13), "切到 全速");
            if (cur >= 1 && cur <= 3) CheckMenuItem(menu, (uint)(10 + cur), MF_CHECKED);
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, new IntPtr(3), "退出程序");

            SetForegroundWindow(_trayWnd);
            int cmd = TrackPopupMenuEx(menu, 0x2 | 0x100 /*RIGHTBUTTON|RETURNCMD*/, p.X, p.Y, _trayWnd, IntPtr.Zero);
            DestroyMenu(menu);
            PostMessageW(_trayWnd, 0, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case 1: ShowWindow(); break;
                case 2:
                    Config.AutoEnabled = !Config.AutoEnabled;
                    Config.Save();
                    SyncSwitches();
                    break;
                case 3: ExitAppNow(); break;
                case 11: _engine.ManualSet(1); break;
                case 12: _engine.ManualSet(2); break;
                case 13: _engine.ManualSet(3); break;
            }
        }
        catch { }
    }

    // ---------- P/Invoke ----------
    const uint WM_TRAY = 0x0400 + 1;
    const uint WM_SETICON = 0x0080;
    const uint WM_HOTKEY = 0x0312;
    const uint WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205;

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NID
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(uint dwMessage, ref NID lpData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string text);
    [DllImport("user32.dll")] static extern uint CheckMenuItem(IntPtr menu, uint id, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]
    static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
