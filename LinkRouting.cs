using System;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace FloatBrowser;
internal static class LinkRouting
{
    public static bool IsAuthorization(string url) => Uri.TryCreate(url,UriKind.Absolute,out var uri) &&
        System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath+uri.Query,@"(?i)(oauth|authorize|authorization|passport|login|signin|sso|redirect_uri=|response_type=)");
    public static string Install(string key)=>$$"""
        (()=>{
          const key={{JsonSerializer.Serialize(key+"_links")}};if(window[key])return;window[key]=true;
          const record=e=>{
            if(!e.isTrusted)return;
            const a=e.composedPath().find(n=>n instanceof HTMLAnchorElement);
            chrome.webview.postMessage({key,href:a?.href??'',plain:e.type==='click'&&e.button===0&&!e.ctrlKey&&!e.shiftKey&&!e.altKey&&!e.metaKey&&!a?.hasAttribute('download')});
          };
          document.addEventListener('click',record,true);
          document.addEventListener('auxclick',record,true);
          document.addEventListener('contextmenu',record,true);
        })()
        """;
}
public partial class BrowserWindow
{
    private string? plainLink;
    private long linkIntentAt;
    private void ReceiveLinkIntent(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc=JsonDocument.Parse(e.WebMessageAsJson);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty("key",out var key) || key.GetString()!=mediaKey+"_links")return;
            plainLink=root.GetProperty("plain").GetBoolean()?root.GetProperty("href").GetString():null;
            linkIntentAt=Environment.TickCount64;
        }
        catch(JsonException){}catch(InvalidOperationException){}catch(System.Collections.Generic.KeyNotFoundException){}
    }
    private bool OpenLinkHere(CoreWebView2NewWindowRequestedEventArgs args)
    {
        bool here=args.IsUserInitiated && Environment.TickCount64-linkIntentAt<1500 &&
            String.Equals(plainLink,args.Uri,StringComparison.Ordinal) && !args.WindowFeatures.HasPosition && !args.WindowFeatures.HasSize &&
            (String.IsNullOrEmpty(args.Name) || args.Name=="_blank") && Navigation.IsWebUrl(args.Uri) && !LinkRouting.IsAuthorization(args.Uri);
        plainLink=null;return here;
    }
}
