using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Forms = System.Windows.Forms;

namespace FloatBrowser;
internal sealed class BrowserManager : IDisposable
{
    public DataStore Store { get; }
    public Preferences Settings => Store.Data.Settings;
    public List<BrowserWindow> Windows { get; } = new();
    public BrowserWindow? Current { get; private set; }
    public Task<CoreWebView2Environment> Environment { get; }
    public bool Exiting { get; private set; }
    private readonly HotkeyService hotkeys;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon appIcon;
    private readonly DispatcherTimer timer;
    private readonly DispatcherTimer saveTimer;
    private readonly DispatcherTimer mediaTimer;
    private bool syncingPlacement;
    internal string? HotkeyError;
    // Tests replace only this input; the production dispatcher timer and real windows stay active.
    internal Func<Native.POINT?> PointerReader { get; set; } = () => Native.GetCursorPos(out var p) ? p : null;
    public BrowserManager(DataStore store, bool test)
    {
        Store = store;
        Environment = CoreWebView2Environment.CreateAsync(null, store.Profile);
        hotkeys = new HotkeyService(i => { if (i == 0) ToggleBoss(); else Switch(i == 1 ? -1 : 1); });
        if (!test && !hotkeys.Configure(new[] { Settings.Boss, Settings.Previous, Settings.Next }, out var error)) HotkeyError = error;
        appIcon = new System.Drawing.Icon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"), 32, 32);
        tray = new Forms.NotifyIcon { Icon = appIcon, Text = "浮光 · 悬浮浏览器", Visible = !test };
        tray.DoubleClick += (_, _) => Recall();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => ToggleBoss());
        menu.Items.Add("新建窗口", null, (_, _) => CreateWindow());
        menu.Items.Add("上一个窗口", null, (_, _) => Switch(-1));
        menu.Items.Add("下一个窗口", null, (_, _) => Switch(1));
        var backgroundItem=new Forms.ToolStripMenuItem("后台继续播放") { CheckOnClick=true };
        backgroundItem.Click+=(_,_)=>{Settings.BackgroundPlayback=backgroundItem.Checked;SettingsChanged();};menu.Items.Add(backgroundItem);
        menu.Opening+=(_,_)=>backgroundItem.Checked=Settings.BackgroundPlayback;
        menu.Items.Add("全部暂停",null,async (_,_)=>await PauseAllMedia());
        menu.Items.Add("设置", null, (_, _) => { Recall(); Current?.ShowSettings(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出浮光", null, (_, _) => ((App)Application.Current).Quit());
        tray.ContextMenuStrip = menu;
        timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => Current?.PollPointer(); timer.Start();
        mediaTimer=new DispatcherTimer(TimeSpan.FromMilliseconds(1200),DispatcherPriority.Background,(_,_)=>
        { foreach(var window in Windows.ToArray())_=window.PollMediaState(); },Application.Current.Dispatcher);
        saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveNow(); };
    }
    public BrowserWindow? CreateWindow(string? url = null)
    {
        if (Windows.Count >= 5) { Current?.SetStatus("最多保留 5 个窗口，请先关闭一个窗口。"); return null; }
        var window = new BrowserWindow(this, url);
        Windows.Add(window); Activate(window); RefreshWindows(); return window;
    }
    public void Activate(BrowserWindow window)
    {
        foreach (var other in Windows.Where(x => x != window)) other.Conceal(HiddenReason.Switched);
        Current = window; window.Reveal(); RefreshWindows();
    }
    public void Recall() { if (Current is not null) Activate(Current); else CreateWindow(); }
    public void ToggleBoss()
    {
        if (Windows.Any(x => x.IsVisible)) foreach (var w in Windows) w.Conceal(HiddenReason.BossKey);
        else Recall();
    }
    public void Switch(int delta)
    {
        if (Windows.Count == 0) return;
        int i = Current is null ? 0 : Windows.IndexOf(Current);
        Activate(Windows[(i + delta + Windows.Count) % Windows.Count]);
    }
    public void Remove(BrowserWindow window)
    {
        Windows.Remove(window);
        if (Exiting) return;
        if (Windows.Count == 0)
        {
            Current = null;
            Application.Current.Dispatcher.BeginInvoke(() => ((App)Application.Current).Quit());
            return;
        }
        if (Current == window) { Current = Windows.LastOrDefault(); Recall(); }
        RefreshWindows();
    }
    internal async Task PauseAllMedia() => await Task.WhenAll(Windows.ToArray().Select(w=>w.PauseAllMedia()));
    public void RefreshWindows() { foreach (var w in Windows) w.UpdateWindowList(); }
    public void SettingsChanged()
    {
        foreach (var w in Windows) w.ApplySettings();
        saveTimer.Stop(); saveTimer.Start();
    }
    internal void RememberPlacement(BrowserWindow source)
    {
        if (Exiting || syncingPlacement || Current != source || !source.CanSharePlacement) return;
        var placement = new WindowPlacement(source.Left, source.Top, source.Width, source.Height);
        if (!placement.IsValid) return;
        Settings.WindowPlacement = placement;
        // Keep the latest global bounds in memory during a native drag, but do
        // not reflow four hidden WebViews or write the profile on every pixel.
        if(source.IsInteractiveResize){saveTimer.Stop();return;}
        syncingPlacement = true;
        try { foreach (var window in Windows.Where(w => w != source)) window.ApplySharedPlacement(placement); }
        finally { syncingPlacement = false; }
        saveTimer.Stop(); saveTimer.Start();
    }
    public void SaveNow()
    {
        try { Store.Save(); }
        catch (Exception ex) { Current?.SetStatus("本机数据保存失败：" + ex.Message); }
    }
    public bool ChangeHotkeys(Hotkey[] keys, out string error)
    {
        if (!hotkeys.Configure(keys, out error)) return false;
        Settings.Boss = keys[0]; Settings.Previous = keys[1]; Settings.Next = keys[2]; HotkeyError = null; SettingsChanged(); return true;
    }
    internal void SuspendHotkeys() { if (!Exiting) hotkeys.Suspend(); }
    internal void ResumeHotkeys()
    {
        if (!Exiting && !hotkeys.Resume(out var error)) { HotkeyError = error; Current?.SetStatus(error); }
    }
    public void Dispose()
    {
        if (Exiting) return;
        Exiting = true; timer.Stop();mediaTimer.Stop(); saveTimer.Stop(); SaveNow(); hotkeys.Dispose(); tray.Visible = false; tray.Dispose(); appIcon.Dispose();
        foreach (var w in Windows.ToArray()) w.Close();
    }
}
