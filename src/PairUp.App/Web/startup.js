/* Startup never delays a ready view to finish its decorative animation. */
(() => {
  'use strict';
  const overlay = document.getElementById('app-startup');
  const shell = document.querySelector('.app-shell');
  // Keep the approved glass artwork as the primary identity, with a local
  // scalable rendition if the bitmap cannot load. No inline event handlers.
  for (const image of document.querySelectorAll('img[data-fallback-src]')) {
    const useFallback = () => {
      if (!image.dataset.fallbackSrc) return;
      const source = image.dataset.fallbackSrc;
      delete image.dataset.fallbackSrc;
      image.src = source;
    };
    image.addEventListener('error', useFallback, { once:true });
    if (image.complete && image.naturalWidth === 0) useFallback();
  }
  let finished = false;
  function ready(reason = 'ready') {
    if (finished) return;
    finished = true;
    clearTimeout(fallback);
    overlay.hidden = true;
    overlay.dataset.state = reason;
    shell.inert = false;
    document.body.removeAttribute('aria-busy');
  }
  // A failed or silent host must not trap the user behind decoration. The view
  // keeps its honest disconnected state and disabled device controls on timeout.
  const fallback = setTimeout(() => ready('connection-timeout'), 10000);
  window.ShengyuStartup = Object.freeze({ ready });
  window.addEventListener('pagehide', () => { clearTimeout(fallback); }, { once:true });
})();
