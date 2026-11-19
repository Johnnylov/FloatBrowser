using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace FloatBrowser;

internal static partial class Verification
{
    private static async Task ResizeRegression(BrowserManager manager, BrowserWindow window)
    {
        var originalBounds=new Rect(window.Left,window.Top,window.Width,window.Height);
        bool originalBackdrop=manager.Settings.ClearWhiteBackground;
        bool originalHide=manager.Settings.HideOnLeave;
        double originalOpacity=manager.Settings.Opacity,originalZoom=manager.Settings.Zoom;
        var handle=new WindowInteropHelper(window).Handle;
        try
        {
            manager.Activate(window);
            window.Overlay.Visibility=Visibility.Collapsed;window.SetToolsExpanded(false);
            manager.Settings.HideOnLeave=false;manager.Settings.Opacity=.67;manager.Settings.Zoom=1;
            await Navigate(window,"https://float.internal/test.html");
            await window.Web.CoreWebView2.ExecuteScriptAsync("window.resizeMarker='same-page'");
            foreach(bool backdrop in new[]{false,true})
            {
                string mode=backdrop?"transparent":"ordinary";
                manager.Settings.ClearWhiteBackground=backdrop;manager.SettingsChanged();
                window.Width=1000;window.Height=680;window.UpdateLayout();await Task.Delay(180);
                var hidden=manager.Windows.Where(w=>w!=window).ToArray();
                var hiddenBounds=hidden.ToDictionary(w=>w,w=>new Rect(w.Left,w.Top,w.Width,w.Height));
                var hiddenApplications=hidden.ToDictionary(w=>w,w=>w.SharedPlacementApplications);
                double viewportBefore=double.Parse(await window.Web.CoreWebView2.ExecuteScriptAsync("innerWidth"),System.Globalization.CultureInfo.InvariantCulture);
                double zoomBefore=window.Web.ZoomFactor;
                Native.SendMessage(handle,0x0231,IntPtr.Zero,IntPtr.Zero);
                Check(window.IsInteractiveResize,mode+": native enter-size/move message enables interactive resize path");
                int layouts=window.ChromeLayoutPasses,opacityWrites=window.NativeOpacityUpdates;
                var costs=new List<double>();
                for(int frame=0;frame<32;frame++)
                {
                    var elapsed=Stopwatch.StartNew();
                    window.Width=900+frame*7;window.Height=600+frame*3;
                    window.UpdateLayout();elapsed.Stop();costs.Add(elapsed.Elapsed.TotalMilliseconds);
                    await Task.Delay(8);
                }
                Check(hidden.All(w=>w.SharedPlacementApplications==hiddenApplications[w] && new Rect(w.Left,w.Top,w.Width,w.Height)==hiddenBounds[w]),mode+": dragging does not resize hidden WebViews on every frame");
                Check(window.ChromeLayoutPasses-layouts<=1,mode+": 32 resize frames avoid repeated toolbar layout work");
                Check(window.NativeOpacityUpdates==opacityWrites,mode+": 32 resize frames do not rewrite native opacity");
                Check(manager.Settings.WindowPlacement is {} pending && Math.Abs(pending.Width-window.Width)<1 && Math.Abs(pending.Height-window.Height)<1,mode+": shared preference tracks latest drag bounds before release");
                double viewportAfter=double.Parse(await window.Web.CoreWebView2.ExecuteScriptAsync("innerWidth"),System.Globalization.CultureInfo.InvariantCulture);
                Check(viewportAfter>viewportBefore+50 && window.Web.ZoomFactor==zoomBefore,mode+": webpage viewport follows live resize without proportional content zoom");
                Check(Native.GetLayeredWindowAttributes(handle,out uint key,out byte alpha,out uint flags) && alpha==171 && flags==(backdrop?3u:2u) && (!backdrop || key==Native.BackdropKey),mode+": native transparency survives continuous resize without being reset");
                if(backdrop)
                {
                    Check(!Native.IsWindowVisible(window.TransparentInput.Handle),"native edge drag avoids repainting the unused transparent page input surface");
                }
                Native.SendMessage(handle,0x0232,IntPtr.Zero,IntPtr.Zero);
                await Task.Delay(120);
                Check(!window.IsInteractiveResize,mode+": native exit-size/move message commits resize");
                if(backdrop)
                {
                    Check(Native.IsWindowVisible(window.TransparentInput.Handle) && Native.GetWindowRect(window.TransparentInput.Handle,out _),"releasing the edge restores the transparent page input surface");
                    Native.GetWindowRect(window.TransparentInput.Handle,out var input);
                    var first=window.Web.PointToScreen(new Point());
                    var last=window.Web.PointToScreen(new Point(window.Web.ActualWidth,window.Web.ActualHeight));
                    Check(Math.Abs(input.Left-first.X)<=1 && Math.Abs(input.Top-first.Y)<=1 && Math.Abs(input.Right-last.X)<=1 && Math.Abs(input.Bottom-last.Y)<=1,"restored transparent input aligns with final webpage bounds");
                    Check(Native.GetLayeredWindowAttributes(window.TransparentInput.Handle,out _,out byte inputAlpha,out _) && inputAlpha==1,"transparent input keeps alpha1 across drag hide and recall");
                }
                Check(hidden.All(w=>Math.Abs(w.Width-window.Width)<1 && Math.Abs(w.Height-window.Height)<1),mode+": hidden windows receive the final shared size on release");
                Check(hidden.All(w=>w.SharedPlacementApplications-hiddenApplications[w]<=1),mode+": each hidden window receives at most one final geometry update");
                Check(await window.Web.CoreWebView2.ExecuteScriptAsync("window.resizeMarker==='same-page'")=="true",mode+": resize keeps the original webpage instance");
                var ordered=costs.OrderBy(x=>x).ToArray();
                checks.Add($"INFO: {mode} resize, 32 programmatic native-size/move frames: synchronous layout avg={costs.Average():0.00}ms p95={ordered[(int)Math.Floor((ordered.Length-1)*.95)]:0.00}ms; excludes waits and does not measure display FPS");
            }
        }
        finally
        {
            if(window.IsInteractiveResize)Native.SendMessage(handle,0x0232,IntPtr.Zero,IntPtr.Zero);
            manager.Settings.ClearWhiteBackground=originalBackdrop;manager.Settings.HideOnLeave=originalHide;
            manager.Settings.Opacity=originalOpacity;manager.Settings.Zoom=originalZoom;
            window.Width=originalBounds.Width;window.Height=originalBounds.Height;window.Left=originalBounds.Left;window.Top=originalBounds.Top;
            manager.SettingsChanged();window.UpdateLayout();
        }
    }

    private static Button WindowListButton(BrowserWindow host,BrowserWindow target,string tag)
    {
        var row=host.WindowList.Children.OfType<Grid>().Single(r=>ReferenceEquals(r.Tag,target));
        return row.Children.OfType<Button>().Single(b=>Equals(b.Tag,tag));
    }
    private static async Task WindowListCloseRegression(BrowserManager manager,BrowserWindow window)
    {
        manager.Activate(window);
        window.Width=1000;window.Height=680;window.SetToolsExpanded(true);window.UpdateLayout();
        Check(manager.Windows.Count==5,"window-list close regression begins with five retained pages");
        Check(window.WindowList.Children.OfType<Grid>().Count()==manager.Windows.Count,"each retained page has a dedicated window-list row");
        foreach(var target in manager.Windows)
        {
            var activate=WindowListButton(window,target,"ActivateWindow");
            var close=WindowListButton(window,target,"CloseWindow");
            Check(!ReferenceEquals(activate,close) && close.IsVisible && close.ActualWidth>=20,"page row exposes separate activate and close controls");
        }
        await window.Web.CoreWebView2.ExecuteScriptAsync("window.listCloseMarker='unchanged'");
        var hidden=manager.Windows.First(w=>w!=window);
        WindowListButton(window,hidden,"CloseWindow").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(80);
        Check(manager.Windows.Count==4 && !manager.Windows.Contains(hidden),"window-list close removes a hidden webpage rather than merely hiding it");
        Check(manager.Current==window && window.IsVisible,"closing a background webpage leaves the current webpage active");
        Check(await window.Web.CoreWebView2.ExecuteScriptAsync("window.listCloseMarker==='unchanged'")=="true","closing a background page preserves the current page instance");
        Check(window.WindowList.Children.Count==4 && window.NewButton.IsEnabled,"list and new-window availability update after closing a page");
        var replacement=manager.CreateWindow("https://float.internal/test.html");
        Check(replacement is not null,"new page can occupy the released window slot");
        await replacement!.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        replacement.SetToolsExpanded(true);replacement.UpdateLayout();await Task.Delay(80);
        WindowListButton(replacement,replacement,"CloseWindow").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(80);
        Check(manager.Windows.Count==4 && !manager.Windows.Contains(replacement),"window-list close removes the active webpage");
        Check(manager.Current is not null && manager.Windows.Contains(manager.Current) && manager.Current.IsVisible,"closing the active page recalls a remaining webpage");
        var finalReplacement=manager.CreateWindow("https://float.internal/test.html");
        Check(finalReplacement is not null,"closing the active page also frees a window slot");
        await finalReplacement!.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        manager.Activate(window);window.SetToolsExpanded(false);
        Check(manager.Windows.Count==5 && manager.Current==window,"window-list regression restores five pages and the original active page");
    }
}
