using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace FloatBrowser;
internal static partial class Verification
{
    private static readonly List<string> checks = new();
    private static string Argument(string[] args, string name, string fallback)
    {
        int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
    public static string TestRoot(string[] args) => Argument(args, "--test-root", Path.Combine(Path.GetTempPath(), "FloatBrowser.Test"));
    public static void WriteResult(string[] args, string result) => File.WriteAllText(Argument(args, "--result", Path.Combine(TestRoot(args), "result.txt")), result + "\n" + string.Join("\n", checks));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); checks.Add("PASS: " + message); }
    public static void SelfTest(string[] args)
    {
        var p = new VisibilityPolicy();
        Check(Hotkey.FromKey(Key.Q, ModifierKeys.None) == new Hotkey(0, 0x51), "single-letter hotkey needs no modifiers");
        Check(Hotkey.FromKey(Key.F8, ModifierKeys.Shift) == new Hotkey(4, 0x77), "Shift-only combination accepted");
        Check(Hotkey.FromKey(Key.D7, ModifierKeys.None).ToString() == "7", "number hotkey display is readable");
        foreach (var reason in new[] { HiddenReason.BossKey, HiddenReason.Switched }) { p.Hide(reason); Check(!p.CanMouseRestore(true, true), "manual/switch hidden cannot hover restore"); }
        p.Hide(HiddenReason.MouseLeave); Check(!p.CanMouseRestore(false, true), "restore defaults off"); Check(!p.CanMouseRestore(true, false), "requires pointer entry"); Check(p.CanMouseRestore(true, true), "mouse hide can restore"); p.Show(); Check(p.Visible, "reveal resets reason");
        var root = TestRoot(args); Directory.CreateDirectory(root); var store = new DataStore(root);
        Check(!store.Data.Settings.HideOnLeave && !store.Data.Settings.RestoreOnHover, "mouse options default off");
        for (int i = 0; i < 60; i++) store.Visit("Page " + i, "https://example.com/" + i);
        Check(store.Data.History.Count == 50 && store.Data.History[0].Url.EndsWith("59"), "50 newest history entries");
        store.Visit("Again", "https://example.com/55"); Check(store.Data.History.Count == 50 && store.Data.History[0].Title == "Again", "history deduplicated");
        store.Data.Favorites.Add(new("Saved", "https://example.com", DateTimeOffset.Now)); store.Data.Settings.Opacity = .4; store.Save();
        var reloaded = new DataStore(root); Check(reloaded.Data.Favorites.Count >= 1 && reloaded.Data.Settings.Opacity == .4, "roundtrip persistence");
        Check(Navigation.Resolve("example.com") == "https://example.com", "domain navigation"); Check(Navigation.Resolve("hello world").EndsWith("hello%20world"), "escaped search"); Check(!Navigation.IsWebUrl("javascript:alert(1)"), "scheme validation");
        WriteResult(args, "PASS: visibility policy, mouse defaults, history bound/deduplication, persistence, URL handling");
    }
    private static async Task Navigate(BrowserWindow w, string url)
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong navigation = ulong.MaxValue;
        void Starting(object? s, CoreWebView2NavigationStartingEventArgs a) { if (a.Uri == url) navigation = a.NavigationId; }
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs a) { if (a.NavigationId == navigation) ready.TrySetResult(a.IsSuccess); }
        w.Web.CoreWebView2.NavigationStarting += Starting;
        w.Web.CoreWebView2.NavigationCompleted += Handler;
        try { w.Web.CoreWebView2.Navigate(url); Check(await ready.Task.WaitAsync(TimeSpan.FromSeconds(30)), "fixture navigation"); }
        finally { w.Web.CoreWebView2.NavigationCompleted -= Handler; w.Web.CoreWebView2.NavigationStarting -= Starting; }
    }
    public static async Task Smoke(BrowserManager m, BrowserWindow w, string[] args)
    {
        m.PointerReader = () => new Native.POINT { X = -30000, Y = -30000 };
        await w.Ready.Task.WaitAsync(TimeSpan.FromSeconds(45));
        w.Hide(); w.Reveal(); // Consume a test runner's initial hidden STARTUPINFO.
        await Navigate(w, "https://float.internal/test.html");
        if (args.Contains("--resize-player-test")) { await ResizePlayerRegression(m, w, args); return; }
        if(args.Contains("--playback-options-test")){await PlaybackOptionsRegression(m,w,args);return;}
        if (args.Contains("--media-test")) { await PlaybackRegression(m, w); return; }
        if (args.Contains("--backdrop-test")) { await BackdropRegression(m, w); return; }
        if(args.Contains("--close-list-test"))
        {
            var row=w.WindowList.Children.OfType<Grid>().Single();
            row.Children.OfType<Button>().Single(b=>Equals(b.Tag,"CloseWindow")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(m.Windows.Count==0 && !w.IsVisible,"list close on the final webpage exits without hiding or creating another window");
            return;
        }
        if (args.Contains("--close-test"))
        {
            w.CloseButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(m.Windows.Count == 0 && !w.IsVisible, "title-bar X closes last window without creating or hiding a replacement");
            return;
        }
        if (args.Contains("--persistence-test"))
        {
            Check(m.Settings.WindowPlacement is { } savedPlacement && Math.Abs(w.Width-savedPlacement.Width)<1 && Math.Abs(w.Height-savedPlacement.Height)<1 && Math.Abs(w.Left-savedPlacement.Left)<1 && Math.Abs(w.Top-savedPlacement.Top)<1, "global window placement survives process restart");
            Check(Math.Abs(m.Settings.Opacity - .67) < .001 && Math.Abs(w.OpacitySlider.Value - 67) < .001, "global opacity survives restart and initial XAML slider setup");
            Check(Native.GetLayeredWindowAttributes(new WindowInteropHelper(w).Handle, out _, out byte restoredAlpha, out _) && restoredAlpha == 171, "restored opacity is applied to native window");
            Check(await w.Web.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('retained')") == "\"yes\"", "localStorage across process restart");
            var cookies = await w.Web.CoreWebView2.CookieManager.GetCookiesAsync("https://float.internal");
            Check(cookies.Any(c => c.Name == "session_check" && c.Value == "kept"), "persistent cookies across restart"); return;
        }
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("window.fixture") == "42", "real JavaScript execution");
        await FocusLayoutRegression(m, w);
        await SingleKeyRegression(m, w);
        await w.Web.CoreWebView2.ExecuteScriptAsync("localStorage.setItem('retained','yes')");
        var cookie = w.Web.CoreWebView2.CookieManager.CreateCookie("session_check", "kept", "float.internal", "/"); cookie.Expires = DateTime.UtcNow.AddDays(7); cookie.IsSecure = true; w.Web.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
        m.Settings.Opacity = .1; m.Settings.Zoom = .3; m.SettingsChanged();
        Check(Native.GetLayeredWindowAttributes(new System.Windows.Interop.WindowInteropHelper(w).Handle, out _, out byte alpha, out _) && alpha == 26 && Math.Abs(w.Web.ZoomFactor - .3) < .001, "native lower opacity and zoom bounds");
        m.Settings.Opacity = 1; m.Settings.Zoom = 3; m.SettingsChanged(); Check(w.Web.ZoomFactor == 3, "upper zoom bound"); m.Settings.Zoom = 1; m.SettingsChanged();
        var rect = new Rect(w.Left,w.Top,w.Width,w.Height); w.ToggleAd(); Check(w.IsAd && w.Width == 380, "ad geometry"); w.ToggleAd();
        Check(new Rect(w.Left,w.Top,w.Width,w.Height) == rect, "ad restores original bounds"); Check(await w.Web.CoreWebView2.ExecuteScriptAsync("window.fixture") == "42", "ad retains page context");
        await w.SetMobile(true); Check(w.IsMobile, "mobile flag"); Check(await w.Web.CoreWebView2.ExecuteScriptAsync("window.innerWidth") == "390", "mobile viewport is 390 CSS pixels"); await w.SetMobile(false);
        m.Settings.Opacity = .67; m.SettingsChanged();
        for (int i = 0; i < 4; i++)
        {
            var child = m.CreateWindow(); Check(child is not null, "new window"); await child!.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Check(Math.Abs(m.Settings.Opacity - .67) < .001 && m.Windows.All(x => Math.Abs(x.OpacitySlider.Value - 67) < .001), "new window does not reset shared opacity");
            Check(Native.GetLayeredWindowAttributes(new WindowInteropHelper(child).Handle, out _, out byte childAlpha, out _) && childAlpha == 171, "new window inherits native global opacity");
        }
        Check(m.CreateWindow() is null && m.Windows.Count == 5, "five-window cap"); Check(m.Windows.Count(x => x.IsVisible) == 1, "only current window visible");
        await SharedPlacementRegression(m, w);
        m.Activate(w); m.ToggleBoss(); Check(m.Windows.All(x => !x.IsVisible && x.Policy.Reason == HiddenReason.BossKey), "boss hides every window"); await Task.Delay(600); m.ToggleBoss(); Check(w.IsVisible, "boss recalls current");
        m.Switch(1); Check(!w.IsVisible && w.Policy.Reason == HiddenReason.Switched, "switch preserves hidden reason"); m.Activate(w);
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("localStorage.getItem('retained')") == "\"yes\"", "profile storage retained");
        // Verify conflicting global hotkey registration leaves the previous mapping intact.
        using var blocker = new HotkeyService(_ => { });
        var reserved = new[] { new Hotkey(7, 0x79), new Hotkey(7, 0x7A), new Hotkey(7, 0x7B) };
        Check(blocker.Configure(reserved, out _), "test hotkeys registered");
        var before = m.Settings.Boss; Check(!m.ChangeHotkeys(reserved, out _) && m.Settings.Boss == before, "hotkey conflict rollback");
        await MouseRegression(m, w);
        await PlaybackRegression(m, w);
        await BackdropRegression(m, w);
        string renderDirectory = Argument(args, "--render-dir", "");
        await CompactToolsRegression(m,w,renderDirectory);
        await ResizeRegression(m,w);
        await WindowListCloseRegression(m,w);
        if (renderDirectory.Length > 0) await RenderInspection(m, w, renderDirectory);
        foreach (var child in m.Windows.Where(x => x != w).ToArray()) child.Close();
        Check(m.Windows.Count == 1, "closing four initialized windows preserves remaining window");
        w.Conceal(HiddenReason.BossKey); Check(!w.IsVisible && m.Windows.Count == 1, "explicit hide retains last window");
        m.Recall(); Check(w.IsVisible, "tray recall after explicit hide");
        var pending = m.CreateWindow(); pending!.Close();
        Check(m.Windows.Count == 1, "close a window during WebView initialization");
        m.Activate(w); w.Conceal(HiddenReason.BossKey);
        m.Settings.Opacity = .67; m.SettingsChanged(); m.SaveNow(); await Task.Delay(800);
    }
    private static Native.POINT Inside(BrowserWindow w)
    {
        Check(Native.GetWindowRect(new WindowInteropHelper(w).Handle, out var r), "read physical HWND bounds");
        return new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
    }
    private static async Task SharedPlacementRegression(BrowserManager m, BrowserWindow w)
    {
        m.Activate(w); w.Width=960; w.Height=640; w.Left=120; w.Top=85; w.UpdateLayout(); await Task.Delay(100);
        Check(m.Windows.All(x=>Math.Abs(x.Width-960)<1 && Math.Abs(x.Height-640)<1 && Math.Abs(x.Left-120)<1 && Math.Abs(x.Top-85)<1), "resize and move propagate to all ordinary windows");
        w.ToggleAd(); var adWidth=w.Width;
        var other=m.Windows.First(x=>x!=w); m.Activate(other);
        other.Width=1000; other.Height=700; other.Left=150; other.Top=95; other.UpdateLayout(); await Task.Delay(100);
        Check(w.IsAd && w.Width==adWidth && m.Settings.WindowPlacement?.Width==1000, "advertisement geometry stays separate from shared normal bounds");
        m.Activate(w); w.ToggleAd();
        Check(Math.Abs(w.Width-1000)<1 && Math.Abs(w.Height-700)<1 && Math.Abs(w.Left-150)<1 && Math.Abs(w.Top-95)<1, "leaving advertisement restores latest shared normal placement");
    }
    private static async Task BackdropRegression(BrowserManager m, BrowserWindow w)
    {
        m.Settings.ClearWhiteBackground=false; m.SettingsChanged();
        await Navigate(w,"https://float.internal/backdrop-test.html");
        w.BackdropButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await WaitForScript(w,"getComputedStyle(document.body).backgroundColor==='rgba(0, 0, 0, 0)'", "white page background becomes transparent");
        Check(Native.GetLayeredWindowAttributes(new WindowInteropHelper(w).Handle,out uint key,out _,out uint flags) && key==Native.BackdropKey && (flags&1)!=0,"native color-key transparency enabled alongside global opacity");
        await VerifyNativeBackdrop(m, w);
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("['input','image','canvas','video'].every(id=>getComputedStyle(document.getElementById(id)).backgroundColor==='rgb(255, 255, 255)')") == "true", "interactive controls, images, canvas and video are excluded from white background rewriting");
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.getElementById('color')).backgroundColor==='rgb(30, 90, 70)'") == "true", "colored backgrounds remain unchanged");
        await w.Web.CoreWebView2.ExecuteScriptAsync("const dynamic=document.createElement('div');dynamic.id='dynamicWhite';dynamic.style.backgroundColor='white';document.body.append(dynamic);");
        await WaitForScript(w,"getComputedStyle(document.getElementById('dynamicWhite')).backgroundColor==='rgba(0, 0, 0, 0)'", "newly added white content is processed");
        Check(m.Windows.All(x=>x.BackdropButton.Content.ToString()!.Contains("：开")), "white background switch is global");
        w.BackdropButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await WaitForScript(w,"getComputedStyle(document.body).backgroundColor==='rgb(255, 255, 255)'", "turning feature off restores original inline white background");
        Check(Native.GetLayeredWindowAttributes(new WindowInteropHelper(w).Handle,out _,out _,out flags) && flags==2,"turning feature off removes color-key transparency");
        await FullscreenRegression(m,w);
    }
    private static async Task VerifyNativeBackdrop(BrowserManager m, BrowserWindow w)
    {
        var background = new Window { Width=w.Width+64, Height=w.Height+64, Left=w.Left-32, Top=w.Top-32, ResizeMode=ResizeMode.NoResize, WindowStartupLocation=WindowStartupLocation.Manual, WindowStyle=WindowStyle.None, Background=Brushes.DarkSlateBlue, ShowInTaskbar=false, Topmost=true, Title="Backdrop verification surface" };
        background.Show(); w.Activate(); w.UpdateLayout(); await Task.Delay(250);
        try
        {
            var point=w.Web.PointToScreen(new Point(w.Web.ActualWidth-30,w.Web.ActualHeight-30));
            var pixel=new Native.POINT { X=(int)point.X, Y=(int)point.Y };
            var backHandle=new WindowInteropHelper(background).Handle;
            var frontHandle=new WindowInteropHelper(w).Handle;
            checks.Add($"INFO: native hit root={Native.GetAncestor(Native.WindowFromPoint(pixel),2)} front={frontHandle} back={backHandle} point={pixel.X},{pixel.Y}");
            await WaitFor(()=>Native.WindowFromPoint(pixel)==w.TransparentInput.Handle,"transparent background receives mouse input instead of passing to the underlying app");
            var rectJson=await w.Web.CoreWebView2.ExecuteScriptAsync("(()=>{const r=document.getElementById('color').getBoundingClientRect();return {x:r.x+20,y:r.y+20}})()");
            using var rectDoc=System.Text.Json.JsonDocument.Parse(rectJson);
            var contentPoint=w.Web.PointToScreen(new Point(rectDoc.RootElement.GetProperty("x").GetDouble(),rectDoc.RootElement.GetProperty("y").GetDouble()));
            var contentPixel=new Native.POINT{X=(int)contentPoint.X,Y=(int)contentPoint.Y};
            checks.Add($"INFO: screen background=0x{Native.ScreenPixel(pixel):X6} content=0x{Native.ScreenPixel(contentPixel):X6}");
            static bool Near(uint a,uint b) => new[]{0,8,16}.All(shift=>Math.Abs((int)((a>>shift)&255)-(int)((b>>shift)&255))<=2);
            Check(Near(Native.ScreenPixel(pixel),0x008B3D48) && Near(Native.ScreenPixel(contentPixel),0x00465A1E),"native screen pixels show the underlying purple surface while retaining colored webpage content");
            await VerifyBackdropEdges(w);
            await TransparentInteraction(w);
            w.ToolsToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitForScript(w,"getComputedStyle(document.body).backgroundColor==='rgba(0, 0, 0, 0)'","opening Tools preserves background transparency");
            Check(m.Settings.ClearWhiteBackground && w.IsBackdropActive,"transparent interaction does not require toggling Tools");
            w.ToolsToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await WaitForScript(w,"getComputedStyle(document.body).backgroundColor==='rgba(0, 0, 0, 0)'","closing Tools reapplies background transparency");
            m.Settings.ClearWhiteBackground=false;m.SettingsChanged();
            await WaitFor(()=>Native.GetAncestor(Native.WindowFromPoint(pixel),2)==frontHandle,"disabling background transparency restores normal window hit testing");
            m.Settings.ClearWhiteBackground=true;m.SettingsChanged(); await Task.Delay(200);
        }
        finally { background.Close(); }
    }
    private static async Task VerifyBackdropEdges(BrowserWindow w)
    {
        var dpi=VisualTreeHelper.GetDpi(w.Web);
        checks.Add($"INFO: backdrop edge verification runtime DPI scale={dpi.DpiScaleX:0.###}x{dpi.DpiScaleY:0.###} ({dpi.PixelsPerInchX:0.##}x{dpi.PixelsPerInchY:0.##} dpi); no synthetic DPI override");
        Check(w.UseLayoutRounding && w.SnapsToDevicePixels,"transparent host rounds layout and snaps drawing to physical pixels");
        Check(w.WindowFrame.BorderThickness==new Thickness(0) && w.WindowFrame.CornerRadius==new CornerRadius(0) && w.WindowLayout.Margin==new Thickness(0),"transparent mode removes rounded frame and inset around the webpage");
        Check(!w.CompactTools.IsOpen && w.Overlay.Visibility!=Visibility.Visible,"edge pixel probe has no tool panel or settings overlay over the webpage");
        // Temporarily hide fixture content and scrollbars, leaving its original
        // transparent white background. This distinguishes native host fringes
        // from website text, controls, borders and video pixels.
        await w.Web.CoreWebView2.ExecuteScriptAsync("(()=>{const s=document.createElement('style');s.id='__float_edge_probe';s.textContent='html,body{overflow:hidden!important}body>*{visibility:hidden!important}';document.head.append(s)})()");
        try
        {
            w.UpdateLayout();await Task.Delay(200);
            var origin=w.Web.PointToScreen(new Point());
            var end=w.Web.PointToScreen(new Point(w.Web.ActualWidth,w.Web.ActualHeight));
            Check(Native.GetWindowRect(new WindowInteropHelper(w).Handle,out var frame),"read physical frame bounds for transparent edge verification");
            Check(Math.Abs(origin.X-frame.Left)<.05 && Math.Abs(end.X-frame.Right)<.05 && Math.Abs(end.Y-frame.Bottom)<.05,"transparent webpage extends to left, right and bottom window edges without a shell gap");
            Check(Math.Abs(origin.X-Math.Round(origin.X))<.05 && Math.Abs(origin.Y-Math.Round(origin.Y))<.05 && Math.Abs(end.X-Math.Round(end.X))<.05 && Math.Abs(end.Y-Math.Round(end.Y))<.05,"all transparent webpage boundaries align with physical pixels at the current DPI");
            int left=(int)Math.Round(origin.X),top=(int)Math.Round(origin.Y),right=(int)Math.Round(end.X),bottom=(int)Math.Round(end.Y);
            var samples=new List<(string Side,Native.POINT Point)>();
            foreach(int depth in new[]{0,1,2})
                foreach(double fraction in new[]{.25,.5,.75})
                {
                    int x=left+(int)((right-left)*fraction),y=top+(int)((bottom-top)*fraction);
                    samples.Add(($"left+{depth}",new Native.POINT{X=left+depth,Y=y}));
                    samples.Add(($"right-{depth+1}",new Native.POINT{X=right-depth-1,Y=y}));
                    samples.Add(($"top+{depth}",new Native.POINT{X=x,Y=top+depth}));
                    samples.Add(($"bottom-{depth+1}",new Native.POINT{X=x,Y=bottom-depth-1}));
                }
            var mismatches=new List<string>();
            foreach(var sample in samples)
            {
                uint color=Native.ScreenPixel(sample.Point);
                if(new[]{0,8,16}.Any(shift=>Math.Abs((int)((color>>shift)&255)-(int)((0x008B3D48u>>shift)&255))>2))
                    mismatches.Add($"{sample.Side}@{sample.Point.X},{sample.Point.Y}=0x{color:X6}");
            }
            if(mismatches.Count>0)checks.Add("INFO: unexpected edge pixels: "+string.Join("; ",mismatches));
            Check(mismatches.Count==0,"first three physical pixel rows/columns on all four webpage edges show the purple backing with no bright fringe");
        }
        finally
        {
            await w.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('__float_edge_probe')?.remove()");
            await Task.Delay(120);
        }
    }
    private static async Task NativeMouse(BrowserWindow w, string element, uint message, int keys=0, double fraction=.5)
    {
        var json=await w.Web.CoreWebView2.ExecuteScriptAsync($"(()=>{{const r=document.getElementById('{element}').getBoundingClientRect();return {{x:r.x+r.width*{fraction.ToString(System.Globalization.CultureInfo.InvariantCulture)},y:r.y+r.height/2}}}})()");
        using var doc=System.Text.Json.JsonDocument.Parse(json);
        var dpi=VisualTreeHelper.GetDpi(w.Web);
        int x=(int)Math.Round(doc.RootElement.GetProperty("x").GetDouble()*w.Web.ZoomFactor*dpi.DpiScaleX);
        int y=(int)Math.Round(doc.RootElement.GetProperty("y").GetDouble()*w.Web.ZoomFactor*dpi.DpiScaleY);
        Native.SendMessage(w.TransparentInput.Handle,message,new IntPtr(keys),BackdropInput.Pack(x,y));
        await Task.Delay(80);
    }
    private static async Task NativeClick(BrowserWindow w, string element)
    {
        await NativeMouse(w,element,0x200);
        await NativeMouse(w,element,0x201,1);
        await NativeMouse(w,element,0x202);
    }
    private static async Task TransparentInteraction(BrowserWindow w)
    {
        Check(w.TransparentInput.Renderer!=IntPtr.Zero,"Chromium native input target is available");
        await NativeClick(w,"white");
        await WaitForScript(w,"clicks===1 && trusted && moves>0","transparent page receives trusted native click and hover events");
        await NativeClick(w,"input");
        await WaitForScript(w,"document.activeElement.id==='input'","native click focuses HTML text input while background stays transparent");
        BackdropInput.PostMessage(w.TransparentInput.Renderer,0x102,new IntPtr('X'),IntPtr.Zero);
        await WaitForScript(w,"document.getElementById('input').value.includes('X')","focused web input receives native keyboard text");
        await w.Web.CoreWebView2.ExecuteScriptAsync("document.body.style.minHeight='2000px'");
        var screen=w.Web.PointToScreen(new Point(200,200));
        Native.SendMessage(w.TransparentInput.Handle,0x20A,new IntPtr(unchecked((int)0xFF880000)),BackdropInput.Pack((int)screen.X,(int)screen.Y));
        await WaitForScript(w,"scrollY>0","native mouse wheel scrolls transparent page");
        await w.Web.CoreWebView2.ExecuteScriptAsync("document.body.style.minHeight='';scrollTo(0,0)");
        await Task.Delay(150);
    }
    private static async Task FullscreenRegression(BrowserManager m,BrowserWindow w)
    {
        m.Settings.ClearWhiteBackground=true;m.SettingsChanged();
        await Navigate(w,"https://float.internal/fullscreen-test.html");
        await Task.Delay(250);
        await NativeClick(w,"enter");
        checks.Add("INFO: native window active="+w.IsActive+"; fullscreen activation "+await w.Web.CoreWebView2.ExecuteScriptAsync("({clicked:window.enterClicked,error:window.fullscreenError,full:document.fullscreenElement?.id,focus:document.hasFocus(),hidden:document.hidden})"));
        await WaitForScript(w,"document.fullscreenElement?.id==='player'","native click enters real HTML fullscreen while transparent");
        m.Settings.ClearWhiteBackground=false;m.SettingsChanged();await Task.Delay(80);
        m.Settings.ClearWhiteBackground=true;m.SettingsChanged();await Task.Delay(150);
        await Task.Delay(900);
        await WaitForScript(w,"getComputedStyle(document.getElementById('bar')).visibility==='hidden'","fullscreen control bar hides when idle");
        await NativeMouse(w,"seek",0x200);
        await WaitForScript(w,"getComputedStyle(document.getElementById('bar')).visibility==='visible'","fullscreen bottom controls appear on native hover after transparency toggled on");
        await NativeClick(w,"play");
        await WaitForScript(w,"!document.getElementById('movie').paused","fullscreen playback button receives native click");
        await NativeMouse(w,"seek",0x201,1,.1);
        await NativeMouse(w,"seek",0x200,1,.8);
        await NativeMouse(w,"seek",0x202,0,.8);
        await WaitForScript(w,"Number(document.getElementById('seek').value)>70 && seekEvents>0","fullscreen seek slider handles trusted drag input");
        await NativeClick(w,"play");
        await WaitForScript(w,"document.getElementById('movie').paused","fullscreen playback can be paused again");
        await NativeClick(w,"exit");
        await WaitForScript(w,"document.fullscreenElement===null","fullscreen exit button remains clickable");
        var old=new Rect(w.Left,w.Top,w.Width,w.Height);
        w.Width=240;w.Height=160;w.UpdateLayout();await Task.Delay(200);
        Check(w.ActualWidth==240 && w.ActualHeight==160,"normal window resizes freely below the previous 320 by 220 floor");
        Check(m.Settings.WindowPlacement?.Width==240,"small shared window geometry is persisted");
        var p=w.Web.PointToScreen(new Point(w.Web.ActualWidth-3,w.Web.ActualHeight-3));
        var hit=Native.SendMessage(w.TransparentInput.Handle,0x84,IntPtr.Zero,BackdropInput.Pack((int)p.X,(int)p.Y));
        Check(hit.ToInt32()==17,"transparent bottom-right corner retains native resize hit target");
        var input=w.TransparentInput.Handle;w.Conceal(HiddenReason.BossKey);
        Check(!Native.IsWindowVisible(input),"hiding browser immediately hides its input surface");
        w.Reveal();await Task.Delay(100);Check(Native.IsWindowVisible(input),"recalling browser restores transparent input surface");
        var taskbar=TaskbarGuard.Areas().FirstOrDefault(r=>r.Right-r.Left>100 && r.Bottom-r.Top>10);
        if(taskbar.Right>taskbar.Left)
        {
            int x=(taskbar.Left+taskbar.Right)/2,y=(taskbar.Top+taskbar.Bottom)/2;
            var dpi=VisualTreeHelper.GetDpi(w);
            w.Left=(x-120)/dpi.DpiScaleX;w.Top=(y-70)/dpi.DpiScaleY;w.UpdateLayout();await Task.Delay(250);
            var root=Native.GetAncestor(Native.WindowFromPoint(new Native.POINT{X=x,Y=y}),2);
            Check(root!=input && root!=new WindowInteropHelper(w).Handle,"overlapping system taskbar takes input priority over pinned transparent browser");
        }
        else checks.Add("NOT COVERED: visible system taskbar was unavailable");
        w.Width=old.Width;w.Height=old.Height;w.Left=old.Left;w.Top=old.Top;
        m.Settings.ClearWhiteBackground=false;m.SettingsChanged();
    }
    private static async Task SingleKeyRegression(BrowserManager m, BrowserWindow w)
    {
        w.ShowSettings();
        var boxes = w.OverlayContent.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<TextBox>()).Where(b => Equals(b.Tag, "HotkeyCapture")).ToArray();
        Check(boxes.Length == 3, "three editable hotkey fields available");
        void Capture(TextBox box, Key key) => box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(w), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        foreach (var key in new[] { Key.Q, Key.D7, Key.Space, Key.Escape, Key.F8 })
        {
            Capture(boxes[0], key);
            Check(boxes[0].Text == Hotkey.FromKey(key, ModifierKeys.None).ToString() && w.Overlay.IsVisible, "settings capture single key " + key);
        }
        Capture(boxes[0], Key.Q); Capture(boxes[1], Key.F8); Capture(boxes[2], Key.Space);
        w.OverlayContent.Children.OfType<Button>().Single(b => Equals(b.Content, "保存快捷键")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(m.Settings.Boss == new Hotkey(0, 0x51) && m.Settings.Previous == new Hotkey(0, 0x77) && m.Settings.Next == new Hotkey(0, 0x20), "single-key boss and switch hotkeys saved and registered through settings");
        var keys = new[] { m.Settings.Boss, m.Settings.Previous, m.Settings.Next };
        m.SuspendHotkeys();
        using (var captureProbe = new HotkeyService(_ => { })) Check(captureProbe.Configure(keys, out _), "editing temporarily releases registered single keys");
        m.ResumeHotkeys();
        using (var restoredProbe = new HotkeyService(_ => { })) Check(!restoredProbe.Configure(keys, out _), "leaving hotkey editor restores registration");
        w.Overlay.Visibility = Visibility.Collapsed; await Task.Delay(30);
    }
    private static async Task FocusLayoutRegression(BrowserManager m, BrowserWindow w)
    {
        w.UpdateLayout(); await Task.Delay(150);
        Check(!w.ToolsExpanded && !w.Sidebar.IsVisible && !w.Toolbar.IsVisible && !w.Footer.IsVisible, "navigation automatically hides sidebar, address bar and footer");
        Check(w.Web.ActualWidth > w.ActualWidth * .98 && w.Web.ActualHeight > w.ActualHeight * .94, "web page occupies over 98 percent width and 94 percent height");
        Check(w.HideButton.ActualHeight >= 24 && w.CloseButton.ActualHeight >= 24, "compact title bar leaves enough height for hide and close buttons");
        var hwnd = new WindowInteropHelper(w).Handle;
        Native.GetWindowRect(hwnd, out var bounds); var origin = new Native.POINT(); Native.ClientToScreen(hwnd, ref origin);
        Check(Math.Abs(origin.Y - bounds.Top) <= 1 && Math.Abs(origin.X - bounds.Left) <= 1, "native client area begins at window edge: no system white top band");
        Check(Native.SendMessage(hwnd, 0x007F, new IntPtr(1), IntPtr.Zero) != IntPtr.Zero, "native window has an assigned large taskbar icon");
        Check(w.Icon is not null && File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico")), "window and tray icon resources available");
        var oldReader = m.PointerReader;
        var edge = w.PointToScreen(new Point(7, 100));
        Native.POINT pointer = new() { X = (int)edge.X, Y = (int)edge.Y };
        m.PointerReader = () => pointer;
        await Task.Delay(500); Check(!w.ToolsExpanded && w.SidebarColumn.Width.Value == 0, "hovering left edge leaves tools hidden with no reserved strip");
        w.ToolsToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        w.UpdateLayout();
        var page = w.Web.PointToScreen(new Point(w.Web.ActualWidth / 2, w.Web.ActualHeight / 2));
        pointer = new() { X = (int)page.X, Y = (int)page.Y };
        await Task.Delay(600); Check(w.ToolsExpanded, "tools opened by button stay open when pointer returns to page");
        m.PointerReader = oldReader;
        w.ToolsToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        w.ToolsToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(w.ToolsExpanded && w.Sidebar.IsVisible, "title-bar Tools button opens toolbar");
        w.ToolsToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!w.ToolsExpanded, "title-bar Tools button closes toolbar");
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("window.fixture") == "42", "toolbar expansion keeps the same page instance");
    }
    private static async Task WaitFor(Func<bool> condition, string message, int timeout = 2500)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < timeout) await Task.Delay(40);
        Check(condition(), message);
    }
    private static async Task MouseRegression(BrowserManager m, BrowserWindow w)
    {
        var outside = new Native.POINT { X = -30000, Y = -30000 };
        Native.POINT? pointer = Inside(w); m.PointerReader = () => pointer;
        var hwnd = new WindowInteropHelper(w).Handle;
        m.Settings.HideOnLeave = false; m.Settings.RestoreOnHover = false;
        await Task.Delay(950); pointer = outside; await Task.Delay(600);
        Check(w.IsVisible && Native.IsWindowVisible(hwnd), "disabled auto-hide keeps actual HWND visible");
        w.ShowSettings(); pointer = Inside(w); await Task.Delay(180);
        var checkbox = w.OverlayContent.Children.OfType<CheckBox>().First();
        checkbox.IsChecked = true; checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(m.Settings.HideOnLeave, "settings checkbox invokes real enabled handler");
        pointer = outside; await Task.Delay(20); pointer = Inside(w); await Task.Delay(100);
        Check(w.IsVisible, "20ms edge excursion does not accidentally hide window");
        pointer = outside;
        await MeasureHide(w, "settings page");
        Check(!Native.IsWindowVisible(hwnd) && w.Policy.Reason == HiddenReason.MouseLeave, "settings page HWND is hidden, not merely transparent");
        pointer = Inside(w); await Task.Delay(350);
        Check(!w.IsVisible, "mouse cannot restore while restore switch is off");
        var restore = w.OverlayContent.Children.OfType<CheckBox>().Last();
        restore.IsChecked = true; restore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(m.Settings.RestoreOnHover, "visible settings auto-restore checkbox enables restore");
        pointer = outside; await Task.Delay(180); pointer = Inside(w);
        await WaitFor(() => w.IsVisible, "independent hover restore switch restores mouse-hidden window");
        Check(w.Web.Visibility == Visibility.Hidden && w.Overlay.IsVisible, "restored settings remain above native WebView");
        m.ToggleBoss(); pointer = outside; await Task.Delay(180); pointer = Inside(w); await Task.Delay(350);
        Check(!w.IsVisible && !Native.IsWindowVisible(hwnd), "boss-hidden HWND cannot be restored by pointer entry");
        m.ToggleBoss(); Check(w.IsVisible, "boss key restores manually hidden settings");
        w.Overlay.Visibility = Visibility.Collapsed; pointer = Inside(w); await Task.Delay(950);
        pointer = outside; await MeasureHide(w, "web page");
        pointer = Inside(w); await WaitFor(() => w.IsVisible, "web page: pointer return restores window");
        w.ToggleAd(); pointer = Inside(w); await Task.Delay(950); pointer = outside;
        await MeasureHide(w, "advertisement mode");
        pointer = Inside(w); await WaitFor(() => w.IsVisible, "advertisement mode: pointer return restores window"); w.ToggleAd();
        pointer = Inside(w); w.PollPointer(); await Task.Delay(35); pointer = outside;
        await MeasureHide(w, "immediately after recall");
        pointer = Inside(w); await WaitFor(() => w.IsVisible, "restore after fast recall-hide cycle");
        m.Settings.HideOnLeave = false; m.Settings.RestoreOnHover = false; pointer = null;
        m.Settings.Opacity = .5; m.SettingsChanged();
        w.Width -= 20; w.Height -= 20; w.UpdateLayout(); await Task.Delay(150);
        Check(Native.GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out _) && alpha == 128, "native opacity survives resizing");
        w.Conceal(HiddenReason.BossKey); w.Reveal(); await Task.Delay(150);
        Check(Native.GetLayeredWindowAttributes(hwnd, out _, out alpha, out _) && alpha == 128, "native opacity survives hide and recall");
        m.Settings.Opacity = 1; m.SettingsChanged();
    }
    private static async Task MeasureHide(BrowserWindow window, string scenario)
    {
        var elapsed = Stopwatch.StartNew();
        while (window.IsVisible && elapsed.ElapsedMilliseconds < 1000) await Task.Delay(5);
        elapsed.Stop();
        Check(!window.IsVisible && !Native.IsWindowVisible(new WindowInteropHelper(window).Handle), scenario + ": native window hidden");
        Check(elapsed.ElapsedMilliseconds < 250, scenario + $": hide latency {elapsed.ElapsedMilliseconds}ms (under 250ms)");
    }
    private static async Task WaitForScript(BrowserWindow w, string expression, string message)
    {
        var clock = Stopwatch.StartNew(); bool passed = false;
        while (clock.ElapsedMilliseconds < 4000)
        {
            if (await w.Web.CoreWebView2.ExecuteScriptAsync(expression) == "true") { passed = true; break; }
            await Task.Delay(25);
        }
        Check(passed, message);
    }
    private static async Task PlaybackRegression(BrowserManager m, BrowserWindow w)
    {
        await Navigate(w, "https://float.internal/playback-test.html");
        m.Settings.ResumeMediaOnShow = true; m.SettingsChanged();
        await w.Web.CoreWebView2.ExecuteScriptAsync("document.dispatchEvent(new PointerEvent('pointerdown')); startPlayback();");
        await WaitForScript(w, "!document.getElementById('playing').paused", "real muted HTML video starts playback");
        w.Conceal(HiddenReason.MouseLeave); await Task.Delay(250);
        // A read-only query can wake a suspended renderer; it does not change the media state.
        await WaitForScript(w, "document.getElementById('playing').paused", "hiding pauses a previously playing real video");
        w.Reveal();
        await WaitForScript(w, "!document.getElementById('playing').paused", "returning to window resumes previously playing video");
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('manual').paused") == "true", "user-paused video stays paused");
        w.ResumeMediaButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!m.Settings.ResumeMediaOnShow && m.Windows.All(x => x.ResumeMediaButton.Content.ToString()!.EndsWith("关")), "toolbar toggle disables media resume globally");
        w.Conceal(HiddenReason.BossKey); await Task.Delay(250);
        await WaitForScript(w, "document.getElementById('playing').paused", "video is paused while resume option is disabled");
        w.Reveal(); await Task.Delay(250);
        var playbackDebug = await w.Web.CoreWebView2.ExecuteScriptAsync("({paused:document.getElementById('playing').paused,hidden:document.hidden,states:Object.keys(window).filter(k=>k.startsWith('__float_media_')).map(k=>({marked:window[k].marked.size,pending:window[k].pending.size,wants:window[k].wantsPlayback}))})");
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('playing').paused") == "true", "disabled playback option leaves video paused on return: " + playbackDebug);
        w.ResumeMediaButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        w.Conceal(HiddenReason.MouseLeave); await Task.Delay(100); w.Reveal(); await Task.Delay(150);
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('playing').paused") == "true", "enabling option does not resurrect stale paused media");
        await w.Web.CoreWebView2.ExecuteScriptAsync("document.dispatchEvent(new PointerEvent('pointerdown')); startPlayback();");
        await WaitForScript(w, "!document.getElementById('playing').paused", "video can be restarted for fast transitions");
        w.Conceal(HiddenReason.MouseLeave); w.Reveal(); w.Conceal(HiddenReason.MouseLeave); await Task.Delay(200);
        await WaitForScript(w, "document.getElementById('playing').paused", "rapid hide/show/hide ends paused");
        w.Reveal(); await WaitForScript(w, "!document.getElementById('playing').paused", "rapid transitions preserve resume state");
        w.ShowSettings();
        await WaitForScript(w, "!document.getElementById('playing').paused", "opening settings preserves playback");
        w.Conceal(HiddenReason.BossKey); await Task.Delay(150); w.Reveal(); await Task.Delay(150);
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('playing').paused") == "false", "recalling into settings resumes policy-paused media");
        w.Overlay.Visibility = Visibility.Collapsed;
        await WaitForScript(w, "!document.getElementById('playing').paused", "returning from settings resumes previous playback");
        var playbackBounds=new Rect(w.Left,w.Top,w.Width,w.Height);
        try
        {
            w.Width=1000;w.Height=680;w.UpdateLayout();
            w.ShowSettings();
            await WaitForScript(w,"!document.getElementById('playing').paused","wide settings preserve playback in separate popup");
            w.Width=560;w.Height=310;w.UpdateLayout();
            Check(w.UsesCompactTools && w.CompactTools.IsOpen && w.Overlay.IsVisible && w.Web.IsVisible,"shrinking settings to compact mode reveals the same video beside the popup");
            await WaitForScript(w,"!document.getElementById('playing').paused","visible video resumes after wide settings become compact popup when resume is enabled");
            Check(await w.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('manual').paused")=="true","compact settings transition does not start user-paused media");
            w.Width=1000;w.Height=680;w.UpdateLayout();
            await WaitForScript(w,"!document.getElementById('playing').paused","growing settings keeps playback running");
            m.Settings.ResumeMediaOnShow=false;m.SettingsChanged();
            w.Width=560;w.Height=310;w.UpdateLayout();await Task.Delay(300);
            Check(w.Web.IsVisible && w.Overlay.IsVisible,"disabled resume still reveals the video alongside compact settings");
            Check(await w.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('playing').paused")=="false","settings layout changes do not pause media even when auto-resume is disabled");
        }
        finally
        {
            w.Width=playbackBounds.Width;w.Height=playbackBounds.Height;w.Left=playbackBounds.Left;w.Top=playbackBounds.Top;
            m.Settings.ResumeMediaOnShow=true;m.SettingsChanged();w.Overlay.Visibility=Visibility.Collapsed;
            w.SetToolsExpanded(false);w.UpdateLayout();
        }
        m.Settings.Opacity = .67; m.SettingsChanged();
        await Navigate(w, "https://float.internal/test.html");
        Check(Math.Abs(m.Settings.Opacity - .67) < .001 && Native.GetLayeredWindowAttributes(new WindowInteropHelper(w).Handle, out _, out byte alpha, out _) && alpha == 171, "navigating to a new page preserves global opacity");
        m.Settings.Opacity = 1; m.SettingsChanged();
    }
    private static async Task CompactToolsRegression(BrowserManager m,BrowserWindow w,string directory)
    {
        if(directory.Length>0)Directory.CreateDirectory(directory);
        var originalBounds=new Rect(w.Left,w.Top,w.Width,w.Height);
        double originalOpacity=m.Settings.Opacity;
        bool originalBackdrop=m.Settings.ClearWhiteBackground;
        bool originalHide=m.Settings.HideOnLeave;
        var originalPointer=m.PointerReader;
        try
        {
            m.Settings.HideOnLeave=false;m.Settings.Opacity=.32;m.Settings.ClearWhiteBackground=false;m.SettingsChanged();
            await Navigate(w,"https://float.internal/test.html");
            await w.Web.CoreWebView2.ExecuteScriptAsync("window.compactMarker='same-page'");
            foreach(var size in new[]{new Size(560,310),new Size(1000,240),new Size(320,220),new Size(160,90)})
            {
                string label=$"{size.Width:0}x{size.Height:0}";
                w.Width=size.Width;w.Height=size.Height;w.SetToolsExpanded(true);w.UpdateLayout();await Task.Delay(180);
                Check(w.UsesCompactTools && w.ToolsExpanded && w.CompactTools.IsOpen,label+": expanded tools use the separate compact popup");
                Check(w.Web.IsVisible && w.Web.ActualWidth>=w.ActualWidth-6 && w.Web.ActualHeight>=w.ActualHeight-38,label+": tools leave the full browser area available");
                Check(w.CompactToolsRoot.Opacity==1 && PresentationSource.FromVisual(w.CompactToolsRoot)!=PresentationSource.FromVisual(w),label+": tools have a separate fully opaque surface from the 32 percent host");
                if(size.Width==560)
                {
                    m.Settings.ClearWhiteBackground=true;m.SettingsChanged();w.UpdateLayout();await Task.Delay(200);
                    var popupStart=w.CompactToolsRoot.PointToScreen(new Point());
                    var popupEnd=w.CompactToolsRoot.PointToScreen(new Point(w.CompactToolsRoot.ActualWidth,w.CompactToolsRoot.ActualHeight));
                    var webStart=w.Web.PointToScreen(new Point());
                    var webEnd=w.Web.PointToScreen(new Point(w.Web.ActualWidth,w.Web.ActualHeight));
                    var overlap=Rect.Intersect(new Rect(popupStart,popupEnd),new Rect(webStart,webEnd));
                    var chosen=overlap.IsEmpty ? new Point((popupStart.X+popupEnd.X)/2,(popupStart.Y+popupEnd.Y)/2) : new Point(overlap.X+overlap.Width/2,overlap.Y+overlap.Height/2);
                    var target=Native.GetAncestor(Native.WindowFromPoint(new Native.POINT{X=(int)chosen.X,Y=(int)chosen.Y}),2);
                    var popupSource=(HwndSource)PresentationSource.FromVisual(w.CompactToolsRoot);
                    Check(target==popupSource.Handle && target!=w.TransparentInput.Handle,"transparent input surface does not intercept compact tools mouse input"+(overlap.IsEmpty?" (popup outside webpage)":" (overlapping webpage region)"));
                    var header=w.CompactToolsRoot.PointToScreen(new Point(w.CompactToolsRoot.ActualWidth/2,20));
                    uint headerColor=Native.ScreenPixel(new Native.POINT{X=(int)header.X,Y=(int)header.Y});
                    checks.Add($"INFO: compact tool header native pixel=0x{headerColor:X6}, host opacity=32 percent");
                    Check(new[]{0,8,16}.All(shift=>Math.Abs((int)((headerColor>>shift)&255)-(int)((0x00292216u>>shift)&255))<=2),"compact tool header remains opaque dark while host is transparent");
                    m.Settings.ClearWhiteBackground=false;m.SettingsChanged();w.UpdateLayout();await Task.Delay(80);
                }
                Check(w.Address.ActualWidth>=180,label+": address field retains a usable width");
                foreach(var control in new FrameworkElement[]{w.Address,w.BackButton,w.ForwardButton,w.FavoriteButton,w.MoreButton,w.OpacitySlider,w.ZoomButton,w.DeviceButton,w.ResumeMediaButton,w.BackdropButton,w.NewButton})
                    await CheckReachable(control,w.CompactToolsScroll,label+": "+control.Name);
                var workspaceButtons=VisualDescendants(w.Sidebar).OfType<Button>().Where(b=>b.Content is string text && (text.Contains("起始页") || text.Contains("我的收藏") || text.Contains("最近访问") || text.Contains("快捷键与设置"))).ToArray();
                Check(workspaceButtons.Length==4,label+": all workspace destinations remain present");
                w.CompactToolsScroll.ScrollToTop();await Task.Delay(40);
                workspaceButtons[0].RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120){RoutedEvent=Mouse.PreviewMouseWheelEvent});
                await Task.Delay(40);
                Check(w.CompactToolsScroll.VerticalOffset>0,label+": mouse wheel over nested sidebar scrolls the shared tools viewport");
                foreach(var button in workspaceButtons)await CheckReachable(button,w.CompactToolsScroll,label+": "+button.Content);
                Check(w.WindowList.Children.Count==m.Windows.Count,label+": all retained windows remain selectable");
                foreach(var button in w.WindowList.Children.OfType<Grid>().SelectMany(row=>row.Children.OfType<Button>()))await CheckReachable(button,w.CompactToolsScroll,label+": window selector / close action");
                w.CompactToolsScroll.ScrollToTop();w.SidebarScroll.ScrollToTop();await Task.Delay(60);
                if(directory.Length>0)
                {
                    await Capture(w,Path.Combine(directory,"compact-page-"+label+".png"));
                    CaptureElement(w.CompactToolsRoot,Path.Combine(directory,"compact-tools-"+label+".png"));
                }
                w.ShowSettings();w.UpdateLayout();await Task.Delay(100);
                Check(w.CompactTools.IsOpen && w.Overlay.IsVisible && w.Web.IsVisible,label+": settings open in tools without covering or hiding the main web control");
                var boxes=w.OverlayContent.Children.OfType<Grid>().SelectMany(g=>g.Children.OfType<TextBox>()).Where(b=>Equals(b.Tag,"HotkeyCapture")).ToArray();
                Check(boxes.Length==3,label+": all shortcut editors remain available");
                foreach(var box in boxes)await CheckReachable(box,w.OverlayScroll,label+": shortcut editor");
                var save=w.OverlayContent.Children.OfType<Button>().Single(b=>Equals(b.Content,"保存快捷键"));
                await CheckReachable(save,w.OverlayScroll,label+": save shortcuts");
                w.OverlayScroll.ScrollToTop();await Task.Delay(60);
                if(directory.Length>0)CaptureElement(w.CompactToolsRoot,Path.Combine(directory,"compact-settings-"+label+".png"));
                var dismiss=VisualDescendants(w.Overlay).OfType<Button>().Single(b=>Equals(b.Content,"关闭 ×"));
                dismiss.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(w.Overlay.Visibility==Visibility.Collapsed && w.CompactTools.IsOpen,label+": settings dismiss returns to compact tools");
                Check(await w.Web.CoreWebView2.ExecuteScriptAsync("window.compactMarker==='same-page'")=="true",label+": resizing and tools preserve the same page instance");
            }
            // Both surfaces belong to the active browser for mouse-leave detection.
            var popupPoint=w.CompactToolsRoot.PointToScreen(new Point(w.CompactToolsRoot.ActualWidth/2,20));
            m.PointerReader=()=>new Native.POINT{X=(int)popupPoint.X,Y=(int)popupPoint.Y};
            m.Settings.HideOnLeave=true;m.SettingsChanged();await Task.Delay(300);
            Check(w.IsVisible && w.CompactTools.IsOpen,"moving from a tiny browser into its tools does not auto-hide either surface");
            m.Settings.HideOnLeave=false;m.SettingsChanged();
            w.Conceal(HiddenReason.BossKey);await Task.Delay(60);
            Check(!w.CompactTools.IsOpen && !w.IsVisible,"boss hide also closes compact tools popup");
            w.Reveal();await Task.Delay(100);
            Check(w.CompactTools.IsOpen && w.ToolsExpanded,"recall restores the previously opened compact tools");
            w.Width=1000;w.Height=680;w.UpdateLayout();await Task.Delay(150);
            Check(!w.UsesCompactTools && !w.CompactTools.IsOpen && w.ToolsExpanded,"resizing wide restores the ordinary layout while preserving tools state");
            Check(w.Sidebar.IsVisible && w.Toolbar.IsVisible && w.Footer.IsVisible && w.Web.ActualHeight>300,"restored wide layout keeps toolbar and webpage usable");
            Check(await w.Web.CoreWebView2.ExecuteScriptAsync("window.compactMarker==='same-page'")=="true","compact-to-wide transition retains page instance");
        }
        finally
        {
            m.PointerReader=originalPointer;m.Settings.HideOnLeave=originalHide;
            m.Settings.Opacity=originalOpacity;m.Settings.ClearWhiteBackground=originalBackdrop;
            w.Overlay.Visibility=Visibility.Collapsed;w.SetToolsExpanded(false);
            w.Width=originalBounds.Width;w.Height=originalBounds.Height;w.Left=originalBounds.Left;w.Top=originalBounds.Top;
            m.SettingsChanged();
        }
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);yield return child;
            foreach(var descendant in VisualDescendants(child))yield return descendant;
        }
    }
    private static async Task CheckReachable(FrameworkElement control,ScrollViewer viewer,string label)
    {
        control.BringIntoView();await Task.Delay(25);viewer.UpdateLayout();
        var bounds=control.TransformToAncestor(viewer).TransformBounds(new Rect(0,0,control.ActualWidth,control.ActualHeight));
        Check(control.IsVisible && control.ActualWidth>15 && control.ActualHeight>10 && bounds.Left>=-1 && bounds.Top>=-1 && bounds.Right<=viewer.ActualWidth+1 && bounds.Bottom<=viewer.ActualHeight+1,label+" can be scrolled fully into view");
    }
    private static void CaptureElement(FrameworkElement element,string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);element.UpdateLayout();
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
        bitmap.Render(element);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=File.Create(path);encoder.Save(stream);
    }
    private static async Task RenderInspection(BrowserManager m, BrowserWindow w, string directory)
    {
        Directory.CreateDirectory(directory);
        await Navigate(w, "https://float.internal/test.html");
        await Capture(w, Path.Combine(directory, "page-focused.png"));
        w.SetToolsExpanded(true); await Capture(w, Path.Combine(directory, "page-tools-expanded.png"));
        await Navigate(w, "https://float.internal/home.html");
        Check(!w.Sidebar.IsVisible && w.ToolsToggle.IsVisible, "start page also hides tools until button is clicked");
        w.SetToolsExpanded(true);
        w.Width = 1180; w.Height = 790;
        await Capture(w, Path.Combine(directory, "home.png"));
        var menu = w.CreateMoreMenu(); menu.PlacementTarget = w.MoreButton; menu.IsOpen = true;
        await Task.Delay(200); menu.UpdateLayout();
        Check(((SolidColorBrush)menu.Background).Color == (Color)ColorConverter.ConvertFromString("#19272E"), "more menu has explicit opaque dark background");
        Check(menu.Opacity == 1, "menu opacity stays fully readable");
        var menuImage = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32); menuImage.Render(menu);
        var menuEncoder = new PngBitmapEncoder(); menuEncoder.Frames.Add(BitmapFrame.Create(menuImage));
        using (var file = File.Create(Path.Combine(directory, "more-menu.png"))) menuEncoder.Save(file);
        menu.IsOpen = false;
        w.ShowSettings(); await Capture(w, Path.Combine(directory, "settings.png"));
        w.Width = 800; w.Height = 480; await Capture(w, Path.Combine(directory, "settings-small.png"));
        w.OverlayScroll.ScrollToEnd(); w.SidebarScroll.ScrollToEnd();
        await Capture(w, Path.Combine(directory, "settings-small-scrolled.png"));
        Check(w.OverlayScroll.VerticalOffset > 0 && w.SidebarScroll.VerticalOffset > 0, "small-window settings and five-window sidebar are scrollable");
        w.OverlayScroll.ScrollToTop(); w.SidebarScroll.ScrollToTop();
        w.Overlay.Visibility = Visibility.Collapsed; await Capture(w, Path.Combine(directory, "home-small.png"));
        w.SetToolsExpanded(false);w.Width=240;w.Height=160;await Capture(w,Path.Combine(directory,"window-240x160.png"));
        w.Width=160;w.Height=90;await Capture(w,Path.Combine(directory,"window-160x90.png"));
        Check(w.ToolsToggle.IsVisible && w.CloseButton.IsVisible && w.CloseButton.ActualWidth>20,"tiny window retains tools and close buttons");
        w.Width = 1180; w.Height = 790; w.ToggleAd(); await Capture(w, Path.Combine(directory, "advertisement.png")); w.ToggleAd();
        Check(true, "rendered full/small home, settings, and advertisement layouts");
    }
    private static async Task Capture(BrowserWindow w, string path)
    {
        w.UpdateLayout(); await Task.Delay(300);
        int width = (int)Math.Ceiling(w.ActualWidth), height = (int)Math.Ceiling(w.ActualHeight);
        var shell = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); shell.Render(w);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawImage(shell, new Rect(0,0,width,height));
            // HWND content is captured by WebView2's supported API, then placed at its measured layout rectangle.
            if (w.Web.IsVisible && w.Overlay.Visibility != Visibility.Visible)
            {
                using var stream = new MemoryStream(); await w.Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream); stream.Position = 0;
                var page = new BitmapImage(); page.BeginInit(); page.CacheOption = BitmapCacheOption.OnLoad; page.StreamSource = stream; page.EndInit();
                var point = w.Web.TranslatePoint(new Point(0,0), w);
                dc.DrawImage(page, new Rect(point, new Size(w.Web.ActualWidth, w.Web.ActualHeight)));
            }
        }
        var output = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); output.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(output)); using var file = File.Create(path); encoder.Save(file);
    }
}


