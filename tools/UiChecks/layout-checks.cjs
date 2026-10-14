/* Headless production-page layout checks. No desktop, audio, or real preferences. */
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const sharp = require('sharp');
const { chromium } = require('playwright');
const webRoot = path.resolve(__dirname, '../../src/PairUp.App/Web');
const output = path.resolve(process.argv[2] || path.join(__dirname, '../../artifacts/layout'));
const defaults = { widthPercent: 100, heightPx: 150, align: 'center', sphereSizePx: 200 };
(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, executablePath: process.env.SHENGYU_BROWSER_PATH });
  const results = [], errors = [], measurements = [];
  const check = async (name, work) => { try { await work(); results.push({ name, passed: true }); } catch (error) { results.push({ name, passed: false, detail: String(error.stack || error) }); } };
  try {
    const context = await browser.newContext({ viewport: { width: 1180, height: 760 } });
    await context.route('**/*', async route => {
      const url = new URL(route.request().url()), file = path.resolve(webRoot, '.' + decodeURIComponent(url.pathname));
      if (url.hostname !== 'shengyu.local' || !file.startsWith(webRoot + path.sep) || !fs.existsSync(file)) return route.abort();
      return route.fulfill({ body: fs.readFileSync(file), contentType: ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml' })[path.extname(file)] || 'application/octet-stream' });
    });
    const page = await context.newPage(); page.on('pageerror', error => errors.push(String(error)));
    const get = () => page.evaluate(() => window.ShengyuStrandsLayout.get());
    const set = patch => page.evaluate(p => window.ShengyuStrandsLayout.set(p), patch);
    const effects = () => page.evaluate(() => window.ShengyuStrandsSettings.get());
    const mode = value => page.evaluate(glass => window.ShengyuStrandsSettings.set({ glass }), value);
    const settle = async () => { await page.waitForTimeout(300); };
    const nav = async name => { await page.locator('[data-page=' + name + ']').click(); await settle(); };
    const edit = async (field, value, type = 'number') => { await page.locator('[data-strands-layout=' + field + '][type=' + type + ']').evaluate((input, value) => { input.value = value; input.dispatchEvent(new Event('input', { bubbles: true })); input.dispatchEvent(new Event('change', { bubbles: true })); }, String(value)); await settle(); };
    const bounds = () => page.locator('[data-strands-host=hero] canvas').boundingBox();
    const active = async () => { await page.waitForFunction(() => window.ShengyuStrandsStatus?.activeInstances === 1 && document.querySelector('[data-strands-host=hero] canvas')); await settle(); };
    await page.goto('https://shengyu.local/index.html?demo=1'); await active();
    await check('new settings use full width, 150px height, center and 200px sphere', async () => { assert.deepEqual(await get(), defaults); assert.equal((await effects()).glass, false); });
    await nav('appearance');
    await check('all three dimensions have paired range and numeric controls', async () => {
      for (const field of ['widthPercent', 'heightPx', 'sphereSizePx']) for (const type of ['range', 'number']) assert.equal(await page.locator('[data-strands-layout=' + field + '][type=' + type + ']').count(), 1);
    });
    await check('width slider and numeric input stay synchronized', async () => { await edit('widthPercent', 37); assert.equal((await get()).widthPercent, 37); assert.equal(await page.locator('[data-strands-layout=widthPercent][type=range]').inputValue(), '37'); await edit('widthPercent', 62, 'range'); assert.equal(await page.locator('[data-strands-layout=widthPercent][type=number]').inputValue(), '62'); });
    await check('height input immediately updates layout', async () => { await edit('heightPx', 241); assert.equal((await get()).heightPx, 241); });
    await check('dimension ranges reject out-of-range and invalid persisted data', async () => { await set({ widthPercent: 500, heightPx: -10, sphereSizePx: 800, align: 'invalid' }); assert.deepEqual(await get(), { widthPercent: 100, heightPx: 80, sphereSizePx: 320, align: 'center' }); });
    await check('mode buttons use glass as the only mode state', async () => { await page.locator('[data-strands-mode=sphere]').last().click(); assert.equal((await effects()).glass, true); assert.deepEqual(Object.keys(await get()).sort(), Object.keys(defaults).sort()); await page.locator('[data-strands-mode=wave]').last().click(); assert.equal((await effects()).glass, false); });
    await check('sphere input and wave dimensions survive repeated switching', async () => { await set({ widthPercent: 42, heightPx: 180 }); await mode(true); await edit('sphereSizePx', 230); await mode(false); assert.deepEqual(await get(), { widthPercent: 42, heightPx: 180, sphereSizePx: 230, align: 'center' }); });
    await check('all four presets preserve color, speed and other effect recipe values', async () => {
      await page.evaluate(() => window.ShengyuStrandsSettings.set({ colors: ['#123456', '#ABCDEF'], speed: 1.4, waviness: 2.4, refraction: 1.75 }));
      const before = await effects(); delete before.glass;
      for (const preset of ['fill', 'medium', 'mini', 'sphere']) { await page.locator('[data-strands-preset=' + preset + ']').click(); const after = await effects(); delete after.glass; assert.deepEqual(after, before); const value = await get(); if (preset !== 'sphere') { assert.equal(value.widthPercent, { fill: 100, medium: 60, mini: 30 }[preset]); assert.equal(value.heightPx, preset === 'mini' ? 100 : 150); } }
      assert.equal((await effects()).glass, true); assert.equal((await get()).sphereSizePx, 200);
    });
    await check('restore layout does not change recipe, mode or speaker state', async () => {
      const before = await effects(), audio = await page.evaluate(() => window.ShengyuUI.getState()); await page.locator('#strands-layout-reset').click();
      assert.deepEqual(await get(), defaults); assert.deepEqual(await effects(), before); assert.deepEqual(await page.evaluate(() => window.ShengyuUI.getState()), audio);
    });
    await check('layout and recipe survive full reload', async () => { await set({ widthPercent: 55, heightPx: 210, align: 'right', sphereSizePx: 260 }); await settle(); const before = await effects(); await page.reload(); assert.deepEqual(await get(), { widthPercent: 55, heightPx: 210, align: 'right', sphereSizePx: 260 }); assert.deepEqual(await effects(), before); });
    await check('existing recipe without a layout key upgrades to layout defaults', async () => {
      await page.evaluate(() => localStorage.removeItem(window.ShengyuStrandsLayout.storageKey)); const before = await effects(); await page.reload(); assert.deepEqual(await get(), defaults); assert.deepEqual(await effects(), before);
    });
    await check('demo and real preference namespaces remain isolated', async () => { await set({ widthPercent: 33 }); await settle(); await page.goto('https://shengyu.local/index.html'); assert.deepEqual(await get(), defaults); assert.equal((await effects()).speed, .5); await page.goto('https://shengyu.local/index.html?demo=1'); assert.equal((await get()).widthPercent, 33); assert.equal((await effects()).speed, 1.4); });
    await page.evaluate(() => { window.ShengyuStrandsSettings.reset(); window.ShengyuStrandsLayout.reset(); }); await active();
    await page.emulateMedia({ reducedMotion: 'reduce' }); await settle();
    await check('wave width changes actual colored pixels rather than only its container', async () => {
      const style = await page.addStyleTag({ content: '[data-strands-host] canvas { background: #111 !important; }' });
      try {
        const samples = [];
        for (const widthPercent of [100, 20]) {
          await set({ widthPercent, heightPx: 150 }); await settle();
          const png = await page.locator('[data-strands-host=hero] canvas').screenshot();
          fs.writeFileSync(path.join(output, 'pixel-wave-' + widthPercent + '.png'), png);
          const { data, info } = await sharp(png).removeAlpha().raw().toBuffer({ resolveWithObject: true });
          let min = info.width, max = -1, count = 0;
          for (let y = 0; y < info.height; y++) for (let x = 0; x < info.width; x++) { const i = (y * info.width + x) * info.channels, c = [data[i], data[i + 1], data[i + 2]]; if (Math.max(...c) - Math.min(...c) > 65 && Math.max(...c) > 70) { min = Math.min(min, x); max = Math.max(max, x); count++; } }
          assert.ok(count > 10, 'No actual colored wave pixels'); samples.push({ widthPercent, canvasWidth: info.width, colorWidth: max - min + 1, pixels: count });
        }
        measurements.push({ name: 'visible-wave-width', samples }); assert.ok(samples[0].colorWidth / samples[1].colorWidth > 3.8, JSON.stringify(samples));
      } finally { await style.evaluate(node => node.remove()); }
    });
    await check('three positions move the actual canvas within available space', async () => {
      await set({ widthPercent: 30 }); const positions = [];
      for (const align of ['left', 'center', 'right']) { await nav('appearance'); await page.locator('[data-strands-align=' + align + ']').click(); assert.equal((await get()).align, align); await nav('play'); await active(); positions.push((await bounds()).x); }
      assert.ok(positions[1] > positions[0] + 20 && positions[2] > positions[1] + 20, JSON.stringify(positions));
    });
    for (const width of [640, 1180, 1920]) for (const size of [80, 320]) {
      await check('no clipping/overlap at viewport ' + width + ', size ' + size, async () => {
        await page.setViewportSize({ width, height: 900 }); await set({ widthPercent: 100, heightPx: size, sphereSizePx: size, align: 'center' });
        await page.evaluate(() => { const state = window.ShengyuUI.getState(); state.sourceName = '耳机（2- Realtek(R) Audio） · AUX 有线音箱 · 很长的真实设备名称显示检查'; state.devices[1].name = state.sourceName; window.shengyuReceive(state); });
        for (const glass of [false, true]) {
          await mode(glass); await active(); const box = await bounds();
          const copy = await page.locator('.hero-copy').boundingBox();
          assert.ok(box.x >= 0 && box.x + box.width <= width + 1);
          assert.ok(box.x >= copy.x + copy.width - 1 || box.y >= copy.y + copy.height - 1 || box.y + box.height <= copy.y + 1, 'Animation overlaps text');
          assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'Page has horizontal overflow');
          if (glass) assert.ok(Math.abs(box.width - box.height) <= 1, 'Sphere canvas is not square: ' + JSON.stringify(box));
          else assert.ok(Math.abs(box.height - size) <= 1, 'Actual height does not match requested size');
          assert.equal(await page.locator('#source-name').textContent(), '耳机（2- Realtek(R) Audio） · AUX 有线音箱 · 很长的真实设备名称显示检查');
          measurements.push({ width, size, glass, box });
        }
      });
    }
    await check('actual visible sphere silhouette has equal horizontal and vertical diameters', async () => {
      await page.setViewportSize({ width: 1180, height: 900 }); await mode(true);
      const style = await page.addStyleTag({ content: '[data-strands-host] canvas { background: #000 !important; }' });
      try {
        for (const sphereSizePx of [80, 200, 320]) {
          await set({ sphereSizePx }); await active();
          const png = await page.locator('[data-strands-host=hero] canvas').screenshot();
          fs.writeFileSync(path.join(output, 'pixel-sphere-' + sphereSizePx + '.png'), png);
          const { data, info } = await sharp(png).removeAlpha().raw().toBuffer({ resolveWithObject: true });
          let x0 = info.width, x1 = -1, y0 = info.height, y1 = -1;
          for (let y = 2; y < info.height - 2; y++) for (let x = 2; x < info.width - 2; x++) { const i = (y * info.width + x) * info.channels; if (Math.max(data[i], data[i + 1], data[i + 2]) > 30) { x0 = Math.min(x0, x); x1 = Math.max(x1, x); y0 = Math.min(y0, y); y1 = Math.max(y1, y); } }
          const diameterX = x1 - x0 + 1, diameterY = y1 - y0 + 1;
          assert.ok(diameterX > sphereSizePx * .8 && diameterY > sphereSizePx * .8, 'No complete sphere silhouette');
          assert.ok(diameterX < sphereSizePx * .97 && diameterY < sphereSizePx * .97, 'Detected canvas edges instead of the sphere silhouette');
          assert.ok(Math.abs(diameterX - diameterY) <= 2, 'Visible sphere is oval');
          measurements.push({ name: 'sphere-silhouette', sphereSizePx, diameterX, diameterY });
        }
      } finally { await style.evaluate(node => node.remove()); }
    });
    await check('small-window appearance controls remain reachable without horizontal overflow', async () => {
      await page.setViewportSize({ width: 640, height: 900 }); await nav('appearance');
      for (const glass of [false, true]) { await mode(glass); await settle(); await page.locator('#strands-layout-reset').scrollIntoViewIfNeeded(); assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)); const selector = '[data-strands-layout=' + (glass ? 'sphereSizePx' : 'widthPercent') + '][type=number]'; await page.locator(selector).scrollIntoViewIfNeeded(); assert.ok(await page.locator(selector).isVisible()); }
      await page.screenshot({ path: path.join(output, '05-small-settings.png') }); await nav('play'); await active();
    });
    await check('switching modes never leaves duplicate canvases', async () => { for (let i = 0; i < 8; i++) { await mode(i % 2 === 0); await settle(); assert.equal(await page.locator('[data-strands-host] canvas').count(), 1); assert.equal(await page.evaluate(() => window.ShengyuStrandsStatus.activeInstances), 1); } });
    await check('reduced motion still renders one static canvas', async () => { assert.equal(await page.evaluate(() => window.ShengyuStrandsStatus.renderMode), 'static'); assert.equal(await page.locator('[data-strands-host] canvas').count(), 1); });
    await page.setViewportSize({ width: 1180, height: 760 });
    await page.goto('https://shengyu.local/index.html?demo=1');
    await page.evaluate(() => {
      window.ShengyuStrandsSettings.reset(); window.ShengyuStrandsLayout.reset();
      // Exact Windows-style labels in isolated screenshot fixtures, not live devices.
      const state = window.ShengyuUI.getState();
      const names = ['扬声器 (HUAWEI Sound Joy-04105)', '耳机 (2- Realtek(R) Audio)', '扬声器 (HUAWEI CM510)', '耳机 (Bose QCEB)'];
      state.sourceName = names[0]; state.devices.forEach((device, i) => { device.name = names[i] || device.name; });
      window.shengyuReceive(state);
    }); await active();
    await page.screenshot({ path: path.join(output, '01-wave-fill.png') });
    await set({ widthPercent: 30, heightPx: 100 }); await settle(); await page.screenshot({ path: path.join(output, '02-wave-mini.png') });
    await mode(true); await settle(); await page.screenshot({ path: path.join(output, '03-glass-sphere.png') });
    await nav('appearance'); await page.locator('.strands-preview').scrollIntoViewIfNeeded();
    await page.waitForFunction(() => window.ShengyuStrandsStatus.host === 'appearance' && document.querySelector('[data-strands-host=appearance] canvas')); await settle();
    await page.screenshot({ path: path.join(output, '04-layout-settings.png'), fullPage: true });
    await mode(false); await set({ widthPercent: 100, heightPx: 150 });
    await page.locator('.strands-preview').scrollIntoViewIfNeeded(); await settle();
    await page.screenshot({ path: path.join(output, '06-wave-settings.png'), fullPage: true });
    await check('high contrast removes decorative renderer', async () => { await page.emulateMedia({ forcedColors: 'active' }); await settle(); assert.equal(await page.evaluate(() => window.ShengyuStrandsStatus.activeInstances), 0); });
    const result = { passed: results.filter(r => r.passed).length, failed: results.filter(r => !r.passed).length, results, errors, measurements,
      boundary: 'Actual headless Chromium production page rendering, isolated simulated devices; no native WPF, audio playback, microphone, desktop control or physical speaker acceptance.' };
    fs.writeFileSync(path.join(output, 'layout-results.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify({ passed: result.passed, failed: result.failed, failedChecks: results.filter(r => !r.passed), errors, output }, null, 2));
    if (result.failed || errors.length) process.exitCode = 1;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
