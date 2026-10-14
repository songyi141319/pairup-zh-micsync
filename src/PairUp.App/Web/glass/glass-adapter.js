/* Application adapter only. The bundled Sam Asante/React renderer is unchanged. */
(() => {
  'use strict';
  const root = document.documentElement, instances = new Map();
  const queries = ['(prefers-reduced-motion: reduce)', '(prefers-reduced-transparency: reduce)', '(prefers-contrast: more)', '(forced-colors: active)'].map(query => matchMedia(query));
  const lifetime = new AbortController();
  let pending = 0, disposed = false;
  function enabled() {
    return !document.hidden && root.dataset.transparency !== 'reduce' && !['off', 'reduced'].includes(root.dataset.motionActive)
      && !queries.some(query => query.matches) && window.LiquidGlass?.probeCapabilities().backdropFilterUrl;
  }
  function visible(element) {
    if (!element.isConnected || element.closest('[hidden],[aria-hidden=true]')) return false;
    const rect = element.getBoundingClientRect();
    return rect.width > 20 && rect.height > 20 && rect.bottom > 0 && rect.top < innerHeight;
  }
  function options(element) {
    const prefs = window.ShengyuGlassSettings.get(), popover = element.classList.contains('appearance-popover');
    const optics = Object.fromEntries(['strength', 'depth', 'curvature', 'bend', 'bendWidth', 'dispersion', 'splay', 'specular', 'sheen', 'sheenAngle', 'sheenWidth', 'sheenFalloff', 'glow', 'glowSpread', 'glowFalloff', 'frost', 'saturate', 'brightness'].map(key => [key, prefs[key]]));
    optics.mapSize = 384;
    return { radius: parseFloat(getComputedStyle(element).borderTopLeftRadius) || 24, refraction: 1,
      blur: popover ? prefs.popoverBlur : prefs.frost, saturation: prefs.saturate, dispersion: prefs.dispersion, specular: prefs.specular,
      tint: 'rgb(' + getComputedStyle(root).getPropertyValue('--glass-base').trim() + ')',
      tintOpacity: window.ShengyuGlassSettings.scaleAlpha(1 - prefs[popover ? 'popoverTransparency' : 'chromeTransparency'] / 100), optics };
  }
  function reconcile() {
    pending = 0; if (disposed) return;
    // Reserve the third optical surface for the optional Strands glass ball.
    const candidates = enabled() ? Array.from(document.querySelectorAll('[data-glass-optical]')).filter(visible).slice(0, 2) : [];
    for (const [element, entry] of instances) if (!candidates.includes(element)) { entry.instance.destroy(); instances.delete(element); }
    for (const element of candidates) {
      const next = options(element), signature = JSON.stringify(next);
      const existing = instances.get(element);
      if (existing?.signature === signature && !existing.instance.failed) continue;
      try {
        if (existing && !existing.instance.failed) { existing.instance.set(next); existing.signature = signature; }
        else {
          existing?.instance.destroy();
          instances.set(element, { instance: window.LiquidGlass.attach(element, next), signature });
        }
        element.dataset.glassOptics = 'active';
      } catch (_) { existing?.instance.destroy(); instances.delete(element); element.dataset.glassOptics = 'fallback'; }
    }
  }
  function schedule() { if (!pending && !disposed) pending = requestAnimationFrame(reconcile); }
  const resize = new ResizeObserver(schedule);
  document.querySelectorAll('[data-glass-optical]').forEach(element => resize.observe(element));
  const observer = new MutationObserver(schedule);
  observer.observe(root, { attributes: true, attributeFilter: ['data-theme', 'data-motion-active', 'data-transparency'] });
  const unsubscribe = window.ShengyuGlassSettings.subscribe(schedule);
  queries.forEach(query => query.addEventListener('change', schedule, { signal: lifetime.signal }));
  document.addEventListener('glasssurfacechange', schedule, { signal: lifetime.signal });
  document.addEventListener('visibilitychange', schedule, { signal: lifetime.signal });
  document.addEventListener('scroll', schedule, { passive: true, capture: true, signal: lifetime.signal });
  window.addEventListener('resize', schedule, { signal: lifetime.signal });
  function destroy() {
    if (disposed) return; disposed = true; cancelAnimationFrame(pending); pending = 0;
    for (const entry of instances.values()) entry.instance.destroy(); instances.clear();
    lifetime.abort(); resize.disconnect(); observer.disconnect(); unsubscribe();
  }
  window.addEventListener('pagehide', destroy, { once: true });
  window.ShengyuGlassAdapter = Object.freeze({ get activeCount() { return instances.size; }, refresh: schedule, destroy });
  schedule();
})();
