/* Pointer proximity is application lighting. Refraction remains the unchanged Sam renderer. */
(function (scope) {
    'use strict';
    function sampleLight(point, box, reach) {
        const values = [point?.x, point?.y, box?.left, box?.top, box?.width, box?.height, reach];
        if (!values.every(Number.isFinite) || box.width <= 0 || box.height <= 0 || reach <= 0) return { gain: 0, angle: 0, x: 0, y: 0 };
        const x = point.x - box.left, y = point.y - box.top;
        const radius = Math.max(0, Math.min(Number.isFinite(box.radius) ? box.radius : 0, box.width / 2, box.height / 2));
        const qx = Math.abs(x - box.width / 2) - (box.width / 2 - radius);
        const qy = Math.abs(y - box.height / 2) - (box.height / 2 - radius);
        const distance = Math.abs(Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - radius);
        return { x, y, gain: Math.pow(Math.max(0, 1 - distance / reach), 2),
            angle: (Math.atan2(y - box.height / 2, x - box.width / 2) * 180 / Math.PI + 540) % 360 };
    }
    if (typeof module === 'object' && module.exports) module.exports = { sampleLight };
    if (!scope.document) return;
    const document = scope.document, root = document.documentElement;
    let surfaces = Array.from(document.querySelectorAll('[data-glass-surface]'));
    const decorations = new WeakMap(), samples = new WeakMap();
    const queries = ['(prefers-reduced-motion: reduce)', '(prefers-reduced-transparency: reduce)',
        '(prefers-contrast: more)', '(forced-colors: active)'].map(value => scope.matchMedia(value));
    let point = null, pending = 0, disposed = false;
    const lifetime = new AbortController();
    function allowed() {
        return !disposed && !document.hidden && !queries.some(query => query.matches)
            && !['off','reduced'].includes(root.dataset.motionActive) && root.dataset.transparency !== 'reduce';
    }
    function sendLight(surface, sample) {
        const previous = samples.get(surface);
        if (!surface.hasAttribute('data-liquid-glass')) { samples.delete(surface); return; }
        if (!previous && !sample.gain) return;
        // Compare against the last delivered light. Small movements must accumulate,
        // and releasing a live light must always restore the original optics.
        if (previous && Boolean(previous.gain) === Boolean(sample.gain)
            && Math.abs(previous.gain - sample.gain) < .04 && Math.abs(previous.angle - sample.angle) < 3) return;
        samples.set(surface, sample);
        surface.dispatchEvent(new CustomEvent('glasspointerlight', { detail: { active: sample.gain > 0, gain: sample.gain, angle: sample.angle } }));
    }
    function clear() {
        if (pending) scope.cancelAnimationFrame(pending);
        pending = 0;
        for (const surface of surfaces) {
            surface.style.setProperty('--pointer-gain','0');
            sendLight(surface, { gain: 0, angle: 0 });
        }
    }
    function update() {
        pending = 0;
        const prefs = scope.ShengyuGlassSettings?.get();
        if (!point || !allowed() || !prefs || prefs.pointerLight <= 0 || prefs.specular <= 0) { clear(); return; }
        const modal = document.querySelector('.activate:not([hidden]),.logview:not([hidden]),#login-layer[aria-hidden="false"]');
        // Read every rectangle before writing styles; no forced layout per surface.
        const readings = surfaces.map(surface => {
            if (!surface.isConnected || surface.closest('[hidden],[aria-hidden="true"]') || (modal && !modal.contains(surface))) return { surface, sample: { gain: 0, angle: 0 } };
            const rect = surface.getBoundingClientRect();
            if (rect.width < 20 || rect.height < 20 || rect.bottom < 0 || rect.top > scope.innerHeight) return { surface, sample: { gain: 0, angle: 0 } };
            const sample = sampleLight(point, { left: rect.left, top: rect.top, width: rect.width, height: rect.height,
                radius: parseFloat(scope.getComputedStyle(surface).borderTopLeftRadius) }, prefs.pointerReach);
            sample.gain = Math.min(1, sample.gain * prefs.pointerLight * Math.min(1.5, prefs.specular));
            return { surface, sample };
        });
        for (const { surface, sample } of readings) {
            if (sample.gain > .001) {
                if (!decorations.has(surface)) {
                    if (scope.getComputedStyle(surface).position === 'static') surface.style.position = 'relative';
                    const rim = document.createElement('span');
                    rim.className = 'glass-pointer-rim'; rim.setAttribute('aria-hidden','true');
                    surface.append(rim); decorations.set(surface,rim);
                }
                surface.style.setProperty('--pointer-x',sample.x.toFixed(1)+'px');
                surface.style.setProperty('--pointer-y',sample.y.toFixed(1)+'px');
            }
            surface.style.setProperty('--pointer-gain',sample.gain.toFixed(3));
            sendLight(surface,sample);
        }
    }
    function schedule() { if (!disposed && !pending && point) pending = scope.requestAnimationFrame(update); }
    function move(event) {
        if (event.pointerType && event.pointerType !== 'mouse' && event.pointerType !== 'pen') return;
        point = { x: event.clientX, y: event.clientY }; schedule();
    }
    function leave() { point = null; clear(); }
    document.addEventListener('pointermove',move,{ passive:true,signal:lifetime.signal });
    document.documentElement.addEventListener('pointerleave',leave,{ passive:true,signal:lifetime.signal });
    scope.addEventListener('blur',leave,{signal:lifetime.signal});
    scope.addEventListener('resize',schedule,{ passive:true,signal:lifetime.signal });
    document.addEventListener('scroll',schedule,{ passive:true,capture:true,signal:lifetime.signal });
    document.addEventListener('visibilitychange',leave,{signal:lifetime.signal});
    document.addEventListener('glasssurfacechange',()=>{clear();surfaces=Array.from(document.querySelectorAll('[data-glass-surface]'));schedule();},{signal:lifetime.signal});
    const unsubscribe=scope.ShengyuGlassSettings?.subscribe(() => { clear(); schedule(); });
    queries.forEach(query => query.addEventListener('change',() => { clear(); schedule(); },{signal:lifetime.signal}));
    const observer=new MutationObserver(() => { clear(); schedule(); });
    observer.observe(root,{ attributes:true,attributeFilter:['data-motion-active','data-transparency'] });
    function destroy(){if(disposed)return;leave();disposed=true;lifetime.abort();observer.disconnect();unsubscribe?.();for(const surface of surfaces)decorations.get(surface)?.remove();surfaces=[];}
    scope.addEventListener('pagehide',destroy,{once:true});
    scope.ShengyuGlassPointer = Object.freeze({ sampleLight, clear, destroy });
})(typeof window === 'undefined' ? globalThis : window);
