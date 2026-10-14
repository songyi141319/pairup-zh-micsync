/* Background-only Chromium verification. It never opens a visible window or audio device. */
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
(async () => {
  const webRoot = path.resolve(__dirname, '../../src/PairUp.App/Web');
  const output = path.resolve(process.argv[2] || path.join(__dirname, '../../artifacts/headless-ui'));
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, executablePath: process.env.SHENGYU_BROWSER_PATH });
  try {
    const context = await browser.newContext({ viewport: { width: 1180, height: 724 }, colorScheme: 'light' });
    const page = await context.newPage();
    const browserErrors = [];
    page.on('pageerror', error => browserErrors.push(String(error)));
    await page.route('**/*', async route => {
      const url = new URL(route.request().url());
      const file = path.resolve(webRoot, '.' + decodeURIComponent(url.pathname));
      if (url.hostname !== 'shengyu.local' || !file.startsWith(webRoot + path.sep) || !fs.existsSync(file)) return route.abort();
      await route.fulfill({ body: fs.readFileSync(file), contentType: ({ '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript', '.svg': 'image/svg+xml', '.png': 'image/png' })[path.extname(file)] || 'application/octet-stream' });
    });
    await page.addInitScript(() => {
      // Previously supplied device names demonstrate the actual label format;
      // these remain isolated fixtures, never live device enumeration.
      const names = ['扬声器 (HUAWEI Sound Joy-04105)', '耳机 (2- Realtek(R) Audio)', '扬声器 (HUAWEI CM510)', '耳机 (Bose QCEB)', '右侧音箱（示例设备）', '低音辅助（示例设备）'];
      const host = { demo: true, sourceId: 'demo-0', sourceName: names[0], masterVolume: 30, isCalibrating: false, isSourceMuted: false, status: '后台界面检查 · 模拟连接', devices: names.map((name, i) => ({ id: 'demo-' + i, name, kind: [0, 2, 3].includes(i) ? '蓝牙' : '有线', isConnected: i !== 0, isSystemDefault: i === 0, volume: 30, latencyMs: i ? 187.5 : 0, bassBoost: 0, treble: 0, isMono: false, isFavorite: i === 1 || i === 2, spatial: { role: 'FullRange', lowCutHz: 0, highCutHz: 0, trimDb: 0 } })) };
      const profile = item => ({ role: item.role, lowCutHz: item.lowCutHz, highCutHz: item.highCutHz, trimDb: item.trimDb });
      const validProfile = item => ['FullRange', 'FrontLeft', 'FrontRight', 'Center', 'SurroundLeft', 'SurroundRight', 'Bass'].includes(item.role) && [item.lowCutHz, item.highCutHz].every(n => Number.isFinite(n) && (n === 0 || n >= 20 && n <= 20000)) && (!item.lowCutHz || !item.highCutHz || item.lowCutHz < item.highCutHz) && Number.isFinite(item.trimDb) && item.trimDb >= -24 && item.trimDb <= 0;
      window.chrome = window.chrome || {};
      window.chrome.webview = { postMessage(message) { setTimeout(() => {
        try {
          const device = host.devices.find(item => item.id === message.id);
          if (message.action === 'master') { host.masterVolume = message.value; host.devices.filter(item => item.isConnected || item.isSystemDefault).forEach(item => { item.volume = message.value; }); }
          else if (message.action === 'device') { if (!device || device.isSystemDefault && !['volume', 'isFavorite'].includes(message.field)) throw new Error('主音源保持原声'); device[message.field] = message.value; }
          else if (message.action === 'mute') host.isSourceMuted = message.value;
          else if (message.action === 'spatial' || message.action === 'spatialBatch') {
            const items = message.action === 'spatial' ? [message] : message.items;
            if (items.some(item => { const output = host.devices.find(d => d.id === item.id); return !output || output.isSystemDefault || !output.isConnected || !validProfile(item); })) throw new Error('声音分工无效');
            items.forEach(item => { host.devices.find(d => d.id === item.id).spatial = profile(item); });
          }
          window.shengyuReceive?.(structuredClone(host));
        } catch (error) { window.shengyuMessage?.({ message: error.message, error: true }); window.shengyuReceive?.(structuredClone(host)); }
      }, 5); } };
    });
    await page.goto('https://shengyu.local/index.html?demo=1');
    await page.waitForFunction(() => window.ShengyuUI?.getState().sourceId === 'demo-0');
    await page.evaluate(fs.readFileSync(path.join(__dirname, 'native-ui-checks.js'), 'utf8'));
    let result;
    for (let n = 0; n < 700; n++) {
      const progress = await page.evaluate(() => window.__uiCheck || {});
      if (progress.capture) { await page.screenshot({ path: path.join(output, path.basename(progress.capture) + '.png') }); await page.evaluate(() => { window.__captureDone = true; }); }
      if (progress.resize) { await page.setViewportSize({ width: progress.resize.width, height: progress.resize.height - 36 }); await page.evaluate(() => { window.__resizeDone = true; }); }
      if (progress.media) {
        const fields = Object.fromEntries(progress.media.features.map(item => [item.name, item.value]));
        await page.emulateMedia({ reducedMotion: fields['prefers-reduced-motion'] || 'no-preference', forcedColors: fields['forced-colors'] || 'none' });
        await page.evaluate(() => { window.__mediaDone = true; });
      }
      if (progress.done) { result = progress; break; }
      await new Promise(resolve => setTimeout(resolve, 150));
    }
    if (!result) throw new Error('Background UI suite timed out');
    result.boundary = 'Real Chromium rendering in a headless background process; simulated host bridge. No WPF/Core Audio, native window automation, microphone, or physical speaker acceptance.';
    result.browserErrors = browserErrors;
    fs.writeFileSync(path.join(output, 'ui-results.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify({ passed: result.passed, failed: result.failed, fatal: result.fatal, failedChecks: result.results?.filter(item => !item.passed), browserErrors, output }, null, 2));
    if (result.failed || browserErrors.length) process.exitCode = 1;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
