using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace AsusFanStudio;

public partial class App : Microsoft.UI.Xaml.Application
{
    public static Window Root;
    static Mutex _mutex;
    static EventWaitHandle _showSignal;

    public static void Log(string text)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "ProArtConsole");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                System.DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + System.Environment.NewLine);
        }
        catch { }
    }

    static string Dump(System.Exception e)
    {
        if (e is null) return "null";
        string s = e.GetType().FullName + ": " + e.Message + "\n" + e.StackTrace;
        if (e.InnerException is { } inner) s += "\n-- inner --\n" + Dump(inner);
        return s;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string id);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("domain: " + Dump(e.ExceptionObject as System.Exception));
        this.UnhandledException += (_, e) => Log("xaml: " + Dump(e.Exception));

        try { SetCurrentProcessExplicitAppUserModelID("ProArtConsole.Performance"); } catch { }
        Config.Load();
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ProArtConsole.Show");
        _mutex = new Mutex(true, @"Local\ProArtConsole.Instance", out bool owner);

        if (!owner)
        {
            // 已经有实例在后台常驻：把它唤回前台，自己退出
            try { _showSignal.Set(); } catch { }
            Exit();
            return;
        }

        try { Root = new MainWindow(); Root.Activate(); }
        catch (System.Exception e) { Log("launch: " + Dump(e)); throw; }

        new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                try { Root?.DispatcherQueue?.TryEnqueue(() => ((MainWindow)Root).ShowWindow()); }
                catch { }
            }
        })
        { IsBackground = true }.Start();
    }
}
