using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FloatBrowser;
public partial class App : Application
{
    internal BrowserManager Manager = null!;
    private Mutex? mutex;
    private EventWaitHandle? wake;
    private RegisteredWaitHandle? wakeRegistration;
    protected override void OnStartup(StartupEventArgs e)
    {
        Native.SetCurrentProcessExplicitAppUserModelID("FloatBrowser.Desktop");
        base.OnStartup(e);
        if (e.Args.Contains("--self-test"))
        {
            try { Verification.SelfTest(e.Args); Shutdown(0); }
            catch (Exception ex) { Verification.WriteResult(e.Args, ex.ToString()); Shutdown(1); }
            return;
        }
        bool smoke = e.Args.Contains("--smoke-test") || e.Args.Contains("--persistence-test") || e.Args.Contains("--close-test") || e.Args.Contains("--close-list-test") || e.Args.Contains("--media-test") || e.Args.Contains("--backdrop-test") || e.Args.Contains("--resize-player-test") || e.Args.Contains("--playback-options-test");
        string root = smoke ? Verification.TestRoot(e.Args) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatBrowser");
        string suffix = smoke ? ".Test" : "";
        mutex = new Mutex(true, "Local\\FloatBrowser.Instance" + suffix, out bool first);
        wake = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\FloatBrowser.Wake" + suffix);
        if (!first) { wake.Set(); Shutdown(); return; }
        try
        {
            Manager = new BrowserManager(new DataStore(root), smoke);
            wakeRegistration = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) => Dispatcher.BeginInvoke(() => Manager?.Recall()), null, -1, false);
            var window = Manager.CreateWindow();
            if (e.Args.Contains("--diagnostics"))
            {
                var log = Path.Combine(root, "diagnostics.txt");
                File.WriteAllText(log, $"Created: visible={window!.IsVisible}; opacity={window.Opacity}; bounds={window.Left},{window.Top},{window.Width},{window.Height}; hwnd={new System.Windows.Interop.WindowInteropHelper(window).Handle}\n");
                _ = window.Ready.Task.ContinueWith(t => Dispatcher.Invoke(() => File.AppendAllText(log, $"Ready={t.Status}; visible={window.IsVisible}; title={window.Title}; error={t.Exception}\n")));
            }
            if (smoke) _ = RunSmoke(window!, e.Args);
        }
        catch (Exception ex) { if (smoke) Verification.WriteResult(e.Args, ex.ToString()); else MessageBox.Show("启动失败：" + ex.Message, "浮光"); Quit(1); }
    }
    private async Task RunSmoke(BrowserWindow window, string[] args)
    {
        try { await Verification.Smoke(Manager, window, args); Verification.WriteResult(args, "PASS"); Quit(0); }
        catch (Exception ex) { Verification.WriteResult(args, ex.ToString()); Quit(1); }
    }
    internal void Quit(int code = 0) { Manager?.Dispose(); Shutdown(code); }
    protected override void OnExit(ExitEventArgs e)
    {
        wakeRegistration?.Unregister(null); Manager?.Dispose(); wake?.Dispose(); mutex?.Dispose(); base.OnExit(e);
    }
}
