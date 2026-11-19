using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace FloatBrowser;

// A topmost browser must not cover or intercept the shell's taskbar. Exclude
// its actual screen rectangle from both the visual HWND and the input HWND.
internal sealed class TaskbarGuard : IDisposable
{
    private readonly BrowserWindow owner;
    private readonly DispatcherTimer timer;
    private readonly Dictionary<IntPtr,string> last=new();
    private static List<Native.RECT> cachedAreas=new();
    private static long cacheUntil;
    internal static int ShellQueries { get; private set; }
    internal static int RegionUpdates { get; private set; }
    public TaskbarGuard(BrowserWindow owner)
    {
        this.owner=owner;
        timer=new DispatcherTimer(TimeSpan.FromMilliseconds(100),DispatcherPriority.Background,(_,_)=>Sync(),owner.Dispatcher);
    }
    internal static List<Native.RECT> Areas()
    {
        long now=Environment.TickCount64;
        if(now<cacheUntil)return cachedAreas;
        ShellQueries++;
        var result=new List<Native.RECT>();
        foreach(var name in new[]{"Shell_TrayWnd","Shell_SecondaryTrayWnd","NotifyIconOverflowWindow"})
        {
            IntPtr h=IntPtr.Zero;
            while((h=FindWindowEx(IntPtr.Zero,h,name,null))!=IntPtr.Zero)
                if(Native.IsWindowVisible(h) && Native.GetWindowRect(h,out var rect)) result.Add(rect);
        }
        cachedAreas=result;cacheUntil=now+100;
        return cachedAreas;
    }
    public void Sync()
    {
        if(!owner.IsVisible)return;
        var areas=Areas();
        Clip(new WindowInteropHelper(owner).Handle,areas);
        Clip(owner.TransparentInput.Handle,areas);
    }
    private void Clip(IntPtr hwnd,List<Native.RECT> areas)
    {
        if(hwnd==IntPtr.Zero || !Native.GetWindowRect(hwnd,out var r))return;
        var cuts=new List<Native.RECT>();
        foreach(var a in areas)
        {
            var c=new Native.RECT{Left=Math.Max(r.Left,a.Left)-r.Left,Top=Math.Max(r.Top,a.Top)-r.Top,Right=Math.Min(r.Right,a.Right)-r.Left,Bottom=Math.Min(r.Bottom,a.Bottom)-r.Top};
            if(c.Right>c.Left && c.Bottom>c.Top)cuts.Add(c);
        }
        // An unclipped window stays unclipped at every size. Including its
        // dimensions here used to reset the region and redraw on every frame.
        string key=cuts.Count==0?"clear":$"{r.Right-r.Left},{r.Bottom-r.Top}:"+string.Join(";",cuts.ConvertAll(c=>$"{c.Left},{c.Top},{c.Right},{c.Bottom}"));
        if(last.TryGetValue(hwnd,out var old) && old==key)return;
        last[hwnd]=key;
        if(cuts.Count==0)
        {
            // New HWNDs have no region from this guard to remove.
            if(old is not null){SetWindowRgn(hwnd,IntPtr.Zero,true);RegionUpdates++;}
            return;
        }
        var region=CreateRectRgn(0,0,r.Right-r.Left,r.Bottom-r.Top);
        foreach(var c in cuts)
        {
            var cut=CreateRectRgn(c.Left,c.Top,c.Right,c.Bottom);
            CombineRgn(region,region,cut,4);DeleteObject(cut);
        }
        if(SetWindowRgn(hwnd,region,true)==0)DeleteObject(region); // success transfers ownership to Windows
        else RegionUpdates++;
    }
    public void Dispose(){timer.Stop();last.Clear();}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string name,string? title);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd,IntPtr region,bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l,int t,int r,int b);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest,IntPtr first,IntPtr second,int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
}
