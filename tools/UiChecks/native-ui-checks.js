/* DOM checks for either the isolated native preview or the background-only headless harness. No audio devices. */
(() => {
  'use strict';
  const results = [], errors = [], contrasts = [];
  const $ = selector => document.querySelector(selector);
  const $$ = selector => [...document.querySelectorAll(selector)];
  const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
  const state = () => window.ShengyuUI.getState();
  const device = id => state().devices.find(item => item.id === id);
  const require = (condition, message) => { if (!condition) throw new Error(message); };
  const until = async (condition, message) => { for (let n = 0; n < 35; n++) { if (condition()) return; await pause(80); } throw new Error(message); };
  const check = async (name, work) => { try { await work(); results.push({ name, passed: true }); } catch (error) { results.push({ name, passed: false, detail: String(error.stack || error) }); } };
  const click = selector => { const element = $(selector); require(element, 'Missing ' + selector); element.click(); };
  const change = (element, value) => {
    require(element, 'Missing input');
    if (element.type === 'checkbox') element.checked = value; else element.value = value;
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
  };
  const send = message => window.chrome.webview.postMessage(message);
  const page = async name => { click('[data-page=' + name + ']'); await pause(120); };
  const assignment = id => $('.preset-device[data-device-id="' + id + '"]');
  function assign(id, role, capability) {
    const row = assignment(id); require(row, 'Missing assignment ' + id);
    change(row.querySelector('.preset-position select'), role);
    change(row.querySelector('.preset-capability select'), capability);
    change(row.querySelector('input'), true);
  }
  async function capture(name, preserveScroll = false) {
    if (!preserveScroll) window.scrollTo(0, 0); await pause(300);
    window.__captureDone = false; window.__uiCheck = { capture: name };
    await until(() => window.__captureDone, 'Capture not acknowledged: ' + name);
    window.__uiCheck = {}; await pause(80);
  }
  async function resize(width, height) {
    window.__resizeDone = false; window.__uiCheck = { resize: { width, height } };
    await until(() => window.__resizeDone, 'Resize not acknowledged'); window.__uiCheck = {}; await pause(300);
  }
  async function media(features) {
    window.__mediaDone = false; window.__uiCheck = { media: { features } };
    await until(() => window.__mediaDone, 'Media emulation not acknowledged'); window.__uiCheck = {}; await pause(200);
  }
  window.addEventListener('error', event => errors.push(String(event.message)));
  window.addEventListener('unhandledrejection', event => errors.push(String(event.reason)));
  (async () => {
    await until(() => window.ShengyuUI?.inspect().received && device('demo-0'), 'Native preview not ready');
    const originalNotice = window.shengyuMessage;
    window.shengyuMessage = message => { window.__lastNotice = message; originalNotice(message); };
    await check('native preview uses six isolated devices', () => {
      require(state().demo && state().devices.length === 6 && state().sourceId === 'demo-0', 'Incorrect preview data');
      require(window.ShengyuUI.inspect().storagePrefix === 'shengyu-preview-v1-', 'Preview preferences not isolated');
    });
    await check('four Chinese pages navigate correctly', async () => {
      for (const name of ['play', 'space', 'guide', 'appearance']) { await page(name); require(!$('#page-' + name).hidden && $$('.page:not([hidden])').length === 1, name); }
      await page('play');
    });
    await check('master volume goes through native bridge and updates enabled endpoints', async () => {
      change($('#master-volume'), 34);
      await until(() => state().masterVolume === 34 && state().devices.every(item => item.volume === 34), 'Master acknowledgement missing');
    });
    await check('delay step is exactly 0.1 ms and native state acknowledges it', async () => {
      click('.device-card[data-device-id="demo-1"] [data-step="0.1"]');
      await until(() => device('demo-1').latencyMs === 187.6, 'Precision step not received');
    });
    await check('focused checkbox reflects rejected/disconnected host state', () => {
      const checkbox = $('.device-card[data-device-id="demo-1"] [data-field=isConnected]');
      checkbox.focus(); checkbox.checked = true;
      const incoming = state(); incoming.devices.find(item => item.id === 'demo-1').isConnected = false;
      window.shengyuReceive(incoming); require(!checkbox.checked, 'Focused checkbox ignored host'); checkbox.blur(); send({ action: 'ready' });
    });
    await until(() => device('demo-1').isConnected, 'Restore preview state');
    await check('focused but idle slider follows host; active editing survives until blur', () => {
      const slider = $('#master-volume'); slider.focus({ preventScroll: true });
      require(document.activeElement === slider, 'Slider could not receive focus');
      const incoming = state(); incoming.masterVolume = 41; window.shengyuReceive(incoming);
      require(Number(slider.value) === 41, 'Idle focused slider ignored host');
      slider.value = 57; slider.dispatchEvent(new Event('input', { bubbles: true }));
      incoming.masterVolume = 44; window.shengyuReceive(incoming);
      require(Number(slider.value) === 57, 'Unfinished edit overwritten');
      slider.blur(); require(Number(slider.value) === 44, 'Blur did not reconcile: value=' + slider.value + ', state=' + state().masterVolume + ', active=' + document.activeElement?.id);
    });
    send({ action: 'ready' });
    await until(() => state().masterVolume === 34, 'Restore preview master');
    await page('space');
    await check('source cannot be assigned or included in presets', () => {
      require(!assignment('demo-0'), 'Source appeared in preset selection');
      require([...$('.spatial-card[data-device-id="demo-0"]').querySelectorAll('input,select,.apply-spatial')].every(input => input.disabled), 'Source controls enabled');
    });
    await check('full device names remain visible, wrap and have an exact tooltip', async () => {
      await page('play');
      const incoming = state();
      const fullName = '耳机（2- Realtek(R) Audio） · AUX 有线音箱 · 用于核对长设备名称显示的示例';
      incoming.devices.find(item => item.id === 'demo-1').name = fullName;
      window.shengyuReceive(incoming);
      const label = $('.device-card[data-device-id="demo-1"] .device-name');
      require(label.textContent === fullName && label.title === fullName, 'Full device name or tooltip lost');
      require(getComputedStyle(label).whiteSpace !== 'nowrap' && getComputedStyle(label).textOverflow !== 'ellipsis', 'Device name truncated');
      send({ action: 'ready' }); await pause(150); await page('space');
    });
    await check('stereo requires explicit left/right and applies only selected outputs', async () => {
      click('[data-preset=stereo]'); require($('#apply-preset').disabled, 'Empty selection accepted');
      assign('demo-1', 'FrontLeft', 'full'); require($('#apply-preset').disabled, 'One-sided stereo accepted');
      assign('demo-2', 'FrontRight', 'full'); require(!$('#apply-preset').disabled, 'Stereo selection rejected');
      click('#apply-preset'); await until(() => device('demo-1').spatial.role === 'FrontLeft' && device('demo-2').spatial.role === 'FrontRight', 'Stereo not acknowledged');
      require(device('demo-3').spatial.role === 'FullRange' && device('demo-0').spatial.role === 'FullRange', 'Unselected outputs changed');
    });
    await check('music surround assigns rear filter and trim, preserving volume and delays', async () => {
      const before = state(); click('[data-preset=surround]');
      assign('demo-1', 'FrontLeft', 'full'); assign('demo-2', 'FrontRight', 'full');
      assign('demo-3', 'SurroundLeft', 'small'); assign('demo-4', 'SurroundRight', 'small');
      click('#apply-preset'); await until(() => device('demo-3').spatial.role === 'SurroundLeft', 'Surround not acknowledged');
      require(device('demo-3').spatial.lowCutHz === 100 && device('demo-4').spatial.trimDb === -6, 'Rear profile incorrect');
      require(state().devices.every(item => { const old = before.devices.find(d => d.id === item.id); return old.volume === item.volume && old.latencyMs === item.latencyMs && old.isConnected === item.isConnected; }), 'Preset changed existing volume/delay/switch');
    });
    await check('bass requires confirmed bass capability and applies 100 Hz limit', async () => {
      click('[data-preset=bass]'); assign('demo-5', 'Bass', 'small'); require($('#apply-preset').disabled, 'Small speaker accepted as bass');
      assign('demo-5', 'Bass', 'bass'); click('#apply-preset');
      await until(() => device('demo-5').spatial.role === 'Bass', 'Bass not acknowledged');
      require(device('demo-5').spatial.highCutHz === 100 && device('demo-5').spatial.trimDb === -3, 'Incorrect bass profile');
    });
    await check('inactive outputs are excluded from presets', async () => {
      send({ action: 'device', id: 'demo-5', field: 'isConnected', value: false });
      await until(() => assignment('demo-5').querySelector('input').disabled, 'Inactive selection not disabled');
      require(!assignment('demo-5').querySelector('input').checked, 'Inactive output remains selected');
      send({ action: 'device', id: 'demo-5', field: 'isConnected', value: true });
      await until(() => device('demo-5').isConnected, 'Restore output');
    });
    await check('invalid mixed batch is rejected without partial application', async () => {
      const before = JSON.stringify(state().devices.map(item => item.spatial)); window.__lastNotice = null;
      send({ action: 'spatialBatch', items: ['demo-1', 'demo-0'].map(id => ({ id, role: 'Center', lowCutHz: 0, highCutHz: 0, trimDb: -3 })) });
      await until(() => window.__lastNotice?.error === true, 'Invalid batch not rejected');
      require(JSON.stringify(state().devices.map(item => item.spatial)) === before, 'Invalid batch partially applied');
    });
    await check('whole-room preset restores full music only on selected speaker', async () => {
      click('[data-preset=whole]'); assign('demo-5', 'FullRange', 'full'); click('#apply-preset');
      await until(() => device('demo-5').spatial.role === 'FullRange', 'Full music not restored');
      require(device('demo-5').spatial.highCutHz === 0 && device('demo-5').spatial.trimDb === 0 && device('demo-3').spatial.role === 'SurroundLeft', 'Whole-room modified unselected speaker');
    });
    await check('manual profile supports mid band; host rejects reversed bands', async () => {
      const card = $('.spatial-card[data-device-id="demo-5"]');
      for (const [key, value] of Object.entries({ role: 'Center', lowCutHz: 100, highCutHz: 8000, trimDb: -4 })) change(card.querySelector('[data-spatial=' + key + ']'), value);
      card.querySelector('.apply-spatial').click(); await until(() => device('demo-5').spatial.highCutHz === 8000, 'Manual settings not acknowledged');
      window.__lastNotice = null; send({ action: 'spatial', id: 'demo-5', role: 'Center', lowCutHz: 8000, highCutHz: 100, trimDb: 0 });
      await until(() => window.__lastNotice?.error === true, 'Reversed frequency band accepted');
      require(device('demo-5').spatial.lowCutHz === 100, 'Invalid values replaced profile');
    });
    await check('calibration state disables audio mutations', () => {
      const incoming = state(); incoming.isCalibrating = true; window.shengyuReceive(incoming);
      require($('#master-volume').disabled && $$('.device-card [data-field=isConnected]').every(input => input.disabled), 'Controls enabled while calibrating');
      send({ action: 'ready' });
    });
    await until(() => !state().isCalibrating, 'Restore calibration state');
    click('[data-preset=surround]'); assign('demo-1', 'FrontLeft', 'full'); assign('demo-2', 'FrontRight', 'full'); assign('demo-3', 'SurroundLeft', 'small'); assign('demo-4', 'SurroundRight', 'small');
    $('#toast').hidden = true;
    await capture('07-music-space'); await page('guide'); await capture('08-placement-guide');
    await page('appearance');
    await check('31 original DIY fields and three clarity choices remain available', () => {
      require($$('[data-glass-setting]').length === 31, 'DIY field count changed');
      require($$('[data-clarity]').length === 3, 'Clarity count incorrect');
      const old = window.ShengyuGlassSettings.get(); window.ShengyuGlassSettings.set({ depth: 0.53 });
      for (const name of ['classic', 'more', 'clear']) { click('[data-clarity=' + name + ']'); require(window.ShengyuGlassSettings.get().depth === 0.53, 'Clarity reset DIY'); }
      window.ShengyuGlassSettings.set(old);
    });
    const strandsDefaults = { colors: ['#F97316', '#7C3AED', '#06B6D4'], count: 3, speed: .5, amplitude: 1, waviness: 3,
      thickness: .7, glow: 2.6, taper: .5, spread: 1, intensity: .5, saturation: 2, opacity: 1, scale: 2.2,
      glass: false, refraction: 1, dispersion: 1, glassSize: 1, hueShift: 0 };
    const strandSettings = () => window.ShengyuStrandsSettings.get();
    await check('all 18 Strands settings retain the exact requested defaults', () => {
      require(Object.keys(strandSettings()).length === 18, 'Parameter count changed');
      for (const [name, value] of Object.entries(strandsDefaults)) require(JSON.stringify(strandSettings()[name]) === JSON.stringify(value), name + ' default changed');
      require($$('[data-strands-setting][type=range]').length === 16 && $$('[data-strands-setting][type=number]').length === 16, 'Numeric controls missing');
      require($$('[data-strands-mode=wave]').length && $$('[data-strands-mode=sphere]').length && $$('[data-strands-color]').length === 3, 'Mode controls or colors missing');
    });
    await check('all numerical Strands controls update their corresponding setting', () => {
      click('[data-strands-mode=sphere]');
      for (const [name, spec] of Object.entries(window.ShengyuStrandsSettings.fields)) {
        const input = $('[data-strands-setting=' + name + '][type=number]');
        require(!input.disabled, name + ' disabled'); change(input, spec.min);
        require(strandSettings()[name] === spec.min, name + ' did not update');
        change(input, strandsDefaults[name]);
      }
      click('#strands-reset');
    });
    await check('Strands colors support edits, eight entries and empty rainbow palette', () => {
      change($('[data-strands-color="0"]'), '#123456'); require(strandSettings().colors[0] === '#123456', 'Color edit lost');
      while (strandSettings().colors.length < 8) click('#strands-add-color');
      require($('#strands-add-color').disabled && $$('[data-strands-color]').length === 8, 'Eight-color limit not reflected');
      while (strandSettings().colors.length) click('.strands-remove-color');
      require(!$$('[data-strands-color]').length && $('#strands-colors').textContent.includes('彩虹'), 'Empty palette fallback missing');
      click('#strands-reset'); require(strandSettings().colors.join() === strandsDefaults.colors.join(), 'Palette reset failed');
    });
    await check('Strands glass-only controls follow the glass toggle', () => {
      for (const name of ['refraction', 'dispersion', 'glassSize']) require($('[data-strands-setting=' + name + '][type=number]').disabled, name + ' should be disabled');
      click('[data-strands-mode=sphere]');
      require(strandSettings().glass && !$('#strands-refraction').disabled, 'Glass setting did not enable');
      click('[data-strands-mode=wave]');
    });
    await check('Strands values persist locally after an edit', async () => {
      change($('#strands-speed'), .8); await pause(300);
      const saved = JSON.parse(localStorage.getItem(window.ShengyuStrandsSettings.storageKey));
      require(saved.speed === .8 && saved.colors.join() === strandsDefaults.colors.join(), 'Saved settings differ');
      click('#strands-reset'); await pause(250);
    });
    await check('official Strands canvas mounts in the visible DIY preview', async () => {
      $('#strands-title').scrollIntoView({ block: 'start', behavior: 'instant' });
      await until(() => window.ShengyuStrandsStatus.host === 'appearance' && $('[data-strands-host=appearance] canvas'), 'Strands did not render: ' + JSON.stringify(window.ShengyuStrandsStatus));
      require(window.ShengyuStrandsStatus.activeInstances === 1 && window.ShengyuStrandsStatus.renderMode === 'animated' && !window.ShengyuStrandsStatus.lastError, 'Invalid renderer state');
      const canvas = $('[data-strands-host=appearance] canvas'); require(canvas.width > 0 && canvas.height > 0, 'Empty canvas');
    });
    await capture('14-strands-diy', true);
    await check('Strands and liquid glass stay within three active refraction surfaces', async () => {
      click('[data-strands-mode=sphere]'); await pause(150);
      require(window.ShengyuStrandsStatus.activeRefractionSurfaces === 1 && window.ShengyuGlassAdapter.activeCount + window.ShengyuStrandsStatus.activeRefractionSurfaces <= 3, 'Too many active glass surfaces');
      click('[data-strands-mode=wave]');
    });
    await check('Strands stops or unmounts when motion or page visibility changes', async () => {
      change($('#motion-toggle'), false); await until(() => window.ShengyuStrandsStatus.renderMode === 'static', 'Motion toggle not applied');
      change($('#motion-toggle'), true); await until(() => window.ShengyuStrandsStatus.renderMode === 'animated', 'Motion did not resume');
      await page('space'); await until(() => window.ShengyuStrandsStatus.activeInstances === 0, 'Hidden page kept Strands alive');
      await page('play'); await until(() => window.ShengyuStrandsStatus.host === 'hero', 'Hero Strands did not remount');
      await page('appearance');
    });
    await capture('09-appearance');
    await check('appearance popover opens, Escape closes and restores focus', async () => {
      click('.quick-appearance'); await pause(150); require(!$('#appearance-popover').hidden, 'Popover closed');
      require(document.activeElement === $('#close-appearance'), 'Popover focus not set');
      await capture('10-glass-controls');
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      require($('#appearance-popover').hidden && document.activeElement.classList.contains('quick-appearance'), 'Focus not restored');
    });
    await page('play');
    await check('ambient and motion can be independently disabled', async () => {
      change($('#ambient-toggle'), false); require(document.documentElement.dataset.ambient === 'off', 'Ambient not off'); change($('#ambient-toggle'), true);
      change($('#motion-toggle'), false); await pause(150); require(window.ShengyuGlassAdapter.activeCount === 0, 'Optical motion did not stop'); change($('#motion-toggle'), true);
      change($('#transparency-toggle'), true); await pause(150); require(window.ShengyuGlassAdapter.activeCount === 0, 'Transparency fallback not applied'); change($('#transparency-toggle'), false);
    });
    let shot = 1;
    for (const theme of ['light', 'dark']) for (const clarity of ['classic', 'more', 'clear']) {
      await check(theme + '/' + clarity + ' rendered text contrast', async () => {
        click('[data-theme-choice=' + theme + ']'); click('[data-clarity=' + clarity + ']');
        await pause(180); await window.ShengyuGlassContrast.refresh();
        const report = structuredClone(window.ShengyuGlassContrastReport); contrasts.push(report);
        require(report.visibleSurfaceCount > 2 && report.surfaces.every(item => item.sampleCount > 0), 'No real surface samples');
        require(report.primaryMin >= 4.5 && report.secondaryMin >= 4.5 && report.tertiaryMin >= 4.5, 'Text contrast below 4.5: ' + JSON.stringify(report));
        require(report.computedTextMin >= 4.5 && report.computedTextSurfaces.every(item => !item.violations.length), 'Actual computed text below 4.5');
        const sync = $('.sync-card'), syncStyle = getComputedStyle(sync);
        const rgb = value => (value.match(/[\d.]+/g) || []).slice(0, 3).map(Number).join(',');
        require(rgb(getComputedStyle(sync.querySelector('strong')).color) === rgb(syncStyle.getPropertyValue('--adaptive-ink')), 'Actual calibration title ignores adaptive ink');
        require(rgb(getComputedStyle(sync.querySelector('small')).color) === rgb(syncStyle.getPropertyValue('--adaptive-secondary')), 'Actual calibration caption ignores adaptive ink');
        require(window.ShengyuGlassAdapter.activeCount > 0 && window.ShengyuGlassAdapter.activeCount <= 3, 'Original refraction renderer inactive or over budget');
      });
      await capture(String(shot++).padStart(2, '0') + '-' + theme + '-' + clarity);
    }
    await check('mouse edge highlight responds to pointer location', async () => {
      const surface = $('.main-nav'), rect = surface.getBoundingClientRect();
      document.dispatchEvent(new PointerEvent('pointermove', { pointerType: 'mouse', clientX: rect.left + 2, clientY: rect.top + rect.height / 2, bubbles: true }));
      await pause(150); require(Number(surface.style.getPropertyValue('--pointer-gain')) > 0, 'Pointer rim did not respond');
      document.documentElement.dispatchEvent(new PointerEvent('pointerleave', { pointerType: 'mouse' }));
    });
    await check('operating-system reduced-motion disables optical animation', async () => {
      await media([{ name: 'prefers-reduced-motion', value: 'reduce' }]);
      require(matchMedia('(prefers-reduced-motion: reduce)').matches && window.ShengyuGlassAdapter.activeCount === 0, 'Reduced-motion not respected');
      require(window.ShengyuStrandsStatus.renderMode === 'static', 'Strands did not respect reduced motion');
      await media([]);
    });
    await check('operating-system high contrast uses readable fallback', async () => {
      await media([{ name: 'forced-colors', value: 'active' }]);
      require(matchMedia('(forced-colors: active)').matches && window.ShengyuGlassAdapter.activeCount === 0, 'Forced colors not respected');
      require(window.ShengyuStrandsStatus.activeInstances === 0, 'Strands did not stop in forced colors');
      await capture('11-high-contrast'); await media([]);
    });
    await check('640 px window has no horizontal overflow on any page', async () => {
      await resize(640, 720);
      for (const name of ['play', 'space', 'guide', 'appearance']) {
        await page(name); require(document.documentElement.scrollWidth <= innerWidth + 1, name + ' overflows: ' + document.documentElement.scrollWidth + '/' + innerWidth);
      }
      await page('play'); await capture('12-small-window'); await resize(1180, 760);
    });
    await check('visible form controls have accessible labels', () => {
      const unlabeled = $$('input,select').filter(input => !input.disabled && input.getClientRects().length && !input.getAttribute('aria-label') && !input.getAttribute('aria-labelledby') && !input.labels?.length);
      require(!unlabeled.length, 'Unlabeled controls: ' + unlabeled.map(input => input.outerHTML).join('\n'));
    });
    await check('empty device state remains usable and ready restores devices', async () => {
      const incoming = state(); incoming.devices = []; incoming.sourceId = ''; incoming.sourceName = ''; window.shengyuReceive(incoming);
      require(!$('#devices-empty').hidden && !$('.device-card'), 'Empty state not displayed'); send({ action: 'ready' });
      await until(() => state().devices.length === 6, 'Devices not restored');
    });
    await check('no uncaught browser errors', () => require(!errors.length, errors.join('\n')));
    click('[data-theme-choice=light]'); click('[data-clarity=more]'); await page('play');
    await capture('13-final-playback');
    window.__uiCheck = { done: true, passed: results.filter(item => item.passed).length, failed: results.filter(item => !item.passed).length, results, contrasts, errors, userAgent: navigator.userAgent,
      boundary: 'Actual WPF/WebView2 rendered UI with isolated native demo state. No physical audio, microphone, or production settings used. Contrast values are calculated from rendered element background samples, not pixel-by-pixel certification.' };
  })().catch(error => { window.__uiCheck = { done: true, passed: 0, failed: 1, fatal: String(error.stack || error), results, errors }; });
})();
