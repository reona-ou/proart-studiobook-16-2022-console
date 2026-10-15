using System.Runtime.InteropServices;
using System.Text;

class W
{
    const string DLL = @"C:\WINDOWS\System32\DriverStore\FileRepository\asussci2.inf_amd64_197d319fab43e0fe\ASUSSystemAnalysis\AsusWinIO64.dll";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F0();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F1(int a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F2(int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F0o(out int a);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F1o(int a, out int b);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int F1s(int a, StringBuilder s);

    static IntPtr _h;

    static IntPtr Get(string name)
    {
        if (!NativeLibrary.TryGetExport(_h, name, out var p))
            throw new Exception("no export: " + name);
        return p;
    }

    static T Bind<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(Get(name));

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length < 2) { Console.WriteLine("usage: winioprobe <mode> <fn> [intArg]"); return 1; }
        string mode = args[0], fn = args[1];
        int a = args.Length > 2 ? int.Parse(args[2]) : 0;

        _h = NativeLibrary.Load(DLL);
        Console.WriteLine("loaded dll, elevated=" + new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));

        try
        {
            if (NativeLibrary.TryGetExport(_h, "InitializeWinIo", out var ip))
            {
                var init = Marshal.GetDelegateForFunctionPointer<F0>(ip);
                int r = init();
                Console.WriteLine("InitializeWinIo -> " + r);
            }
            else Console.WriteLine("InitializeWinIo export missing");

            switch (mode)
            {
                case "f0":
                    Console.WriteLine(fn + "() = " + Bind<F0>(fn)());
                    break;
                case "f1":
                    Console.WriteLine(fn + "(" + a + ") = " + Bind<F1>(fn)(a));
                    break;
                case "f0o":
                    Bind<F0o>(fn)(out int v0);
                    Console.WriteLine(fn + "(out) = " + v0);
                    break;
                case "f1o":
                    Bind<F1o>(fn)(a, out int v1);
                    Console.WriteLine(fn + "(" + a + ", out) = " + v1);
                    break;
                case "f1s":
                    var sb = new StringBuilder(256);
                    int rr = Bind<F1s>(fn)(a, sb);
                    Console.WriteLine(fn + "(" + a + ", buf) = " + rr + " str='" + sb + "'");
                    break;
                default:
                    Console.WriteLine("bad mode");
                    return 1;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine("EXCEPTION: " + e.Message);
            return 3;
        }

        try
        {
            if (NativeLibrary.TryGetExport(_h, "ShutdownWinIo", out var sp))
                Marshal.GetDelegateForFunctionPointer<F0>(sp)();
        }
        catch { }
        return 0;
    }
}
