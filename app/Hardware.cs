using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AsusFanStudio;

// 一次性采集机器配置：全部走注册表 / kernel32 / SetupAPI，不引 WMI、不要管理员。
// 独显存在性也在这里定论——GPU 的温度、功耗、风扇与调节条是否显示取决于它。
internal static class Hardware
{
    public static string Machine = "—";
    public static string Bios = "—";
    public static string Cpu = "—";
    public static int CpuBaseMhz;
    public static int Threads;
    public static string Memory = "—";
    public static string Os = "—";
    public static string Screen = "—";
    public static string Storage = "—";
    public static string Gpus = "—";

    // PCI 层存在独显（VEN_10DE = NVIDIA，VEN_1002 = AMD），与它当下醒着与否无关
    public static bool HasDgpu;

    static readonly Guid DISPLAY_CLASS = new("4d36e968-e325-11ce-bfc1-08002be10318");
    static readonly Guid DISK_CLASS = new("4d36e967-e325-11ce-bfc1-08002be10318");

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint prop,
        out uint regType, byte[] buf, uint size, out uint required);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr reserved; }

    [DllImport("kernel32.dll")] static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();

    const uint DIGCF_PRESENT = 0x02;
    const uint SPDRP_DEVICEDESC = 0, SPDRP_HARDWAREID = 1, SPDRP_FRIENDLYNAME = 12;

    static List<(string desc, string hwid, string friendly)> Devices(Guid cls)
    {
        var list = new List<(string, string, string)>();
        IntPtr set = SetupDiGetClassDevsW(ref cls, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == new IntPtr(-1)) return list;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;
                list.Add((Str(set, ref data, SPDRP_DEVICEDESC, false), Str(set, ref data, SPDRP_HARDWAREID, true),
                          Str(set, ref data, SPDRP_FRIENDLYNAME, false)));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    // 属性值既可能是单值（REG_SZ）也可能是 \0 分隔的多值（硬件 ID），只按 required 的长度解析：
    // API 会在缓冲区尾部留下多余字节，整块扫会把上一段内容重复拼进来
    static string Str(IntPtr set, ref SP_DEVINFO_DATA data, uint prop, bool multi)
    {
        var buf = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, prop, out _, buf, (uint)buf.Length, out uint req)) return "";
        int n = Math.Min((int)req, buf.Length);
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i + 1 < n; i += 2)
        {
            ushort u = BitConverter.ToUInt16(buf, i);
            if (u == 0)
            {
                if (sb.Length == 0) continue;
                parts.Add(sb.ToString());
                sb.Clear();
                if (!multi) break;
            }
            else sb.Append((char)u);
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        return string.Join(" ", parts).Trim();
    }

    public static void Probe()
    {
        try
        {
            const string BIOS = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
            Machine = (string)Registry.GetValue(BIOS, "SystemProductName", "—");
            Bios = string.Join(" · ", new[]
            {
                (string)Registry.GetValue(BIOS, "BIOSVersion", null),
                (string)Registry.GetValue(BIOS, "BIOSReleaseDate", null),
            }.Where(x => !string.IsNullOrEmpty(x)));
        }
        catch { }

        try
        {
            const string CPU = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0";
            Cpu = ((string)Registry.GetValue(CPU, "ProcessorNameString", "—"))?.Trim();
            CpuBaseMhz = Convert.ToInt32(Registry.GetValue(CPU, "~MHz", 0));
            Threads = Environment.ProcessorCount;
        }
        catch { }

        try
        {
            if (GetPhysicallyInstalledSystemMemory(out ulong kb)) Memory = kb / 1024 / 1024 + " GB";
        }
        catch { }

        try
        {
            const string NT = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            string prod = (string)Registry.GetValue(NT, "ProductName", "");
            string disp = (string)Registry.GetValue(NT, "DisplayVersion", "");
            string build = (string)Registry.GetValue(NT, "CurrentBuild", "");
            uint ubr = Convert.ToUInt32(Registry.GetValue(NT, "UBR", 0));
            int b = int.TryParse(build, out int bb) ? bb : 0;
            // 注册表在 11 上仍写 "Windows 10"，代际按 build 号判
            string family = b >= 22000 ? "Windows 11" : "Windows 10";
            string edition = (prod ?? "").Replace("Windows 10", "").Replace("Windows 11", "").Trim();
            Os = family + (edition.Length > 0 ? " " + edition : "") + (disp.Length > 0 ? " · " + disp : "")
                + (build.Length > 0 ? " · " + build + "." + ubr : "");
        }
        catch { }

        try
        {
            var adapters = Devices(DISPLAY_CLASS).Where(a => a.desc.Length > 0).ToList();
            Gpus = string.Join(" + ", adapters.Select(a => a.desc));
            foreach (var a in adapters)
            {
                string id = (a.hwid + " " + a.desc).ToUpperInvariant();
                if (id.Contains(@"ROOT\DISPLAY")) continue;                       // 虚拟/远程显示适配器
                if (id.Contains("VEN_10DE") || id.Contains("VEN_1002")) HasDgpu = true;
            }
        }
        catch { }

        try
        {
            int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
            uint dpi = GetDpiForSystem();
            Screen = w + "×" + h + (dpi > 96 ? " @ " + dpi * 100 / 96 + "%" : "");
        }
        catch { }

        try
        {
            // 磁盘的 DEVICEDESC 只有本地化的类名「磁盘驱动器」，型号在 FRIENDLYNAME 里
            var names = Devices(DISK_CLASS).Select(d => d.friendly.Length > 0 ? d.friendly : d.desc)
                .Where(n => n.Length > 0).Distinct().ToList();
            var vols = System.IO.DriveInfo.GetDrives()
                .Where(d => d.DriveType == System.IO.DriveType.Fixed && d.IsReady)
                .Select(d => d.Name.TrimEnd('\\') + " " + d.TotalSize / 1073741824 + " GB（空闲 "
                            + d.TotalFreeSpace / 1073741824 + " GB）");
            var parts = names.Concat(vols).ToList();
            Storage = parts.Count > 0 ? string.Join(" · ", parts) : "—";
        }
        catch { }
    }

    public static IEnumerable<(string label, string value)> Items()
    {
        yield return ("机型", Machine);
        yield return ("处理器", Cpu + (CpuBaseMhz > 0 ? "（基频 " + CpuBaseMhz + " MHz · " + Threads + " 线程）" : ""));
        yield return ("内存", Memory);
        yield return ("显卡", Gpus + (HasDgpu ? "（有独显）" : "（无独显）"));
        yield return ("屏幕", Screen);
        yield return ("存储", Storage);
        yield return ("BIOS", Bios);
        yield return ("系统", Os);
    }
}
