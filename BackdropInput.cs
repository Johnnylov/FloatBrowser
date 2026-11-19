using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FloatBrowser;

// A non-activating, 1/255-alpha input surface decouples mouse hit testing from
// the host's color key. Dispatch pointer events through Chromium's input API:
// posting WM_MOUSEMOVE to an HWND covered by this surface races Chromium's
// native mouse-leave tracking and causes hover-driven player controls to vanish.
internal sealed class BackdropInput : IDisposable
{
    private readonly BrowserWindow owner;
    private HwndSource? source;
    private Rect bounds;
    private bool visible, pinned;
    private IntPtr target;
    private readonly LinkedList<(string Type, string Json, int Generation, bool Cleanup)> pending = new();
    private bool dispatching, disposed;
    private int generation, clickCount=1;
    private string? heldButton;
    internal IntPtr Handle => source?.Handle ?? IntPtr.Zero;
    internal IntPtr Renderer => FindRenderer();
    public BackdropInput(BrowserWindow owner) { this.owner = owner; }

    public void Sync()
    {
        bool show = owner.IsVisible && !owner.IsInteractiveResize && owner.IsBackdropActive && owner.Web.IsVisible && owner.Web.CoreWebView2 is not null && owner.WindowState != WindowState.Minimized;
        // Windows owns mouse capture during a native edge drag. There is no page
        // interaction to route until release, so avoid repainting a second layered
        // surface for every pixel. Never release the owner's native sizing capture.
        if (!show) { if (visible) { ResetPointer(); ShowWindow(Handle, 0); visible = false; } return; }
        var p = owner.Web.PointToScreen(new Point());
        var q = owner.Web.PointToScreen(new Point(owner.Web.ActualWidth, owner.Web.ActualHeight));
        var next = new Rect(p, q);
        if (next.Width < 1 || next.Height < 1) return;
        if (source is null)
        {
            source = new HwndSource(new HwndSourceParameters("FloatBrowser.TransparentInput") {
                ParentWindow = new WindowInteropHelper(owner).Handle,
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x08080080, WindowClassStyle=8,
                Width = 1, Height = 1
            });
            source.AddHook(Hook);
            source.AddHook(Native.LayeredStyleHook);
            source.CompositionTarget.BackgroundColor = Colors.Black;
            Native.SetInputOpacity(Handle);
        }
        if (!visible || bounds != next || pinned != owner.Topmost)
        {
            bool zOrderChanged=!visible || pinned!=owner.Topmost;
            // Keep the existing alpha and z-order for geometry-only updates.
            // The style hook preserves WS_EX_LAYERED while HwndTarget resizes.
            uint flags=0x0010u | (visible?0u:0x0040u) | (zOrderChanged?0u:0x0004u);
            SetWindowPos(Handle, owner.Topmost ? new IntPtr(-1) : new IntPtr(-2),
                (int)Math.Round(p.X), (int)Math.Round(p.Y), (int)Math.Round(next.Width), (int)Math.Round(next.Height), flags);
            bounds = next; pinned = owner.Topmost; visible = true;
            if(zOrderChanged) owner.RaiseCompactTools();
        }
    }

    private IntPtr FindRenderer()
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(new WindowInteropHelper(owner).Handle, (h, _) => {
            var name = new StringBuilder(256); GetClassName(h, name, name.Capacity);
            if (name.ToString() == "Chrome_RenderWidgetHostHWND" && Native.IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
        if (msg == 0x84)
        {
            Native.GetWindowRect(new WindowInteropHelper(owner).Handle, out var r);
            int x=(short)(lp.ToInt64()&0xffff), y=(short)((lp.ToInt64()>>16)&0xffff);
            bool left=x<r.Left+8, right=x>=r.Right-8, top=y<r.Top+8, bottom=y>=r.Bottom-8;
            int hit=top ? (left?13:right?14:12) : bottom ? (left?16:right?17:15) : left?10:right?11:1;
            handled = true; return new IntPtr(hit);
        }
        if (msg == 0xA1) { handled=true; owner.Activate(); Native.SendMessage(new WindowInteropHelper(owner).Handle, (uint)msg, wp, lp); return IntPtr.Zero; }
        if (msg == 0x20)
        {
            var renderer = FindRenderer();
            if (renderer != IntPtr.Zero) { PostMessage(renderer, (uint)msg, renderer, lp); handled = true; return new IntPtr(1); }
        }
        if (msg >= 0x200 && msg <= 0x20E)
        {
            var renderer = target != IntPtr.Zero && IsWindow(target) ? target : FindRenderer();
            if (renderer == IntPtr.Zero) return IntPtr.Zero;
            bool down = msg is 0x201 or 0x204 or 0x207 or 0x20B or 0x203 or 0x206 or 0x209 or 0x20D;
            bool up = msg is 0x202 or 0x205 or 0x208 or 0x20C;
            if (down) { owner.Activate(); owner.Web.Focus(); SetFocus(renderer); target = renderer; SetCapture(hwnd); }
            var point = new Native.POINT { X = (short)(lp.ToInt64() & 0xffff), Y = (short)((lp.ToInt64() >> 16) & 0xffff) };
            bool isWheel=msg is 0x20A or 0x20E;
            if (!isWheel) Native.ClientToScreen(hwnd, ref point);
            var local=owner.Web.PointFromScreen(new Point(point.X,point.Y));
            int keys=(int)(wp.ToInt64()&0xffff);
            int modifiers=((keys&4)!=0?8:0)|((keys&8)!=0?2:0)|((GetKeyState(0x12)&0x8000)!=0?1:0);
            int buttons=((keys&1)!=0?1:0)|((keys&2)!=0?2:0)|((keys&16)!=0?4:0)|((keys&32)!=0?8:0)|((keys&64)!=0?16:0);
            string button=msg is 0x201 or 0x202 or 0x203 ? "left" : msg is 0x204 or 0x205 or 0x206 ? "right" : msg is 0x207 or 0x208 or 0x209 ? "middle" : msg is 0x20B or 0x20C or 0x20D ? ((wp.ToInt64()>>16)&0xffff)==1?"back":"forward" : "none";
            if(msg==0x200)button=(buttons&1)!=0?"left":(buttons&2)!=0?"right":(buttons&4)!=0?"middle":(buttons&8)!=0?"back":(buttons&16)!=0?"forward":"none";
            if(down) clickCount=msg is 0x203 or 0x206 or 0x209 or 0x20D ? 2:1;
            string type=isWheel?"mouseWheel":down?"mousePressed":up?"mouseReleased":"mouseMoved";
            var data=new Dictionary<string,object> { ["type"]=type,["x"]=local.X/owner.Web.ZoomFactor,["y"]=local.Y/owner.Web.ZoomFactor,["modifiers"]=modifiers,["buttons"]=buttons,["button"]=button };
            if(down || up)data["clickCount"]=clickCount;
            if(isWheel) { int delta=(short)((wp.ToInt64()>>16)&0xffff);data["deltaX"]=msg==0x20E?delta:0;data["deltaY"]=msg==0x20A?-delta:0; }
            Enqueue(type,JsonSerializer.Serialize(data));
            if (up && (wp.ToInt64() & 0x73) == 0) { target = IntPtr.Zero; ReleaseCapture(); }
            if (msg == 0x200) { var track = new TRACKMOUSEEVENT { Size = Marshal.SizeOf<TRACKMOUSEEVENT>(), Flags = 2, Hwnd = hwnd }; TrackMouseEvent(ref track); }
            handled = true;
        }
        if (msg == 0x2A3 && GetCapture()!=hwnd) Enqueue("mouseMoved","{\"type\":\"mouseMoved\",\"x\":-1,\"y\":-1,\"buttons\":0}");
        // Chromium may legitimately take native capture while processing a
        // dispatched press. Do not cancel that press during the hand-off.
        if (msg == 0x215) target=IntPtr.Zero;
        return IntPtr.Zero;
    }
    private void Enqueue(string type,string data)
    {
        if(disposed || !visible)return;
        // Coalesce only adjacent motion packets; press/release/wheel ordering is
        // preserved so fast dragging cannot leave a stuck button or stale moves.
        if(type=="mouseMoved" && pending.Last?.Value.Type=="mouseMoved")pending.RemoveLast();
        pending.AddLast((type,data,generation,false));
        if(!dispatching)_=Dispatch();
    }
    private async Task Dispatch()
    {
        dispatching=true;
        try
        {
            while(pending.First is {} first)
            {
                var packet=first.Value;pending.RemoveFirst();
                if(disposed || (!visible && !packet.Cleanup) || packet.Generation!=generation)continue;
                try
                {
                    if(packet.Type=="mousePressed") { using var data=JsonDocument.Parse(packet.Json);heldButton=data.RootElement.GetProperty("button").GetString(); }
                    if(owner.Web.CoreWebView2 is {} core)await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",packet.Json);
                    if(packet.Type=="mouseReleased" && packet.Generation==generation)heldButton=null;
                }
                catch(Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            }
        }
        finally { dispatching=false; }
    }
    internal void ResetPointer()
    {
        generation++;pending.Clear();target=IntPtr.Zero;
        // A hide/resize/navigation can interrupt a drag after the press reached
        // Chromium. Release outside the page after any in-flight packet, without
        // generating a click on an old or newly navigated control.
        if(heldButton is {} button && !disposed)
        {
            string data=JsonSerializer.Serialize(new { type="mouseReleased",x=-1,y=-1,button,buttons=0,clickCount=1 });
            pending.AddLast(("mouseReleased",data,generation,true));
            if(!dispatching)_=Dispatch();
        }
        if(Handle!=IntPtr.Zero && GetCapture()==Handle)ReleaseCapture();
    }
    internal static IntPtr Pack(int x, int y) => new(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
    public void Dispose() { ResetPointer();disposed=true;source?.Dispose(); source = null; visible = false; }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public int Size, Flags; public IntPtr Hwnd; public int HoverTime; }
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumProc callback, IntPtr param);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref Native.POINT p);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr GetCapture();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
}

