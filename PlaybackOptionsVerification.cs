using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace FloatBrowser;
internal static partial class Verification
{
    private static async Task PlaybackOptionsRegression(BrowserManager m,BrowserWindow w,string[] args)
    {
        const string playing="!document.getElementById('playing').paused";
        m.Settings.BackgroundPlayback=false;m.Settings.ResumeMediaOnShow=true;m.SettingsChanged();
        await Navigate(w,"https://float.internal/playback-test.html");await Task.Delay(200);
        await w.Web.CoreWebView2.ExecuteScriptAsync("startPlayback()");
        await WaitForScript(w,playing,"foreground playback starts");
        w.ShowSettings();await Task.Delay(200);
        Check(w.CompactTools.IsOpen && w.Web.IsVisible,"settings use independent opaque popup without hiding WebView");
        await WaitForScript(w,playing,"settings do not interrupt playback");
        w.Overlay.Visibility=Visibility.Collapsed;w.SetToolsExpanded(false);
        w.Conceal(HiddenReason.BossKey);await WaitForScript(w,"document.getElementById('playing').paused","background disabled pauses on boss key");
        w.Reveal();await WaitForScript(w,playing,"foreground restores policy-paused media");
        m.Settings.BackgroundPlayback=true;m.SettingsChanged();await Task.Delay(200);
        w.Conceal(HiddenReason.BossKey);await Task.Delay(400);
        await WaitForScript(w,playing,"background enabled keeps boss-hidden media playing");
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("document.hidden")=="false","Chromium visibility remains visible for background playback");
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('manual').paused")=="true","manually paused media never starts");
        m.SaveNow();Check(new DataStore(m.Store.Root).Data.Settings.BackgroundPlayback,"background switch persists to the local profile");
        m.Settings.BackgroundPlayback=false;m.SettingsChanged();await WaitForScript(w,"document.getElementById('playing').paused","turning background off immediately pauses hidden media");
        m.Settings.BackgroundPlayback=true;m.SettingsChanged();await WaitForScript(w,playing,"turning background on restores policy-paused media");
        w.Reveal();w.WindowState=WindowState.Minimized;await Task.Delay(200);
        await WaitForScript(w,playing,"minimized media continues with background enabled");w.WindowState=WindowState.Normal;
        var child=m.CreateWindow("https://float.internal/playback-test.html")!;
        await child.Ready.Task;await Task.Delay(600);
        await child.Web.CoreWebView2.ExecuteScriptAsync("startPlayback()");
        await WaitForScript(child,playing,"second window starts playback");
        await WaitForScript(w,playing,"switching windows preserves previous playback");
        await w.PollMediaState();await child.PollMediaState();
        Check(w.PlayingMedia && child.PlayingMedia,"both playback indicators reflect active video");
        await m.PauseAllMedia();await WaitForScript(child,"document.getElementById('playing').paused","pause-all stops foreground media");
        await WaitForScript(w,"document.getElementById('playing').paused","pause-all stops background media");
        m.Activate(w);await Task.Delay(200);
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('playing').paused")=="true","pause-all is not undone by returning to a window");child.Close();
        m.Settings.BackgroundPlayback=false;m.Settings.ResumeMediaOnShow=false;m.SettingsChanged();
        await w.Web.CoreWebView2.ExecuteScriptAsync("document.dispatchEvent(new PointerEvent('pointerdown'));startPlayback()");
        await WaitForScript(w,playing,"manual restart succeeds after pause-all");
        w.Conceal(HiddenReason.BossKey);await Task.Delay(150);w.Reveal();await Task.Delay(200);
        Check(await w.Web.CoreWebView2.ExecuteScriptAsync("document.getElementById('playing').paused")=="true","disabled auto-resume leaves media paused");
        // Cross-origin child frame uses the same local fixture, a separate origin.
        w.Web.CoreWebView2.SetVirtualHostNameToFolderMapping("frame.internal",System.IO.Path.Combine(AppContext.BaseDirectory,"Assets"),CoreWebView2HostResourceAccessKind.Allow);
        CoreWebView2Frame? embedded=null;
        void FrameCreated(object? s,CoreWebView2FrameCreatedEventArgs e)=>embedded=e.Frame;
        w.Web.CoreWebView2.FrameCreated+=FrameCreated;
        await w.Web.CoreWebView2.ExecuteScriptAsync("const f=document.createElement('iframe');f.src='https://frame.internal/playback-test.html';document.body.append(f)");
        for(int i=0;i<100 && embedded is null;i++)await Task.Delay(30);
        Check(embedded is not null,"cross-origin media frame created");await Task.Delay(500);
        await embedded!.ExecuteScriptAsync("startPlayback()");await Task.Delay(200);
        m.Settings.BackgroundPlayback=true;m.SettingsChanged();await Task.Delay(150);w.Conceal(HiddenReason.BossKey);await Task.Delay(250);
        Check(await embedded.ExecuteScriptAsync(playing)=="true","cross-origin frame continues in background");
        m.Settings.BackgroundPlayback=false;m.SettingsChanged();await Task.Delay(250);
        Check(await embedded.ExecuteScriptAsync("document.getElementById('playing').paused")=="true","cross-origin frame pauses when background switch turns off");
        w.Web.CoreWebView2.FrameCreated-=FrameCreated;w.Reveal();
        await Navigate(w,"https://float.internal/test.html");
        m.Settings.ClearWhiteBackground=true;m.SettingsChanged();await Task.Delay(200);
        await w.Web.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML='<a id=plain target=_blank href=\"https://float.internal/home.html\">ordinary link</a>'");
        int before=m.Windows.Count;await NativeClick(w,"plain");await Task.Delay(500);
        Check(m.Windows.Count==before && w.Web.CoreWebView2.Source=="https://float.internal/home.html","ordinary target-blank link navigates current window");
        Check(w.Web.CoreWebView2.CanGoBack,"current-window navigation retains back history");
        w.Web.CoreWebView2.GoBack();await Task.Delay(300);
        Check(w.Web.CoreWebView2.CanGoForward,"back exposes forward navigation");
        await w.Web.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML='<a id=explicit target=_blank href=\"https://float.internal/home.html\">new window</a>'");
        await NativeMouse(w,"explicit",0x200);await NativeMouse(w,"explicit",0x201,9);await NativeMouse(w,"explicit",0x202,8);await Task.Delay(500);
        Check(m.Windows.Count==before+1,"Ctrl-click preserves explicit new-window action");
        var explicitChild=m.Current!;explicitChild.Close();m.Activate(w);
        await w.Web.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML='<button id=popup>Login popup</button>';document.getElementById('popup').onclick=()=>window.open('https://float.internal/test.html','login','width=400,height=300')");
        await NativeClick(w,"popup");await Task.Delay(500);
        Check(m.Windows.Count==before+1,"named scripted login popup remains a separate window");
        var popup=m.Current!;
        Check(await popup.Web.CoreWebView2.ExecuteScriptAsync("window.opener!==null")=="true","popup keeps its opener for authorization callbacks");
        await popup.Web.CoreWebView2.ExecuteScriptAsync("window.close()");await Task.Delay(200);
        Check(m.Windows.Count==before,"scripted popup can close itself without closing parent");m.Activate(w);
        Check(LinkRouting.IsAuthorization("https://example.test/oauth/authorize?response_type=code"),"authorization routes retain popup semantics");
        var tip=new ToolTip { Content="隐藏到托盘（老板键可召回）" };tip.Style=(Style)Application.Current.FindResource(typeof(ToolTip));
        string directory=Argument(args,"--render-dir","");
        if(directory.Length>0)
        {
            tip.PlacementTarget=w.HideButton;tip.IsOpen=true;await Task.Delay(150);
            CaptureElement(tip,System.IO.Path.Combine(directory,"tooltip.png"));tip.IsOpen=false;
            w.ShowSettings();await Task.Delay(150);w.OverlayScroll.ScrollToEnd();await Task.Delay(80);
            CaptureElement(w.CompactToolsRoot,System.IO.Path.Combine(directory,"playback-settings.png"));
            w.Overlay.Visibility=Visibility.Collapsed;w.SetToolsExpanded(false);
        }
        Check(tip.Background.ToString()=="#FF19272E" && tip.Foreground.ToString()=="#FFFFFFFF" && tip.Opacity==1,"tooltip has explicit opaque dark background and white text");
    }
}
