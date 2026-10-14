(function () {
  'use strict';
  const panel=document.querySelector('.appear,.appearance-popover');
  const head=panel?.querySelector('.appearance-diy-head,.appearance-popover-head');
  if(!head)return;
  if(head.getAttribute('aria-label')==='外观窗口位置，方向键移动，Home 复位')return;
  const key=(window.ShengyuAppearance?.prefix || 'shengyu-app-v1-')+'appearance-position';
  let position=null,drag=null;
  const lifetime=new AbortController();
  const isOpen=()=>!panel.hidden&&panel.getAttribute('aria-hidden')!=='true'&&panel.getClientRects().length>0;
  try {const p=JSON.parse(sessionStorage.getItem(key));if(Number.isFinite(p?.x)&&Number.isFinite(p?.y))position=p;} catch (_error) {}
  head.title='拖动标题可移动外观窗口';head.tabIndex=0;head.setAttribute('role','group');head.setAttribute('aria-label','外观窗口位置，方向键移动，Home 复位');
  function move(x,y) {
    panel.dataset.moved='true';
    const rect=panel.getBoundingClientRect(),safeTop=48,gap=12;
    x=Math.max(gap,Math.min(innerWidth-rect.width-gap,x));
    y=Math.max(safeTop,Math.min(innerHeight-rect.height-gap,y));
    position={x,y};panel.style.left=x+'px';panel.style.top=y+'px';panel.style.right='auto';
    panel.dispatchEvent(new CustomEvent('glasssurfacechange',{bubbles:true}));
  }
  function remember(){if(position)try{sessionStorage.setItem(key,JSON.stringify(position));}catch(_error){}}
  function end(e){
    if(!drag||e&&e.pointerId!==drag.id)return;
    const id=drag.id;drag=null;head.classList.remove('is-dragging');
    if(head.hasPointerCapture(id))head.releasePointerCapture(id);remember();
  }
  head.addEventListener('pointerdown',e=>{
    if(e.button!==0||e.target.closest('button,input,a,select'))return;
    const rect=panel.getBoundingClientRect();drag={id:e.pointerId,x:e.clientX,y:e.clientY,left:rect.left,top:rect.top,active:false};
    head.setPointerCapture(e.pointerId);e.preventDefault();head.focus({preventScroll:true});
  },{signal:lifetime.signal});
  head.addEventListener('pointermove',e=>{
    if(!drag||e.pointerId!==drag.id)return;
    const dx=e.clientX-drag.x,dy=e.clientY-drag.y;
    if(!drag.active&&Math.hypot(dx,dy)<5)return;
    drag.active=true;head.classList.add('is-dragging');move(drag.left+dx,drag.top+dy);
  },{signal:lifetime.signal});
  ['pointerup','pointercancel','lostpointercapture'].forEach(type=>head.addEventListener(type,end,{signal:lifetime.signal}));
  head.addEventListener('keydown',e=>{
    if(e.target!==head)return;
    if(e.key==='Home'){
      e.preventDefault();position=null;delete panel.dataset.moved;panel.style.removeProperty('left');panel.style.removeProperty('top');panel.style.removeProperty('right');
      try{sessionStorage.removeItem(key);}catch(_error){}panel.dispatchEvent(new CustomEvent('glasssurfacechange',{bubbles:true}));return;
    }
    const steps={ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,-1],ArrowDown:[0,1]},step=steps[e.key];
    if(!step)return;e.preventDefault();const r=panel.getBoundingClientRect(),amount=e.shiftKey?24:8;move(r.left+step[0]*amount,r.top+step[1]*amount);remember();
  },{signal:lifetime.signal});
  const observer=new MutationObserver(()=>{if(!isOpen()){end();return;}if(position)move(position.x,position.y);});
  observer.observe(panel,{attributes:true,attributeFilter:['hidden','class','aria-hidden']});
  window.addEventListener('resize',()=>{if(isOpen()&&position)move(position.x,position.y);},{signal:lifetime.signal});
  window.addEventListener('blur',()=>end(),{signal:lifetime.signal});
  window.addEventListener('pagehide',()=>{end();lifetime.abort();observer.disconnect();},{once:true});
})();
(function (scope) {
  'use strict';
  let serial = 0;
  // 共用弹层的语义、焦点与键盘管理；内容与配色仍由各主题提供。
  scope.ShengyuGlassPopover = { open({ document: doc, window: win = scope, title, onClose }) {
    const previous = doc.activeElement;
    const overlay = doc.createElement('div'), panel = doc.createElement('section');
    overlay.className = 'history-overlay'; panel.className = 'history-dialog card content-card';
    panel.setAttribute('role', 'dialog'); panel.setAttribute('aria-modal', 'true'); panel.tabIndex = -1;
    const heading = doc.createElement('h2'); heading.id = 'history-dialog-title-' + ++serial; heading.textContent = title;
    panel.setAttribute('aria-labelledby', heading.id);
    const header = doc.createElement('div'); header.className = 'history-dialog-head';
    const button = doc.createElement('button'); button.type = 'button'; button.textContent = '关闭'; button.setAttribute('aria-label', '关闭' + title);
    const body = doc.createElement('div'); body.className = 'history-dialog-body';
    header.append(heading, button); panel.append(header, body); overlay.append(panel); doc.body.append(overlay);
    const background = [...doc.body.children].filter(node => node !== overlay).map(node => [node, node.inert]);
    for (const [node] of background) node.inert = true;
    let closed = false;
    function close() {
      if (closed) return; closed = true;
      doc.removeEventListener('keydown', keydown);
      for (const [node, inert] of background) node.inert = inert;
      panel.removeAttribute('role'); body.replaceChildren();
      if (typeof overlay.animate === 'function'&&!win.matchMedia('(prefers-reduced-motion: reduce)').matches) {
        const fade = overlay.animate([{opacity:1},{opacity:0}], {duration:120,fill:'forwards'});
        fade.onfinish = () => overlay.remove();
      } else overlay.remove();
      if (previous?.isConnected) previous.focus();
      onClose?.();
    }
    function keydown(event) {
      if (event.key === 'Escape') { event.preventDefault(); close(); return; }
      if (event.key !== 'Tab') return;
      const controls = [...panel.querySelectorAll('button,input,textarea,select,a,summary')].filter(node => !node.disabled && !node.hidden && node.checkVisibility());
      const first = controls[0] || panel, last = controls.at(-1) || panel;
      if (event.shiftKey && (doc.activeElement === first || doc.activeElement === panel)) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && (doc.activeElement === last || !panel.contains(doc.activeElement))) { event.preventDefault(); first.focus(); }
    }
    doc.addEventListener('keydown', keydown); button.addEventListener('click', close);
    overlay.addEventListener('click', event => { if (event.target === overlay) close(); });
    button.focus();
    return { panel, body, close };
  } };
})(typeof window === 'object' ? window : globalThis);
