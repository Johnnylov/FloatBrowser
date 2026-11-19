using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FloatBrowser;
internal static partial class Verification
{
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);

    private static async Task ResizePlayerRegression(BrowserManager m, BrowserWindow w, string[] args)
    {
        m.Settings.HideOnLeave=false; m.Settings.Opacity=.8; m.Settings.Zoom=1;
        w.Left=80; w.Top=80; w.SetToolsExpanded(false);
        await Navigate(w,"https://float.internal/fullscreen-test.html");
        await w.Web.CoreWebView2.ExecuteScriptAsync("window.instanceMarker=123;document.getElementById('player').style.height='calc(100vh - 20px)'");
        var handle=new WindowInteropHelper(w).Handle;
        foreach(bool transparent in new[]{false,true})
        {
            m.Settings.ClearWhiteBackground=transparent; m.SettingsChanged();
            foreach(var size in new[]{new Size(1100,700),new Size(320,210),new Size(1100,700),new Size(160,90),new Size(1000,680)})
            {
                Native.SendMessage(handle,0x0231,IntPtr.Zero,IntPtr.Zero);
                w.Width=size.Width; w.Height=size.Height; w.UpdateLayout();
                Native.SendMessage(handle,0x0232,IntPtr.Zero,IntPtr.Zero);
                w.UpdateLayout(); await Task.Delay(250);
                var viewport=await w.Web.CoreWebView2.ExecuteScriptAsync("JSON.stringify({w:innerWidth,h:innerHeight,marker:window.instanceMarker})");
                using var dimensions=JsonDocument.Parse(JsonSerializer.Deserialize<string>(viewport)!);
                var d=dimensions.RootElement;
                string label=$"{(transparent?"transparent":"ordinary")} {size.Width}x{size.Height}";
                checks.Add($"INFO: {label}: web={w.Web.ActualWidth}x{w.Web.ActualHeight}, viewport={d}");
                Check(Math.Abs(d.GetProperty("w").GetDouble()-w.Web.ActualWidth)<2 && Math.Abs(d.GetProperty("h").GetDouble()-w.Web.ActualHeight)<2,label+": viewport matches final host size");
                Check(d.GetProperty("marker").GetInt32()==123,label+": same page retained");
                var region=CreateRectRgn(0,0,0,0);
                try
                {
                    int kind=GetWindowRgn(handle,region);
                    var p=w.Web.PointToScreen(new Point(w.Web.ActualWidth-20,w.Web.ActualHeight-20));
                    Native.GetWindowRect(handle,out var rect);
                    Check(kind==0 || PtInRegion(region,(int)p.X-rect.Left,(int)p.Y-rect.Top),label+": restored window is not clipped to old smaller region");
                }
                finally { DeleteObject(region); }
            }
        }
        m.Settings.ClearWhiteBackground=true; m.SettingsChanged(); await Task.Delay(200);
        await NativeMouse(w,"seek",0x200);
        await WaitForScript(w,"getComputedStyle(document.getElementById('bar')).visibility==='visible'","inline player controls appear after shrink and restore");
        await FullscreenRegression(m,w);
        string playerUrl=Argument(args,"--player-url","");
        if(playerUrl.Length>0)
        {
            m.Settings.ClearWhiteBackground=true;m.SettingsChanged();
            await Navigate(w,playerUrl);await Task.Delay(10000);
            foreach(var size in new[]{new Size(892,690),new Size(360,240),new Size(892,690)})
            {
                Native.SendMessage(handle,0x0231,IntPtr.Zero,IntPtr.Zero);
                w.Width=size.Width;w.Height=size.Height;w.UpdateLayout();
                Native.SendMessage(handle,0x0232,IntPtr.Zero,IntPtr.Zero);
                await Task.Delay(400);
                checks.Add("INFO: live player "+size+": "+await w.Web.CoreWebView2.ExecuteScriptAsync("(()=>{const p=document.querySelector('.bpx-player-container');const c=document.querySelector('.bpx-player-control-wrap');return {viewport:[innerWidth,innerHeight],player:p?.getBoundingClientRect().toJSON(),bar:c?.getBoundingClientRect().toJSON(),barOpacity:c&&getComputedStyle(c).opacity,barVisibility:c&&getComputedStyle(c).visibility,scroll:[scrollX,scrollY]}})()"));
            }
        }
        string directory=Argument(args,"--render-dir","");
        if(directory.Length>0) { Directory.CreateDirectory(directory); await Capture(w,Path.Combine(directory,"resize-player.png")); }
    }
}
