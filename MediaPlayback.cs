using System.Text.Json;

namespace FloatBrowser;
internal static class MediaPlayback
{
    public static string Install(string key) => $$"""
        (() => {
          const key={{JsonSerializer.Serialize(key)}};
          if(window[key])return;
          const s=window[key]={allow:true,active:new Set(),marked:new Map(),blocked:new Map(),pending:new Set(),epoch:0};
          const media=()=>[...new Set([...document.querySelectorAll('video,audio'),...s.pending])];
          const source=m=>({src:m.currentSrc,object:m.srcObject});
          const same=(m,v)=>m.isConnected&&!m.ended&&m.currentSrc===v.src&&m.srcObject===v.object;
          s.pause=()=>{
            s.allow=false;s.epoch++;let n=0;
            for(const m of media())if((!m.paused&&!m.ended)||s.pending.has(m)){s.marked.set(m,source(m));m.pause();n++;}
            return n;
          };
          s.resume=enabled=>{
            s.allow=true;let n=0;const epoch=++s.epoch;
            for(const [m,v] of s.marked){
              if(!same(m,v))continue;
              if(!enabled){s.blocked.set(m,v);m.pause();continue;}
              s.blocked.delete(m);
              if(!m.paused)continue;
              s.pending.add(m);n++;
              try{Promise.resolve(m.play()).then(()=>{if(epoch!==s.epoch&&!s.allow)m.pause();}).catch(()=>{}).finally(()=>s.pending.delete(m));}catch{s.pending.delete(m);}
            }
            s.marked.clear();return n;
          };
          s.configure=(allow,resume)=>{
            if(!allow)return s.pause();
            if(!s.allow||s.marked.size)return s.resume(resume);
            return 0;
          };
          s.pauseAll=()=>{
            s.epoch++;s.marked.clear();let n=0;
            for(const m of media()){if(!m.paused||s.pending.has(m)){s.blocked.set(m,source(m));m.pause();n++;} }
            return n;
          };
          document.addEventListener('play',e=>{
            const m=e.target;if(!(m instanceof HTMLMediaElement))return;
            const blocked=s.blocked.get(m);
            if(blocked&&same(m,blocked)){m.pause();return;}
            s.blocked.delete(m);
            s.active.add(m);
            if(!s.allow){s.marked.set(m,source(m));m.pause();}
          },true);
          document.addEventListener('pause',e=>{
            const m=e.target;if(!(m instanceof HTMLMediaElement))return;
            // Chromium can pause muted media before the asynchronous host policy
            // arrives. Remember that visibility-induced pause, never a visible
            // user pause or an explicit pause-all.
            if(document.hidden&&s.allow&&s.active.has(m)&&!s.blocked.has(m))s.marked.set(m,source(m));
            s.active.delete(m);
          },true);
          // The host owns visibility policy. A tool panel or focus change must
          // never introduce a second, conflicting document.hidden pause policy.
          document.addEventListener('pointerdown',()=>s.blocked.clear(),true);
          document.addEventListener('keydown',()=>s.blocked.clear(),true);
        })()
        """;
    public static string Configure(string key,bool allow,bool resume) => $"window[{JsonSerializer.Serialize(key)}]?.configure({(allow?"true":"false")},{(resume?"true":"false")})";
    public static string Pause(string key) => $"window[{JsonSerializer.Serialize(key)}]?.pause()";
    public static string Resume(string key,bool enabled) => $"window[{JsonSerializer.Serialize(key)}]?.resume({(enabled?"true":"false")})";
    public static string PauseAll(string key) => $"window[{JsonSerializer.Serialize(key)}]?.pauseAll()";
    public const string Playing = "[...document.querySelectorAll('video,audio')].some(m=>!m.paused&&!m.ended)";
}
