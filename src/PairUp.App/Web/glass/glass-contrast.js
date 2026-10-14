(function (root, factory) {
  'use strict';
  const api=factory();
  if(typeof module==='object'&&module.exports){module.exports=api;return;}
  root.ShengyuGlassContrast=api;
  if(typeof document==='undefined')return;
  const doc=document.documentElement,images=new Map(),surfaces=new Set();
  const plane=document.createElement('canvas'),ctx=plane.getContext('2d',{willReadFrequently:true});
  const colourCanvas=document.createElement('canvas');colourCanvas.width=colourCanvas.height=1;
  const colourCtx=colourCanvas.getContext('2d',{willReadFrequently:true});
  let pending=0,revision=0,waiting=Promise.resolve(),disposed=false,lastReport=null;
  const lifetime=new AbortController(),backgroundObservers=[];
  Object.defineProperty(root,'ShengyuGlassContrastReport',{configurable:true,get:()=>lastReport});
  function colour(value) {
    colourCtx.clearRect(0,0,1,1);colourCtx.fillStyle='transparent';
    try{colourCtx.fillStyle=value;colourCtx.fillRect(0,0,1,1);}catch(_error){}
    const p=colourCtx.getImageData(0,0,1,1).data;return [p[0],p[1],p[2],p[3]/255];
  }
  function split(value) {
    const parts=[];let depth=0,quote='',start=0;
    for(let n=0;n<value.length;n++){
      const c=value[n];if(quote){if(c===quote&&value[n-1]!=='\\')quote='';continue;}
      if(c==='"'||c==="'"){quote=c;continue;}if(c==='(')depth++;if(c===')')depth--;
      if(c===','&&!depth){parts.push(value.slice(start,n).trim());start=n+1;}
    }
    parts.push(value.slice(start).trim());return parts;
  }
  function stops(parts,length) {
    const list=[];
    for(const part of parts){
      const match=part.match(/^(.*?)(?:\s+(-?[\d.]+)(%|px))?(?:\s+(-?[\d.]+)(%|px))?$/);
      if(!match)continue;
      const rgb=colour(match[1]);
      if(!rgb[3]&&!/^(?:transparent$|rgba?\(|hsla?\(|color\(|#)/.test(match[1]))continue;
      const position=match[3]==='%'?Number(match[2])/100:match[2]?Number(match[2])/length:null;
      list.push({colour:match[1],position});
      if(match[4])list.push({colour:match[1],position:match[5]==='%'?Number(match[4])/100:Number(match[4])/length});
    }
    if(list.length<2)return [];
    if(list[0].position===null)list[0].position=0;if(list.at(-1).position===null)list.at(-1).position=1;
    let last=0;
    for(let n=1;n<list.length;n++)if(list[n].position!==null){
      list[n].position=Math.max(list[last].position,list[n].position);
      for(let k=last+1;k<n;k++)list[k].position=list[last].position+(list[n].position-list[last].position)*(k-last)/(n-last);last=n;
    }
    return list;
  }
  function gradient(layer,w,h) {
    const radial=layer.includes('radial-gradient('),start=layer.indexOf('(');
    if(start<0)return;
    const parts=split(layer.slice(start+1,-1));let geometry='';
    if(/(?:^-?[\d.]+deg$|\bat\b|^to\b|^circle\b|^ellipse\b|side|corner)/.test(parts[0]))geometry=parts.shift();
    ctx.save();let g;
    if(radial){
      const at=geometry.match(/\bat\s+([\d.]+)%\s+([\d.]+)%/),x=at?Number(at[1])/100*w:w/2,y=at?Number(at[2])/100*h:h/2;
      const rx=Math.max(x,w-x),ry=Math.max(y,h-y),circle=geometry.includes('circle');
      const radius=circle?Math.hypot(rx,ry):Math.SQRT2*rx,ratio=circle?1:ry/rx;
      const list=stops(parts,radius*innerWidth/192);if(!list.length){ctx.restore();return;}
      ctx.translate(x,y);ctx.scale(1,ratio);g=ctx.createRadialGradient(0,0,0,0,0,radius);
      list.forEach(s=>g.addColorStop(Math.max(0,Math.min(1,s.position)),s.colour));ctx.fillStyle=g;ctx.fillRect(-x,-y/ratio,w,h/ratio);
    }else{
      const angle=geometry.includes('deg')?parseFloat(geometry):geometry.includes('right')?90:180;
      const radians=angle*Math.PI/180,dx=Math.sin(radians),dy=-Math.cos(radians),length=Math.abs(w*dx)+Math.abs(h*dy);
      const list=stops(parts,length*innerWidth/192);if(!list.length){ctx.restore();return;}
      g=ctx.createLinearGradient(w/2-dx*length/2,h/2-dy*length/2,w/2+dx*length/2,h/2+dy*length/2);
      list.forEach(s=>g.addColorStop(Math.max(0,Math.min(1,s.position)),s.colour));ctx.fillStyle=g;ctx.fillRect(0,0,w,h);
    }
    ctx.restore();
  }
  async function imageFor(layer) {
    const match=layer.match(/^url\(["']?(.*?)["']?\)$/),url=match?.[1];
    // Only the locally chosen wallpaper or bundled image. Never fetch external URLs.
    if(!api.allowsImage(url,document.baseURI,null))return null;
    if(!images.has(url)){
      const promise=new Promise(resolve=>{const img=new Image();img.onload=()=>resolve(img);img.onerror=()=>resolve(null);img.src=url;});
      images.set(url,promise);if(images.size>4)images.delete(images.keys().next().value);
    }
    return images.get(url);
  }
  async function background(ticket) {
    const layers=[];
    for(const element of document.querySelectorAll('.backdrop-layer,.ambient-light')){
      const style=getComputedStyle(element);if(style.display==='none'||style.visibility==='hidden'||Number(style.opacity)===0)continue;
      const rect=element.getBoundingClientRect();
      for(const pseudo of ['', '::before','::after']) {
        const paint=pseudo?getComputedStyle(element,pseudo):style;
        if(pseudo&&(paint.content==='none'||paint.content==='normal'||paint.display==='none'))continue;
        const width=pseudo?parseFloat(paint.width)||rect.width:rect.width,height=pseudo?parseFloat(paint.height)||rect.height:rect.height;
        layers.push({opacity:(Number(style.opacity)||1)*(pseudo?(Number(paint.opacity)||1):1),filter:style.filter,colour:paint.backgroundColor,
          rect:{left:rect.left+(pseudo?parseFloat(paint.left)||0:0),top:rect.top+(pseudo?parseFloat(paint.top)||0:0),width,height},
          images:await Promise.all(split(paint.backgroundImage).reverse().map(async value=>({value,image:await imageFor(value)})))});
        if(ticket!==revision)return null;
      }
    }
    plane.width=192;plane.height=Math.max(64,Math.round(192*innerHeight/innerWidth));
    ctx.clearRect(0,0,plane.width,plane.height);ctx.fillStyle=getComputedStyle(document.body).backgroundColor;ctx.fillRect(0,0,plane.width,plane.height);
    for(const layerSet of layers){
      ctx.save();ctx.globalAlpha=layerSet.opacity;
      const scale=192/innerWidth,w=layerSet.rect.width*scale,h=layerSet.rect.height*scale;
      ctx.translate(layerSet.rect.left*scale,layerSet.rect.top*scale);
      const brightness=layerSet.filter.match(/brightness\(([\d.]+)(%)?\)/);
      const blur=layerSet.filter.match(/blur\(([\d.]+)px\)/);
      ctx.filter=(brightness?'brightness('+(Number(brightness[1])/(brightness[2]?100:1))+') ':'')+(blur?'blur('+Number(blur[1])*192/innerWidth+'px)':'');
      if(!ctx.filter)ctx.filter='none';
      ctx.fillStyle=layerSet.colour;ctx.fillRect(0,0,w,h);
      for(const {value:layer,image:img} of layerSet.images){
        if(layer.includes('gradient('))gradient(layer,w,h);
        else {
          if(!img)continue;
          const cover=Math.max(w/img.width,h/img.height),imageWidth=img.width*cover,imageHeight=img.height*cover;
          ctx.drawImage(img,(w-imageWidth)/2,(h-imageHeight)/2,imageWidth,imageHeight);
        }
      }
      ctx.restore();
    }
    return ctx.getImageData(0,0,plane.width,plane.height);
  }
  function discover(){
    let removed=false;
    for(const element of surfaces)if(!element.isConnected){surfaces.delete(element);resize.unobserve(element);removed=true;}
    if(removed){
      contentChanges.disconnect();
      for(const element of surfaces)contentChanges.observe(element,{childList:true,subtree:true,characterData:true,attributes:true,attributeFilter:['hidden','aria-hidden']});
    }
    for(const element of document.querySelectorAll('[data-glass-surface]')){
      if(!surfaces.has(element)){
        surfaces.add(element);resize.observe(element);
        contentChanges.observe(element,{childList:true,subtree:true,characterData:true,attributes:true,attributeFilter:['hidden','aria-hidden','data-order-stats-page']});
      }
    }
  }
  const floating='.appear,.appearance-popover,.act-card,.theme-chooser,.history-dialog,.login,.login-sheet,.lv-card,.inspector';
  function tint(element) {
    const style=getComputedStyle(element),own=colour(style.backgroundColor);
    if(own[3]>.001)return own;
    if(element.hasAttribute('data-liquid-glass')){
      const base=colour('rgb('+getComputedStyle(doc).getPropertyValue('--glass-base').trim()+')');
      const kind=element.matches(floating)?'popover':'chrome';
      base[3]=Math.max(0,Math.min(1,parseFloat(getComputedStyle(doc).getPropertyValue('--diy-'+kind+'-alpha'))||0));return base;
    }
    return [0,0,0,0];
  }
  function textPoints(element, bounds, clips) {
    // Test the glass behind actual lettering, not unused corners of a large panel.
    // Clear deliberately retains the original whole-surface sampling below.
    function clipFor(node) {
      if(clips.has(node))return clips.get(node);
      const clip=node.parentElement?{...clipFor(node.parentElement)}:{left:0,top:0,right:innerWidth,bottom:innerHeight};
      const style=getComputedStyle(node);
      if(/hidden|clip|auto|scroll/.test(style.overflowX+style.overflowY)){
        const rect=node.getBoundingClientRect();
        if(/hidden|clip|auto|scroll/.test(style.overflowX)){clip.left=Math.max(clip.left,rect.left+node.clientLeft);clip.right=Math.min(clip.right,rect.left+node.clientLeft+node.clientWidth);}
        if(/hidden|clip|auto|scroll/.test(style.overflowY)){clip.top=Math.max(clip.top,rect.top+node.clientTop);clip.bottom=Math.min(clip.bottom,rect.top+node.clientTop+node.clientHeight);}
      }
      clips.set(node,clip);return clip;
    }
    const points=[],walker=document.createTreeWalker(element,NodeFilter.SHOW_TEXT),range=document.createRange();
    let node;
    while((node=walker.nextNode())&&points.length<480){
      const parent=node.parentElement;
      if(!node.textContent.trim()||!parent||!parent.checkVisibility()||parent.closest('script,style,[aria-hidden=true]'))continue;
      // Nested cards own their own semantic colours and are sampled separately.
      let nested=false;for(let p=parent;p&&p!==element;p=p.parentElement)if(surfaces.has(p)){nested=true;break;}
      if(nested)continue;
      const clip=clipFor(parent);range.selectNodeContents(node);
      for(const rect of range.getClientRects()){
        if(!rect.width||!rect.height)continue;
        const left=Math.max(rect.left,bounds.left,clip.left),right=Math.min(rect.right,bounds.right,clip.right);
        const top=Math.max(rect.top,bounds.top,clip.top),bottom=Math.min(rect.bottom,bounds.bottom,clip.bottom);
        if(right<=left||bottom<=top)continue;
        for(const u of [.08,.3,.5,.7,.92])points.push({x:left+(right-left)*u,y:(top+bottom)/2,node:parent});
      }
    }
    return points;
  }
  function computedTextAudit(element,samples,clips) {
    // Audit the actual final computed foreground, including inherited CSS and
    // label opacity. The backing remains the same explicitly modelled sample;
    // this is not a screenshot-pixel claim. Disabled controls are WCAG-exempt.
    const nodes=new Set(textPoints(element,element.getBoundingClientRect(),clips).map(point=>point.node));
    const text=[];
    for(const node of nodes){
      if(!node||node.closest(':disabled,[aria-disabled=true]'))continue;
      const style=getComputedStyle(node),ink=colour(style.color),fills=[];let opacity=1;
      for(let child=node;child&&child!==element;child=child.parentElement){
        const paint=getComputedStyle(child);opacity*=Number(paint.opacity);fills.push(colour(paint.backgroundColor));
      }
      opacity*=Number(getComputedStyle(element).opacity);
      let final=samples;
      for(const fill of fills.reverse())if(fill[3])final=final.map(bg=>api.composite(fill,bg,fill[3]));
      const minimum=Math.min(...final.map(bg=>api.contrast(api.composite(ink,bg,ink[3]*opacity),bg)));
      text.push(Object.freeze({element:node.id||node.className||node.tagName.toLowerCase(),
        text:node.textContent.trim().slice(0,100),computedColor:style.color,opacity,minimum}));
    }
    return Object.freeze({minimum:text.length?Math.min(...text.map(item=>item.minimum)):null,
      textCount:text.length,violations:Object.freeze(text.filter(item=>item.minimum<4.5)),texts:Object.freeze(text)});
  }
  async function refresh() {
    if(disposed)return;
    const ticket=++revision;discover();const pixels=await background(ticket);if(disposed||ticket!==revision||!pixels)return;
    const content=[...surfaces].filter(el=>el.matches('.panel,.card')&&!el.matches(floating)).map(element=>({element,rect:element.getBoundingClientRect(),base:tint(element)}));
    const backgrounds=new Map(),materials=new Map(),clips=new WeakMap(),report=[];
    // A floating surface sees the already protected cards underneath it.
    const ordered=[...surfaces].sort((a,b)=>Number(a.matches(floating))-Number(b.matches(floating)));
    for(const element of ordered){
      if(!element.isConnected){surfaces.delete(element);continue;}
      const rect=element.getBoundingClientRect();if(!rect.width||!rect.height||rect.bottom<=0||rect.right<=0||rect.top>=innerHeight||rect.left>=innerWidth||!element.checkVisibility())continue;
      const samples=[],base=tint(element);
      const points=doc.dataset.glassClarityPreset==='clear'?[]:textPoints(element,rect,clips);
      if(!points.length)for(const u of [.1,.3,.5,.7,.9])for(const v of [.15,.4,.65,.85])points.push({x:rect.left+rect.width*u,y:rect.top+rect.height*v});
      for(const {x:screenX,y:screenY} of points){
        const x=Math.max(0,Math.min(plane.width-1,Math.round(screenX/innerWidth*plane.width)));
        const y=Math.max(0,Math.min(plane.height-1,Math.round(screenY/innerHeight*plane.height)));
        const n=(y*plane.width+x)*4;let bg=[pixels.data[n],pixels.data[n+1],pixels.data[n+2]];
        if(element.matches(floating))for(const layer of content){
          if(layer.rect.width&&screenX>=layer.rect.left&&screenX<=layer.rect.right&&screenY>=layer.rect.top&&screenY<=layer.rect.bottom){
            bg=api.composite(layer.base,bg,layer.base[3]);
            const material=materials.get(layer.element);if(material)bg=api.composite(material.backing,bg,material.protect);
          }
        }
        samples.push(api.composite(base,bg,base[3]));
      }
      const result=api.choose(samples,{clarity:doc.dataset.glassClarityPreset||'classic',moreContrast:root.matchMedia('(prefers-contrast: more)').matches});
      materials.set(element,result);
      element.dataset.contrastTone=result.tone;
      for(const [role,key] of [['ink','primary'],['secondary','secondary'],['tertiary','tertiary'],['accent','accent'],['success','success'],['warning','warning'],['danger','danger']]){
        element.style.setProperty('--adaptive-'+role,'rgb('+result[key].join(' ')+')');
      }
      element.style.setProperty('--adaptive-backing','rgb('+result.backing.join(' ')+' / '+result.protect+')');
      // Protection belongs to the material, never to the edges of individual letters.
      element.style.removeProperty('--adaptive-text-shadow');
      const finalBackgrounds=samples.map(bg=>api.composite(result.backing,bg,result.protect));
      backgrounds.set(element,finalBackgrounds);
      const minimum=role=>Math.min(...finalBackgrounds.map(bg=>api.contrast(result[role],bg)));
      report.push(Object.freeze({surface:element.id||element.dataset.deviceId||element.className,
        sampleCount:samples.length,tone:result.tone,backingOpacity:result.protect,
        primaryMin:minimum('primary'),secondaryMin:minimum('secondary'),tertiaryMin:minimum('tertiary')}));
    }
    for(const control of document.querySelectorAll('.diy-control input[type=number],.diy-actions button,.glass-clarity-choices button[aria-pressed=true],.ios-close-appearance,.appearance-popover-head .icon-button,.builtin-wallpaper-grid button,.act-card input:not([type=checkbox]):not([type=radio]),.act-card textarea,.login input,.login-sheet input')){
      const parent=control.closest('[data-contrast-tone]'),samples=backgrounds.get(parent);if(!samples)continue;
      const own=colour(getComputedStyle(control).backgroundColor),result=api.choose(samples.map(bg=>api.composite(own,bg,own[3])));
      control.style.color='rgb('+result.primary.join(' ')+')';
    }
    // Tinted buttons and status chips add another fill on top of the material.
    // Fit their semantic label to that final fill, retaining its hue where possible.
    for(const control of document.querySelectorAll('.act-card button:not(#actGo):not(.act-x),.act-err,.login-err,.history-status')){
      const parent=control.closest('[data-contrast-tone]');let samples=backgrounds.get(parent);if(!samples)continue;
      const fills=[];for(let node=control;node&&node!==parent;node=node.parentElement)fills.push(colour(getComputedStyle(node).backgroundColor));
      for(const fill of fills.reverse())samples=samples.map(bg=>api.composite(fill,bg,fill[3]));
      let role='accent';
      if(control.matches('.act-err,.login-err'))role='warning';
      else if(control.matches('.history-status')){
        const state=control.closest('[data-status]')?.dataset.status;
        role=['done','succeeded','success','completed'].includes(state)?'success':['failed','error'].includes(state)?'warning':
          ['running','in_progress','processing','uploading','finalizing'].includes(state)?'accent':'tertiary';
      }
      const style=getComputedStyle(parent),preferred=colour(style.getPropertyValue('--adaptive-'+role)),primary=colour(style.getPropertyValue('--adaptive-ink'));
      control.style.color='rgb('+api.fitLabel(samples,preferred,primary).join(' ')+')';
    }
    // Filled actions have their own background, independent of the text on glass.
    const action=api.chooseAction(colour(getComputedStyle(doc).getPropertyValue('--action-fill')));
    for(const control of document.querySelectorAll('.primary-button')){
      control.style.backgroundColor='rgb('+action.background.join(' ')+')';
      control.style.color='rgb('+action.primary.join(' ')+')';
    }
    const actual=[];
    for(const [element,samples] of backgrounds)actual.push(Object.freeze({surface:element.id||element.dataset.deviceId||element.className,
      ...computedTextAudit(element,samples,clips)}));
    const audited=actual.filter(item=>item.minimum!==null);
    lastReport=Object.freeze({method:'背景与实际文字坐标取样计算，并审计实际计算文字颜色；非逐像素截图认证',
      clarity:doc.dataset.glassClarityPreset,theme:doc.dataset.theme,visibleSurfaceCount:report.length,
      primaryMin:report.length?Math.min(...report.map(item=>item.primaryMin)):null,
      secondaryMin:report.length?Math.min(...report.map(item=>item.secondaryMin)):null,
      tertiaryMin:report.length?Math.min(...report.map(item=>item.tertiaryMin)):null,
      surfaces:Object.freeze(report),computedTextMin:audited.length?Math.min(...audited.map(item=>item.minimum)):null,
      computedTextSurfaces:Object.freeze(actual),sampledAt:new Date().toISOString()});
  }
  function schedule(){if(pending||disposed)return;pending=requestAnimationFrame(()=>{pending=0;waiting=refresh().catch(()=>{});});}
  const resize=new ResizeObserver(schedule);
  const contentChanges=new MutationObserver(schedule);
  // Ignore sampled CSS variables and pointer highlights: updates must not create an idle loop.
  const rootObserver=new MutationObserver(schedule);
  rootObserver.observe(doc,{attributes:true,attributeFilter:['data-theme','data-palette','data-backdrop','data-backdrop-blur','data-material','data-transparency','data-selected-wallpaper','data-glass-clarity-preset','data-ambient']});
  for(const element of document.querySelectorAll('.backdrop-layer,.ambient-light')){
    const observer=new MutationObserver(schedule);observer.observe(element,{attributes:true,attributeFilter:['style']});backgroundObservers.push(observer);
  }
  document.addEventListener('glasssurfacechange',schedule,{signal:lifetime.signal}); document.addEventListener('click',schedule,{signal:lifetime.signal});
  document.addEventListener('transitionend',event=>{if(['color','background-color'].includes(event.propertyName))schedule();},{signal:lifetime.signal});
  document.addEventListener('scroll',schedule,{capture:true,passive:true,signal:lifetime.signal});window.addEventListener('resize',schedule,{signal:lifetime.signal});
  for(const query of ['(prefers-color-scheme: dark)','(prefers-reduced-transparency: reduce)','(prefers-contrast: more)','(forced-colors: active)'])window.matchMedia(query).addEventListener('change',schedule,{signal:lifetime.signal});
  const unsubscribe=root.ShengyuGlassSettings?.subscribe(schedule);
  api.refresh=async()=>{
    for(let n=0;n<8;n++){
      if(pending){cancelAnimationFrame(pending);pending=0;}
      const ticket=revision+1;waiting=refresh();await waiting;
      if(ticket===revision&&!pending)return;
    }
  };
  api.destroy=()=>{if(disposed)return;disposed=true;revision++;if(pending)cancelAnimationFrame(pending);pending=0;lifetime.abort();resize.disconnect();contentChanges.disconnect();rootObserver.disconnect();backgroundObservers.forEach(observer=>observer.disconnect());unsubscribe?.();surfaces.clear();images.clear();plane.width=plane.height=0;};
  window.addEventListener('pagehide',api.destroy,{once:true});schedule();
})(typeof window!=='undefined'?window:this,function(){
  'use strict';
  const luminance=rgb=>rgb.map(v=>{const s=v/255;return s<=.04045?s/12.92:Math.pow((s+.055)/1.055,2.4);}).reduce((sum,v,n)=>sum+v*[.2126,.7152,.0722][n],0);
  const contrast=(a,b)=>{const x=luminance(a),y=luminance(b);return (Math.max(x,y)+.05)/(Math.min(x,y)+.05);};
  const composite=(a,b,alpha)=>b.slice(0,3).map((v,n)=>Math.round(v*(1-alpha)+a[n]*alpha));
  const palettes={
    dark:{primary:[28,28,30],secondary:[61,61,68],tertiary:[78,78,86],accent:[0,83,179],success:[24,103,51],warning:[132,70,0],danger:[167,33,30]},
    light:{primary:[245,245,247],secondary:[210,211,218],tertiary:[187,189,198],accent:[143,189,242],success:[133,211,160],warning:[240,195,139],danger:[247,161,154]}
  };
  function choose(samples,{moreContrast=false,clarity='clear'}={}) {
    if(!samples.length)samples=[[255,255,255]];
    const black=[0,0,0],white=[255,255,255];
    // A small bright patch must not invert text over a predominantly dark photograph.
    const ordered=samples.slice().sort((a,b)=>luminance(a)-luminance(b)),typical=ordered[Math.floor(ordered.length/2)];
    const tone=contrast(palettes.dark.primary,typical)>=contrast(palettes.light.primary,typical)?'dark':'light';
    const roles=palettes[tone],backing=tone==='dark'?white:black;
    let protect=0;
    // Clear preserves the 0.6.63 fixed semantic palette and full material protection.
    // Classic/more adapt the labels first, so a muted caption cannot whiten the whole card.
    const fixedPalette=clarity==='clear'||moreContrast;
    const readable=alpha=>samples.every(bg=>fixedPalette
      ?Object.entries(roles).every(([role,colour])=>contrast(colour,composite(backing,bg,alpha))>=(role==='primary'&&moreContrast?7:4.5))
      :contrast(roles.primary,composite(backing,bg,alpha))>=5.5);
    while(protect<1&&!readable(protect))protect=Math.round((protect+.02)*100)/100;
    const fitted={...roles};
    if(!fixedPalette){
      const backgrounds=samples.map(bg=>composite(backing,bg,protect));
      for(const role of Object.keys(fitted))if(role!=='primary')fitted[role]=fitLabel(backgrounds,roles[role],roles.primary,4.6);
    }
    return {tone,...fitted,backing,protect};
  }
  function chooseAction(fill) {
    fill=fill.slice(0,3);
    // Prefer the familiar light label on a saturated action. Very bright fills
    // (for example gold) use the dark label; either fill is adjusted only as needed.
    const light=contrast(palettes.light.primary,fill)>=3;
    const primary=palettes[light?'light':'dark'].primary,backing=light?[0,0,0]:[255,255,255];let alpha=0;
    while(alpha<1&&contrast(primary,composite(backing,fill,alpha))<4.5)alpha=Math.round((alpha+.01)*100)/100;
    return {primary,background:composite(backing,fill,alpha)};
  }
  function fitLabel(samples,preferred,primary,minimum=4.5) {
    let amount=0;
    while(amount<1&&samples.some(bg=>contrast(composite(primary,preferred,amount),bg)<minimum))amount=Math.round((amount+.02)*100)/100;
    return composite(primary,preferred,amount);
  }
  function allowsImage(url,baseURI,profileFile) {
    if(typeof url!=='string' || !url)return false;
    if(/^(data:image\/|blob:)/i.test(url))return true;
    try {
      const shared=new URL('../shared/',baseURI),wallpapers=new URL('wallpapers/',shared).href;
      if(url.startsWith(wallpapers) && /\.(webp|jpg|png)$/i.test(url))return true;
      return /^profiles\/[a-z0-9-]+\.(jpg|webp|png)$/i.test(profileFile||'') && url===new URL(profileFile,shared).href;
    }catch(_error){return false;}
  }
  return {luminance,contrast,composite,choose,chooseAction,fitLabel,allowsImage};
});
