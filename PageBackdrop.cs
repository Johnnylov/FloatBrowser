namespace FloatBrowser;
internal static class PageBackdrop
{
    public static string Script(bool enabled) => $$"""
        (() => {
          const key='__float_white_backdrop_v1', color='rgba(0, 0, 0, 0)';
          const install=w=>{
            try {
              const d=w.document;
              let s=w[key];
              if(!s) {
                s=w[key]={enabled:false, changed:new Map(), timer:0, roots:new Set()};
                const options={subtree:true,childList:true,attributes:true,attributeFilter:['class','style']};
                // Keep the entire player UI intact, not just the <video>. Bilibili
                // animates control/danmaku ancestors continuously during playback.
                const protectedArea='video,audio,img,canvas,svg,input,textarea,select,button,a,label,[contenteditable],[role="button"],[role="link"],[role="slider"],[onclick],.bpx-player-container,.bilibili-player';
                const eligible=el=>el instanceof w.HTMLElement && !el.closest(protectedArea);
                s.scan=(root,seen)=>{
                  if(!root || !root.isConnected) return;
                  const elements=[root,...root.querySelectorAll('*')];
                  for(const el of elements) {
                    if(seen.has(el))continue;seen.add(el);
                    if(!eligible(el)) continue;
                    const css=w.getComputedStyle(el);
                    if(css.backgroundImage!=='none' || css.cursor==='pointer') continue;
                    const rgba=css.backgroundColor.match(/[\d.]+/g)?.map(Number);
                    if(!rgba || rgba.length<3 || rgba.slice(0,3).some(n=>n<245) || (rgba[3]??1)<.98 || css.backgroundColor===color) continue;
                    if(!s.changed.has(el) || el.style.getPropertyValue('background-color')!==color)
                      s.changed.set(el,{value:el.style.getPropertyValue('background-color'),priority:el.style.getPropertyPriority('background-color')});
                    el.style.setProperty('background-color',color,'important');
                  }
                };
                s.flush=()=>{
                  s.timer=0;if(!s.enabled)return;
                  s.observer.disconnect();
                  const seen=new Set();
                  for(const root of s.roots)if(!seen.has(root))s.scan(root,seen);s.roots.clear();
                  for(const el of s.changed.keys())if(!el.isConnected)s.changed.delete(el);
                  if(d.documentElement)s.observer.observe(d.documentElement,options);
                };
                s.observer=new w.MutationObserver(records=>{
                  for(const r of records) {
                    if(r.target.nodeType===1 && r.target.closest(protectedArea))continue;
                    if(r.type==='attributes')s.roots.add(r.target);
                    for(const node of r.addedNodes)if(node.nodeType===1)s.roots.add(node);
                  }
                  if(!s.timer)s.timer=w.setTimeout(s.flush,100);
                });
                s.set=on=>{
                  s.enabled=on;s.observer.disconnect();w.clearTimeout(s.timer);s.timer=0;s.roots.clear();
                  if(on){s.roots.add(d.documentElement);s.flush();}
                  else{
                    for(const [el,before] of s.changed)if(el.style.getPropertyValue('background-color')===color){
                      if(before.value)el.style.setProperty('background-color',before.value,before.priority);
                      else el.style.removeProperty('background-color');
                    }
                    s.changed.clear();
                  }
                };
              }
              s.set({{(enabled ? "true" : "false")}});
              for(const frame of w.frames)install(frame);
            } catch {}
          };
          install(window);return true;
        })()
        """;
}
