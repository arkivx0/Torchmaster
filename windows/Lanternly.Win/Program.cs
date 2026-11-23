using System.Windows;
using Lanternly.Core;
namespace Lanternly.Win;

public static class Program
{
    /// <summary>
    /// Crash/diagnostic log: time, place, exception type and stack trace only. Exception messages are
    /// never written (they can carry server text, e-mail addresses or user IDs), nor are credentials, tokens or keys.
    /// </summary>
    public static void Log(Exception? ex, string where)
    {
        try
        {
            Directory.CreateDirectory(Store.Dir);
            var p = Path.Combine(Store.Dir, "crash.log");
            if (File.Exists(p) && new FileInfo(p).Length > 512 * 1024) File.Delete(p);
            File.AppendAllText(p, $"[{DateTime.Now:O}] {where}" + (ex is null ? "\n" : $"\n{ex.GetType().FullName}\n{ex.StackTrace}\n\n"));
        }
        catch { /* logging must never crash the app */ }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        Diag.Sink = (where, ex) => Log(ex, where);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log(ex, "AppDomain"); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception, "Task"); e.SetObserved(); };
        bool demo = args.Contains("--demo");
        using var single = new Mutex(true, demo ? @"Local\Lanternly.Demo" : @"Local\Lanternly.Single", out bool first);
        if (!first) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try { app.Resources = Theme.Load(); } catch (Exception ex) { Log(ex, "Theme"); }
        app.DispatcherUnhandledException += (_, e) => { Log(e.Exception, "UI"); MessageBox.Show(e.Exception.Message, "Torchmaster", MessageBoxButton.OK, MessageBoxImage.Warning); e.Handled = true; };
        var w = new MainWindow(demo);
        if (!args.Contains("--tray")) w.Show();
        app.Run();
    }
}
