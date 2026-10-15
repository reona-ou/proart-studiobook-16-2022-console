// WinIO 探测：枚举 AsusWinIO64.dll 导出，尝试只读 MSR（RAPL 能量计数器）算出 CPU 封装功率。
// 安全约束：这条通道能任意写 MSR，所以只枚举导出；只有名字明确是「读」的导出
// （Get/Read 前缀 + MSR/Power/Energy 等，且不含 Set/Write/Update/Program）才会被调用。
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

static class Pe
{
    // 手工解析 PE 导出表，拿到函数名（.NET 没有托管的导出枚举 API）
    public static string[] Exports(IntPtr h)
    {
        int lfanew = Marshal.ReadInt32(h, 0x3C);
        IntPtr nt = IntPtr.Add(h, lfanew);
        ushort numSections = (ushort)Marshal.ReadInt16(nt, 6);
        int optSize = Marshal.ReadInt16(nt, 20);
        IntPtr opt = IntPtr.Add(nt, 24);
        ushort magic = (ushort)Marshal.ReadInt16(opt, 0);
        int ddOff = magic == 0x20B ? 112 : 96;              // DataDirectory 起始偏移
        uint expRva = (uint)Marshal.ReadInt32(IntPtr.Add(opt, ddOff));
        uint expSize = (uint)Marshal.ReadInt32(IntPtr.Add(opt, ddOff + 4));

        var secs = new (uint va, uint vs, uint raw)[numSections];
        IntPtr sh = IntPtr.Add(opt, optSize);
        for (int i = 0; i < numSections; i++)
        {
            IntPtr s = IntPtr.Add(sh, i * 40);
            secs[i] = ((uint)Marshal.ReadInt32(s, 12), (uint)Marshal.ReadInt32(s, 8), (uint)Marshal.ReadInt32(s, 20));
        }
        uint Off(uint rva)
        {
            foreach (var s in secs) if (rva >= s.va && rva < s.va + Math.Max(s.vs, 1u)) return s.raw + (rva - s.va);
            return uint.MaxValue;
        }

        uint eoff = Off(expRva);
        if (eoff == uint.MaxValue || expSize == 0) return Array.Empty<string>();
        IntPtr exp = IntPtr.Add(h, (int)eoff);
        int count = Marshal.ReadInt32(exp, 24);
        uint namesRva = (uint)Marshal.ReadInt32(exp, 32);
        uint noff = Off(namesRva);
        if (noff == uint.MaxValue) return Array.Empty<string>();

        var list = new List<string>();
        for (int i = 0; i < count; i++)
        {
            uint nRva = (uint)Marshal.ReadInt32(IntPtr.Add(h, (int)noff + i * 4));
            uint o = Off(nRva);
            if (o != uint.MaxValue) list.Add(Marshal.PtrToStringAnsi(IntPtr.Add(h, (int)o)) ?? "?");
        }
        return list.ToArray();
    }
}

static class WinIo
{
    static Action<string> _line = Console.WriteLine;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);

    const uint MSR_UNITS = 0x606;       // RAPL 能量单位/比例
    const uint MSR_PKG = 0x611;         // PKG_ENERGY_STATUS
    const uint MSR_PP0 = 0x635;         // PP0_ENERGY_STATUS（核心域）
    const uint MSR_DRAM = 0x619;        // DRAM_ENERGY_STATUS

    // 名字必须同时满足：读前缀 + 指标含义 + 不含任何写语义
    static bool ReadCandidate(string n) =>
        Regex.IsMatch(n, @"^(Get|Read)[A-Za-z0-9_]*(MSR|Msr|Power|Energy|Watt|Tdp|Pkg)")
        && !Regex.IsMatch(n, "(?i)set|write|update|program|clr|enable|disable|config|init|shutdown");

    public static void Run(Action<string> line, bool call)
    {
        _line = line;
        line("\n== AsusWinIO64.dll：只读 MSR 探测（RAPL） ==");
        string dll = Find();
        line("dll = " + (dll ?? "未找到"));
        if (dll is null) return;

        IntPtr h = LoadLibraryW(dll);
        if (h == IntPtr.Zero) { line("LoadLibrary 失败 err=" + Marshal.GetLastWin32Error()); return; }

        var exps = Pe.Exports(h);
        line("导出 " + exps.Length + " 个：\n   " + string.Join("\n   ", exps));
        var readers = exps.Where(ReadCandidate).ToArray();
        line("白名单读候选：" + (readers.Length == 0 ? "（无）" : string.Join(", ", readers)));
        if (!call || readers.Length == 0) { line("（默认不调用；加 call 参数才按签名试探）"); return; }

        foreach (string name in new[] { "InitializeWinIo", "InitWinIo" })
        {
            if (!NativeLibrary.TryGetExport(h, name, out var p)) continue;
            int r = Marshal.GetDelegateForFunctionPointer<Func<int>>(p)();
            line(name + "() = " + r + (r == 0 ? "  -> 失败" : "  -> 可用"));
            break;
        }

        foreach (string name in readers)
            foreach (string shape in new[] { "idx,out64", "idx->u64" })
            {
                object? v = TryRead(h, name, shape, MSR_PKG);
                line("  " + name + " [" + shape + "] MSR 0x611 -> " + (v?.ToString() ?? "无结果"));
                if (v is null) continue;
                var (joules, scale) = Units(h, name, shape);
                line("    0x635=" + TryRead(h, name, shape, MSR_PP0) + " 0x619=" + TryRead(h, name, shape, MSR_DRAM)
                     + " 0x606=" + TryRead(h, name, shape, MSR_UNITS)
                     + " -> 能量单位=" + (double.IsNaN(joules) ? "?" : joules.ToString("F6") + " J/格") + " 2^" + scale);
                Power(h, name, shape, joules, scale);
                break;
            }

        if (NativeLibrary.TryGetExport(h, "ShutdownWinIo", out var sp))
        {
            try { Marshal.GetDelegateForFunctionPointer<Action>(sp)(); line("ShutdownWinIo() 已调用"); } catch { }
        }
    }

    delegate int ReadOut64(uint idx, out ulong val);
    delegate ulong Read64(uint idx);

    static object? TryRead(IntPtr h, string name, string shape, uint msr)
    {
        try
        {
            if (!NativeLibrary.TryGetExport(h, name, out var p)) return null;
            if (shape == "idx,out64")
            {
                var d = Marshal.GetDelegateForFunctionPointer<ReadOut64>(p);
                int r = d(msr, out ulong v);
                return r != 0 ? v : null;
            }
            var e = Marshal.GetDelegateForFunctionPointer<Read64>(p);
            ulong x = e(msr);
            return x != 0 && x != ulong.MaxValue ? x : null;
        }
        catch (Exception ex) { _line("  " + name + " [" + shape + "] 异常 " + ex.GetType().Name); return null; }
    }

    static (double, int) Units(IntPtr h, string name, string shape)
    {
        if (TryRead(h, name, shape, MSR_UNITS) is not ulong raw) return (double.NaN, 0);
        int units = (int)((raw >> 8) & 0xFF);
        int scale = (int)(raw & 0xF);
        return (units > 0 ? 1.0 / (1 << units) : double.NaN, scale);
    }

    // 两次能量计数器之差 / 时间 = 平均功率：真值，不是估算
    static void Power(IntPtr h, string name, string shape, double joules, int scale)
    {
        if (double.IsNaN(joules)) return;
        if (TryRead(h, name, shape, MSR_PKG) is not ulong a) return;
        Thread.Sleep(3000);
        if (TryRead(h, name, shape, MSR_PKG) is not ulong b) return;
        double w = (b - a) * joules * (1 << scale) / 3.0;
        _line("    Δ计数=" + (b - a) + " / 3s -> 封装功率 " + w.ToString("F1") + " W");
    }

    static string? Find()
    {
        string root = @"C:\WINDOWS\System32\DriverStore\FileRepository";
        try
        {
            foreach (string d in Directory.GetDirectories(root, "asussci2.inf_amd64_*"))
                foreach (string f in Directory.GetFiles(d, "AsusWinIO64.dll", SearchOption.AllDirectories))
                    return f;
            foreach (string f in Directory.GetFiles(root, "AsusWinIO64.dll", SearchOption.AllDirectories))
                return f;
            foreach (string f in Directory.GetFiles(root, "AsusWinIO*.dll", SearchOption.AllDirectories))
                return f;
        }
        catch (Exception ex) { _line("查找 dll 异常：" + ex.Message); }
        return null;
    }
}
