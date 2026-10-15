using System.Runtime.InteropServices;
using System.Text;

class ModeTest
{
    const string FILE_NAME = @"\\.\\ATKACPI";
    const uint CONTROL_CODE = 0x0022240C;
    const uint DSTS = 0x53545344;
    const uint DEVS = 0x53564544;
    const uint INIT = 0x54494E49;
    const uint MODE = 0x00110019;
    const uint CPU_FAN = 0x00110013;
    const uint GPU_FAN = 0x00110014;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFile(string n, uint a, uint s, IntPtr sec, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inB, uint inSz, byte[] outB, uint outSz, ref uint ret, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    static IntPtr _h;

    static byte[] Call(uint method, byte[] args)
    {
        byte[] buf = new byte[8 + args.Length];
        BitConverter.GetBytes(method).CopyTo(buf, 0);
        BitConverter.GetBytes((uint)args.Length).CopyTo(buf, 4);
        Array.Copy(args, buf, args.Length);
        byte[] o = new byte[16];
        uint r = 0;
        bool ok = DeviceIoControl(_h, CONTROL_CODE, buf, (uint)buf.Length, o, (uint)o.Length, ref r, IntPtr.Zero);
        if (!ok) { Console.WriteLine("    IOCTL fail err=" + Marshal.GetLastWin32Error()); return null; }
        return o;
    }

    static byte[] Two(uint a, uint b)
    {
        byte[] x = new byte[8];
        BitConverter.GetBytes(a).CopyTo(x, 0);
        BitConverter.GetBytes(b).CopyTo(x, 4);
        return x;
    }

    static int ReadMode()
    {
        byte[] r = Call(DSTS, Two(MODE, 0));
        if (r is null) return int.MinValue;
        int d0 = BitConverter.ToInt32(r, 0);
        Console.WriteLine("    mode raw = " + BitConverter.ToString(r) + " (dword0=0x" + d0.ToString("X8") + ", low16=" + (d0 & 0xFFFF) + ")");
        return d0 & 0xFFFF;
    }

    static string ModeName(int m) => m switch { 0 => "Normal", 1 => "Quiet", 2 => "Turbo", 3 => "FullSpeed", _ => "unknown" };

    static void Rpm(string tag)
    {
        byte[] a = Call(DSTS, Two(CPU_FAN, 0));
        byte[] b = Call(DSTS, Two(GPU_FAN, 0));
        int ca = a is null ? -1 : (BitConverter.ToInt32(a, 0) & 0xFFFF) * 100;
        int cb = b is null ? -1 : (BitConverter.ToInt32(b, 0) & 0xFFFF) * 100;
        Console.WriteLine("    " + tag + " CPUfan=" + ca + " GPUfan=" + cb);
    }

    static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("elevated=" + new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
        if (argv.Length < 1 || argv[0] != "WRITEMODE")
        {
            Console.WriteLine("refusing to write: pass the literal token WRITEMODE plus target 1..2");
            Console.WriteLine("read-only mode:");
            _h = CreateFile(FILE_NAME, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (_h == new IntPtr(-1)) { Console.WriteLine("open failed"); return 2; }
            byte[] ii = Call(INIT, new byte[8]);
            Console.WriteLine("INIT -> " + (ii is null ? "fail" : BitConverter.ToString(ii)));
            ReadMode(); Rpm("  ");
            byte[] ii2 = Call(INIT, new byte[8]);
            Console.WriteLine("INIT again -> " + (ii2 is null ? "fail" : BitConverter.ToString(ii2)));
            ReadMode(); Rpm("  after second INIT");
            CloseHandle(_h);
            return 0;
        }
        int target = argv.Length > 1 ? int.Parse(argv[1]) : -1;
        if (target != 1 && target != 2) { Console.WriteLine("target must be 1 (Quiet) or 2 (Turbo); refusing FullSpeed/Normal"); return 1; }

        _h = CreateFile(FILE_NAME, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (_h == new IntPtr(-1)) { Console.WriteLine("open failed err=" + Marshal.GetLastWin32Error()); return 2; }

        byte[] i = Call(INIT, new byte[8]);
        Console.WriteLine("INIT -> " + (i is null ? "fail" : BitConverter.ToString(i)));

        Console.WriteLine("1) current:");
        int orig = ReadMode();
        Console.WriteLine("   = " + ModeName(orig) + " (" + orig + ")");
        Rpm("before");

        Console.WriteLine("2) write DEVS(MODE, " + target + " = " + ModeName(target) + ")");
        byte[] w = Call(DEVS, Two(MODE, (uint)target));
        Console.WriteLine("   status raw = " + (w is null ? "fail" : BitConverter.ToString(w)) +
                          (w is null ? "" : " -> " + (BitConverter.ToInt32(w, 0) == 1 ? "OK" : BitConverter.ToInt32(w, 0))));

        Thread.Sleep(2500);
        Console.WriteLine("3) read back:");
        int now = ReadMode();
        Console.WriteLine("   = " + ModeName(now));
        Rpm("after-write");

        Console.WriteLine("4) restore DEVS(MODE, " + orig + ")");
        byte[] w2 = Call(DEVS, Two(MODE, (uint)orig));
        Console.WriteLine("   status raw = " + (w2 is null ? "fail" : BitConverter.ToString(w2)));
        Thread.Sleep(1500);
        Console.WriteLine("5) final:");
        int fin = ReadMode();
        Console.WriteLine("   = " + ModeName(fin) + "  (restored=" + (fin == orig) + ")");
        Rpm("final");

        CloseHandle(_h);
        return 0;
    }
}
