using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace FloatBrowser;
public partial class BrowserWindow
{
    private readonly HashSet<CoreWebView2Frame> mediaFrames=new();
    private bool pollingMedia;
    private bool? focusEmulated;
    private (bool Allow,bool Resume,bool Emulate,int Document)? mediaPolicy;
    internal bool PlayingMedia { get; private set; }
    private bool MediaForeground => IsVisible && WindowState!=WindowState.Minimized;

    private void RegisterMediaFrame(CoreWebView2Frame frame)
    {
        if(!mediaFrames.Add(frame))return;
        frame.FrameCreated+=(_,e)=>RegisterMediaFrame(e.Frame);
        frame.Destroyed+=(_,_)=>mediaFrames.Remove(frame);
        frame.DOMContentLoaded+=async (_,_)=>
        {
            try
            {
                if(closed)return;
                await frame.ExecuteScriptAsync(MediaPlayback.Install(mediaKey));
                await frame.ExecuteScriptAsync(LinkRouting.Install(mediaKey));
                await frame.ExecuteScriptAsync(CurrentMediaScript());
            }
            catch(Exception ex){System.Diagnostics.Debug.WriteLine(ex);}
        };
        frame.WebMessageReceived+=ReceiveLinkIntent;
    }
    private string CurrentMediaScript()=>MediaPlayback.Configure(mediaKey,
        manager.Settings.BackgroundPlayback||MediaForeground,
        manager.Settings.BackgroundPlayback||manager.Settings.ResumeMediaOnShow);
    private async Task ForEachDocument(string script)
    {
        if(closed || Web.CoreWebView2 is null)return;
        await Web.CoreWebView2.ExecuteScriptAsync(script);
        foreach(var frame in mediaFrames.ToArray())
            try { if(!closed)await frame.ExecuteScriptAsync(script); } catch(Exception ex){System.Diagnostics.Debug.WriteLine(ex);}
    }
    private void UpdateMediaPolicy(bool force=false)
    {
        if(closed || Web.CoreWebView2 is null)return;
        bool allow=manager.Settings.BackgroundPlayback||MediaForeground;
        var policy=(allow,manager.Settings.BackgroundPlayback||manager.Settings.ResumeMediaOnShow,allow&&(!IsActive||!MediaForeground),documentVersion);
        if(!force && mediaPolicy==policy)return;
        mediaPolicy=policy;
        mediaTransition=ApplyMediaPolicy(mediaTransition);
    }
    private async Task ApplyMediaPolicy(Task previous)
    {
        try
        {
            await previous;
            if(closed || Web.CoreWebView2 is null)return;
            bool allow=manager.Settings.BackgroundPlayback||MediaForeground;
            bool emulate=allow&&(!IsActive||!MediaForeground);
            // Keep Chromium's native page visibility stable while playback is
            // allowed, including boss-key hiding and native occlusion.
            if(emulate && focusEmulated!=true)
            {
                await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled","{\"enabled\":true}");
                focusEmulated=true;
            }
            await ForEachDocument(CurrentMediaScript());
            if(!emulate && focusEmulated!=false)
            {
                await Web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled","{\"enabled\":false}");
                focusEmulated=false;
            }
        }
        catch(Exception ex){mediaPolicy=null;SetStatus("播放策略未能完全应用："+ex.Message);}
    }
    internal Task PauseAllMedia()
    {
        async Task PauseAfter(Task previous)
        {
            try { await previous;if(!closed)await ForEachDocument(MediaPlayback.PauseAll(mediaKey)); }
            catch(Exception ex){SetStatus("暂停媒体失败："+ex.Message);}
        }
        return mediaTransition=PauseAfter(mediaTransition);
    }
    internal async Task PollMediaState()
    {
        if(closed || pollingMedia || Web.CoreWebView2 is null)return;
        pollingMedia=true;
        int version=documentVersion;
        try
        {
            bool playing=Web.CoreWebView2.IsDocumentPlayingAudio || await Web.CoreWebView2.ExecuteScriptAsync(MediaPlayback.Playing)=="true";
            foreach(var frame in mediaFrames.ToArray())
                try{if(!playing)playing=await frame.ExecuteScriptAsync(MediaPlayback.Playing)=="true";}catch{ }
            if(!closed && version==documentVersion && playing!=PlayingMedia){PlayingMedia=playing;manager.RefreshWindows();}
        }
        catch(Exception ex){System.Diagnostics.Debug.WriteLine(ex);}
        finally{pollingMedia=false;}
    }
}
