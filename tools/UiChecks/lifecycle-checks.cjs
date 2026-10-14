/* Background-only lifecycle/late-host regressions. No desktop or audio access. */
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
(async () => {
  const webRoot = path.resolve(__dirname, '../../src/PairUp.App/Web');
  const output = path.resolve(process.argv[2] || path.join(__dirname, '../../artifacts/headless-ui'));
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, executablePath: process.env.SHENGYU_BROWSER_PATH });
  const results = [], browserErrors = [];
  const check = async (name, action) => { try { await action(); results.push({ name, passed: true }); } catch (error) { results.push({ name, passed: false, detail: String(error.stack || error) }); } };
  try {
    const context = await browser.newContext({ viewport: { width: 1180, height: 724 } });
    await context.route('**/*', async route => {
      const url = new URL(route.request().url());
      const file = path.resolve(webRoot, '.' + decodeURIComponent(url.pathname));
      if (url.hostname !== 'shengyu.local' || !file.startsWith(webRoot + path.sep) || !fs.existsSync(file)) return route.abort();
      await route.fulfill({ body: fs.readFileSync(file), contentType: ({ '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript', '.svg': 'image/svg+xml' })[path.extname(file)] || 'application/octet-stream' });
    });
    const page = await context.newPage();
    page.on('pageerror', error => browserErrors.push(String(error)));
    await page.goto('https://shengyu.local/index.html');
    await check('standalone real page disables device operations until host state arrives', async () => {
      assert.equal(await page.evaluate(() => window.ShengyuUI.inspect().canOperate), false);
      assert.equal(await page.locator('#master-volume').isDisabled(), true);
      assert.equal(await page.evaluate(() => window.ShengyuUI.getState().devices.length), 0);
    });
    await check('late host interface receives the bounded ready handshake', async () => {
      await page.evaluate(() => {
        window.__hostMessages = []; window.chrome = window.chrome || {};
        window.chrome.webview = { postMessage: message => window.__hostMessages.push(message) };
      });
      await page.waitForFunction(() => window.__hostMessages.some(message => message.action === 'ready'));
    });
    await check('late host state enables real controls and exact Windows device names', async () => {
      await page.evaluate(() => window.shengyuReceive({ sourceId: 'source', sourceName: '扬声器 (HUAWEI Sound Joy-04105)', masterVolume: 30,
        devices: [{ id: 'source', name: '扬声器 (HUAWEI Sound Joy-04105)', isSystemDefault: true, volume: 30 },
          { id: 'aux', name: '耳机 (2- Realtek(R) Audio)', isConnected: true, volume: 30, latencyMs: 187.5 }] }));
      assert.equal(await page.evaluate(() => window.ShengyuUI.inspect().hostReceived), true);
      assert.equal(await page.locator('#master-volume').isDisabled(), false);
      assert.equal(await page.locator('.device-card[data-device-id=aux] .device-name').textContent(), '耳机 (2- Realtek(R) Audio)');
    });
    await check('master change posts to host without locally pretending it was applied', async () => {
      await page.locator('#master-volume').evaluate(element => { element.value = '43'; element.dispatchEvent(new Event('change', { bubbles: true })); });
      assert.equal(await page.evaluate(() => window.__hostMessages.at(-1).action), 'master');
      assert.equal(await page.evaluate(() => window.__hostMessages.at(-1).value), 43);
      assert.equal(await page.evaluate(() => window.ShengyuUI.getState().masterVolume), 30);
      await page.evaluate(() => { const state = window.ShengyuUI.getState(); state.masterVolume = 43; window.shengyuReceive(state); });
      assert.equal(await page.locator('#master-volume').inputValue(), '43');
    });
    await check('replaced host interface is used by the next operation', async () => {
      await page.evaluate(() => { window.__replacementMessages = []; window.chrome.webview = { postMessage: message => window.__replacementMessages.push(message) }; });
      await page.locator('#master-volume').evaluate(element => { element.value = '46'; element.dispatchEvent(new Event('change', { bubbles: true })); });
      assert.equal(await page.evaluate(() => window.__replacementMessages.at(-1).value), 46);
    });
    await page.goto('https://shengyu.local/index.html?demo=1');
    await check('browser-only demo remains explicitly marked and locally operable', async () => {
      assert.equal(await page.evaluate(() => window.ShengyuUI.inspect().hostReceived), false);
      assert.equal(await page.locator('#mode-badge').isVisible(), true);
      await page.locator('#master-volume').evaluate(element => { element.value = '38'; element.dispatchEvent(new Event('change', { bubbles: true })); });
      assert.equal(await page.evaluate(() => window.ShengyuUI.getState().masterVolume), 38);
    });
    await check('demo cannot fall back to local success after host connection is lost', async () => {
      await page.evaluate(() => {
        window.chrome = window.chrome || {}; window.chrome.webview = { postMessage: () => {} };
        window.shengyuReceive(window.ShengyuUI.getState()); delete window.chrome.webview;
      });
      await page.locator('#master-volume').evaluate(element => { element.value = '70'; element.dispatchEvent(new Event('change', { bubbles: true })); });
      assert.equal(await page.evaluate(() => window.ShengyuUI.getState().masterVolume), 38);
      assert.equal(await page.evaluate(() => window.ShengyuUI.inspect().canOperate), false);
      assert.equal(await page.locator('#master-volume').isDisabled(), true);
    });
    await check('Strands preferences survive a full page reload and remain preview-isolated', async () => {
      await page.evaluate(() => window.ShengyuStrandsSettings.set({ speed: 1.3, colors: ['#123456', '#654321'], glass: true }));
      await page.waitForTimeout(300); await page.reload();
      assert.deepEqual(await page.evaluate(() => ({ speed: window.ShengyuStrandsSettings.get().speed, colors: window.ShengyuStrandsSettings.get().colors, glass: window.ShengyuStrandsSettings.get().glass })),
        { speed: 1.3, colors: ['#123456', '#654321'], glass: true });
      assert.match(await page.evaluate(() => window.ShengyuStrandsSettings.storageKey), /^shengyu-preview-/);
      await page.goto('https://shengyu.local/index.html');
      assert.equal(await page.evaluate(() => window.ShengyuStrandsSettings.get().speed), .5);
      assert.match(await page.evaluate(() => window.ShengyuStrandsSettings.storageKey), /^shengyu-app-/);
    });
    await check('page teardown releases Strands instance and original glass surfaces', async () => {
      await page.waitForFunction(() => window.ShengyuStrandsStatus?.activeInstances === 1);
      await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pagehide')));
      assert.equal(await page.evaluate(() => window.ShengyuStrandsStatus.activeInstances), 0);
      assert.equal(await page.evaluate(() => window.ShengyuStrandsStatus.renderMode), 'disposed');
      assert.equal(await page.evaluate(() => window.ShengyuGlassAdapter.activeCount), 0);
    });
    const result = { passed: results.filter(item => item.passed).length, failed: results.filter(item => !item.passed).length, results, browserErrors,
      boundary: 'Headless Chromium and simulated message delivery only. No native WPF/Core Audio, visible windows, microphone or physical speakers.' };
    fs.writeFileSync(path.join(output, 'lifecycle-results.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));
    if (result.failed || browserErrors.length) process.exitCode = 1;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
