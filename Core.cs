using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;

namespace FloatBrowser;

public enum HiddenReason { None, MouseLeave, BossKey, Switched }
public sealed class VisibilityPolicy
{
    public HiddenReason Reason { get; private set; }
    public bool Visible => Reason == HiddenReason.None;
    public void Hide(HiddenReason reason) => Reason = reason;
    public void Show() => Reason = HiddenReason.None;
    public bool CanMouseRestore(bool enabled, bool entered) => enabled && entered && Reason == HiddenReason.MouseLeave;
}
public sealed record Hotkey(int Modifiers, int KeyCode)
{
    public static Hotkey FromKey(Key key, ModifierKeys modifiers) => new((int)modifiers & 15, KeyInterop.VirtualKeyFromKey(key));
    public override string ToString() =>
        ((Modifiers & 2) != 0 ? "Ctrl + " : "") + ((Modifiers & 1) != 0 ? "Alt + " : "") +
        ((Modifiers & 4) != 0 ? "Shift + " : "") + ((Modifiers & 8) != 0 ? "Win + " : "") +
        (KeyCode is >= 0x30 and <= 0x39 ? ((char)KeyCode).ToString() : KeyInterop.KeyFromVirtualKey(KeyCode).ToString());
}
public sealed class Preferences
{
    public WindowPlacement? WindowPlacement { get; set; }
    public bool ClearWhiteBackground { get; set; }
    public bool HideOnLeave { get; set; }
    public bool RestoreOnHover { get; set; }
    public bool ResumeMediaOnShow { get; set; } = true;
    public bool BackgroundPlayback { get; set; }
    public bool Topmost { get; set; } = true;
    public double Opacity { get; set; } = 1;
    public double Zoom { get; set; } = 1;
    public Hotkey Boss { get; set; } = new(3, 0x51);
    public Hotkey Previous { get; set; } = new(3, 0x25);
    public Hotkey Next { get; set; } = new(3, 0x27);
}
public sealed record WindowPlacement(double Left, double Top, double Width, double Height)
{
    public bool IsValid => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height) && Width >= 160 && Height >= 90 && Width <= 10000 && Height <= 10000;
}
public sealed record PageEntry(string Title, string Url, DateTimeOffset Visited);
public sealed class LocalData
{
    public Preferences Settings { get; set; } = new();
    public List<PageEntry> Favorites { get; set; } = new();
    public List<PageEntry> History { get; set; } = new();
}
public sealed class DataStore
{
    public string Root { get; }
    public string Profile => Path.Combine(Root, "WebView2");
    public LocalData Data { get; private set; } = new();
    public string? LoadWarning { get; private set; }
    public DataStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        var path = Path.Combine(root, "data.json");
        if (File.Exists(path))
        {
            try { Data = JsonSerializer.Deserialize<LocalData>(File.ReadAllText(path)) ?? new(); }
            catch (Exception ex) when (ex is JsonException || ex is IOException)
            {
                File.Copy(path, path + ".damaged-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true);
                LoadWarning = "本地设置文件无法读取，已保留备份。网站登录数据未清除。";
            }
        }
        Data.Settings ??= new(); Data.Favorites ??= new(); Data.History ??= new();
        Data.Settings.Boss ??= new(3, 0x51); Data.Settings.Previous ??= new(3, 0x25); Data.Settings.Next ??= new(3, 0x27);
        Data.Settings.Opacity = double.IsFinite(Data.Settings.Opacity) ? Math.Clamp(Data.Settings.Opacity, .1, 1) : 1;
        Data.Settings.Zoom = double.IsFinite(Data.Settings.Zoom) ? Math.Clamp(Data.Settings.Zoom, .3, 3) : 1;
        if (Data.Settings.WindowPlacement?.IsValid != true) Data.Settings.WindowPlacement = null;
        Data.History = Data.History.Take(50).ToList();
    }
    public void Save()
    {
        var path = Path.Combine(Root, "data.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    public void Visit(string title, string url)
    {
        if (!Navigation.IsWebUrl(url) || url.StartsWith("https://float.internal/", StringComparison.OrdinalIgnoreCase)) return;
        Data.History.RemoveAll(x => x.Url == url);
        Data.History.Insert(0, new(title, url, DateTimeOffset.Now));
        Data.History = Data.History.Take(50).ToList(); Save();
    }
}
public static class Navigation
{
    public static bool IsWebUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
    public static string Resolve(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return "https://float.internal/home.html";
        if (IsWebUrl(input)) return new Uri(input).AbsoluteUri;
        if (!input.Contains(' ') && (input.Contains('.') || input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)) && IsWebUrl("https://" + input)) return "https://" + input;
        return "https://www.bing.com/search?q=" + Uri.EscapeDataString(input);
    }
}


