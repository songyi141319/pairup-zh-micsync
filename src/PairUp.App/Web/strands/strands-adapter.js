/* Lifecycle and display layout adapter; the original Strands shaders own drawing. */
(() => {
  'use strict';
  const settings = window.ShengyuStrandsSettings, runtime = window.ShengyuStrandsRuntime;
  if (!settings || !runtime) return;
  const hosts = [...document.querySelectorAll('[data-strands-host]')];
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)'), forcedColors = matchMedia('(forced-colors: active)');
  const lifetime = new AbortController();
  let active = null, disposed = false, scheduled = 0, values = settings.get(), lastError = null, mode = 'hidden';
  const failedHosts = new WeakSet();
  function visible(host) {
    if (document.hidden || forcedColors.matches || !host.checkVisibility()) return false;
    const box = host.getBoundingClientRect();
    return box.width > 0 && box.height > 0 && box.bottom > 0 && box.top < innerHeight && box.right > 0 && box.left < innerWidth;
  }
  function stop() {
    if (!active) return;
    const previous = active; active = null;
    previous.instance.destroy();
    previous.host.dataset.strandsStatus = failedHosts.has(previous.host) ? 'unavailable' : 'inactive';
  }
  function fail(host, error) {
    failedHosts.add(host); lastError = error; host.dataset.strandsStatus = 'unavailable';
    // A plain notice is intentional: no imitation animation or connected meter.
    const fallback = host.parentElement.querySelector('.strands-fallback');
    if (fallback) { fallback.hidden = false; fallback.textContent = '当前图形环境暂不能显示彩色波形'; }
    mode = 'unavailable';
    schedule();
  }
  function update() {
    if (disposed) return;
    const host = hosts.find(item => visible(item) && !failedHosts.has(item));
    const paused = reducedMotion.matches || document.documentElement.dataset.motionActive === 'off' || values.speed === 0;
    mode = host ? (paused ? 'static' : 'animated') : hosts.some(item => visible(item) && failedHosts.has(item)) ? 'unavailable' : 'hidden';
    if (active && active.host !== host) stop();
    if (!host) return;
    const props = { ...values, paused };
    if (!active) {
      try {
        const instance = runtime.mount(host, props, error => fail(host, error));
        // A boundary callback can run before mount() has returned. Never publish
        // that failed instance as active, even if a renderer swallowed the error.
        if (failedHosts.has(host)) { instance.destroy(); mode = 'unavailable'; return; }
        active = { host, instance };
      }
      catch (error) { fail(host, String(error)); return; }
    } else {
      try { active.instance.update(props); }
      catch (error) { fail(host, String(error)); }
      if (failedHosts.has(host)) { stop(); mode = 'unavailable'; return; }
    }
    host.dataset.strandsStatus = mode;
  }
  function schedule() { if (scheduled || disposed) return; scheduled = requestAnimationFrame(() => { scheduled = 0; update(); }); }
  const viewport = new IntersectionObserver(schedule); hosts.forEach(host => viewport.observe(host));
  const rootObserver = new MutationObserver(schedule);
  rootObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-motion-active'] });
  const unsubscribe = settings.subscribe(next => { values = next; update(); });
  for (const query of [reducedMotion, forcedColors]) query.addEventListener('change', schedule, { signal: lifetime.signal });
  document.addEventListener('visibilitychange', schedule, { signal: lifetime.signal });
  document.addEventListener('glasssurfacechange', schedule, { signal: lifetime.signal });
  window.addEventListener('resize', schedule, { signal: lifetime.signal });
  function inspect() {
    return Object.freeze({ activeInstances: active ? 1 : 0, activeRefractionSurfaces: active && values.glass ? 1 : 0,
      renderMode: mode, displayMode: values.glass ? 'sphere' : 'wave', logicalAspectRatio: values.glass ? 1 : 1.7,
      layout: Object.freeze(window.ShengyuStrandsLayout?.get() || {}),
      host: active?.host.dataset.strandsHost || null, lastError, parameterCount: Object.keys(settings.defaults).length });
  }
  function destroy() {
    if (disposed) return; disposed = true; if (scheduled) cancelAnimationFrame(scheduled); scheduled = 0;
    stop(); viewport.disconnect(); rootObserver.disconnect(); unsubscribe(); lifetime.abort(); mode = 'disposed';
  }
  Object.defineProperty(window, 'ShengyuStrandsStatus', { get: inspect });
  window.ShengyuStrands = Object.freeze({ inspect, destroy });
  window.addEventListener('pagehide', destroy, { once: true }); schedule();
})();
