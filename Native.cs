using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FloatBrowser;
internal static class Native
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    internal static void RaisePopup(IntPtr hwnd) => SetWindowPos(hwnd,new IntPtr(-1),0,0,0,0,0x213); // no move/size/activate/owner reorder
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    internal static uint ScreenPixel(POINT point)
    {
        var dc=GetDC(IntPtr.Zero); try { return GetPixel(dc,point.X,point.Y); } finally { ReleaseDC(IntPtr.Zero,dc); }
    }
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr region, uint flags);
    internal static void RedrawSurface(IntPtr hwnd) => RedrawWindow(hwnd,IntPtr.Zero,IntPtr.Zero,0x481); // invalidate frame and child surfaces, asynchronously
    internal static IntPtr LayeredStyleHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Preserve uniform native window alpha instead of WPF per-pixel transparency.
        if (wParam.ToInt64() == -20 && msg == 0x007C)
        {
            // HwndTarget otherwise strips WS_EX_LAYERED when resizing a non-per-pixel window.
            Marshal.WriteInt32(lParam, 4, Marshal.ReadInt32(lParam, 4) | 0x80000);
            handled = true;
        }
        if (wParam.ToInt64() == -20 && msg == 0x007D) handled = true;
        return IntPtr.Zero;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    internal const uint BackdropKey = 0x00FEFDFE;
    internal static void SetOpacity(IntPtr hwnd, double opacity, bool clearWhite = false)
        => SetLayeredOpacity(hwnd, clearWhite ? BackdropKey : 0, (byte)Math.Round(Math.Clamp(opacity, .1, 1) * 255), clearWhite ? 3u : 2u);
    internal static void SetInputOpacity(IntPtr hwnd) => SetLayeredOpacity(hwnd, 0, 1, 2);
    private static void SetLayeredOpacity(IntPtr hwnd, uint key, byte alpha, uint flags)
    {
        if (hwnd == IntPtr.Zero) return;
        const int exStyle = -20, layered = 0x80000;
        int style = GetWindowLong(hwnd, exStyle);
        if ((style & layered) == 0) SetWindowLong(hwnd, exStyle, style | layered);
        if (!SetLayeredWindowAttributes(hwnd, key, alpha, flags))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; public bool Contains(POINT p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom; }
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
internal sealed class HotkeyService : IDisposable
{
    private readonly HwndSource source;
    private readonly Action<int> callback;
    private List<(int Id, Hotkey Key)> active = new();
    private int generation;
    private bool suspended;
    public void Suspend()
    {
        if (suspended) return;
        foreach (var item in active) Native.UnregisterHotKey(source.Handle, item.Id);
        suspended = true;
    }
    public bool Resume(out string error)
    {
        error = "";
        if (!suspended) return true;
        if (active.Count == 0) { suspended = false; return true; }
        return Configure(active.ConvertAll(item => item.Key).ToArray(), out error);
    }
    public HotkeyService(Action<int> callback)
    {
        this.callback = callback;
        source = new HwndSource(new HwndSourceParameters("FloatBrowser.Hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        source.AddHook(Hook);
    }
    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312)
        {
            int i = active.FindIndex(x => x.Id == wParam.ToInt32());
            if (i >= 0 && !suspended) { callback(i); handled = true; }
        }
        return IntPtr.Zero;
    }
    public bool Configure(Hotkey[] keys, out string error)
    {
        error = "";
        if (keys.Length != 3 || new HashSet<Hotkey>(keys).Count != 3) { error = "三个快捷键不能相同。"; return false; }
        var next = new List<(int Id, Hotkey Key)>();
        foreach (var key in keys)
        {
            var existing = suspended ? default : active.Find(x => x.Key == key);
            if (existing != default) { next.Add(existing); continue; }
            int id = ++generation;
            if (!Native.RegisterHotKey(source.Handle, id, (uint)key.Modifiers | 0x4000, (uint)key.KeyCode))
            {
                foreach (var item in next) if (suspended || !active.Contains(item)) Native.UnregisterHotKey(source.Handle, item.Id);
                error = $"{key} 已被占用或无法注册，原快捷键保持不变。"; return false;
            }
            next.Add((id, key));
        }
        if (!suspended) foreach (var item in active) if (!next.Contains(item)) Native.UnregisterHotKey(source.Handle, item.Id);
        active = next; suspended = false; return true;
    }
    public void Dispose() { foreach (var item in active) Native.UnregisterHotKey(source.Handle, item.Id); source.Dispose(); }
}
