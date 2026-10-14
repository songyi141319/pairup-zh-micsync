/* Real headless WebGL capability/failure checks using the production Strands adapter. */
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
const webRoot = path.resolve(process.env.SHENGYU_WEB_ROOT || path.join(__dirname, '../../src/PairUp.App/Web'));
const settings = require(path.join(webRoot, 'strands/strands-settings.js'));
(async () => {
  const output = path.resolve(process.argv[2] || path.join(__dirname, '../../artifacts/headless-ui'));
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, executablePath: process.env.SHENGYU_BROWSER_PATH });
  const results = [];
  try {
    for (const scenario of ['normal', 'webgl1-only', 'no-webgl', 'link-failure', 'glass-link-failure']) {
      const context = await browser.newContext({ viewport: { width: 600, height: 300 } });
      const page = await context.newPage(), browserErrors = [];
      let browserErrorCount = 0;
      page.on('pageerror', error => { browserErrorCount++; if (browserErrors.length < 20) browserErrors.push(String(error)); });
      await page.route('**/*', async route => {
        const url = new URL(route.request().url());
        if (url.hostname !== 'shengyu.local') return route.abort();
        if (url.pathname === '/strands-fixture.html') return route.fulfill({ contentType: 'text/html', body:
          '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="strands/strands-runtime.css"></head><body><div style="width:500px;height:220px;position:relative"><div data-strands-host="fixture" style="position:absolute;inset:0"></div><span class="strands-fallback" hidden></span></div><script src="strands/strands-runtime.js"></script><script src="strands/strands-adapter.js"></script></body></html>' });
        const file = path.resolve(webRoot, '.' + decodeURIComponent(url.pathname));
        if (!file.startsWith(webRoot + path.sep) || !fs.existsSync(file)) return route.abort();
        return route.fulfill({ body: fs.readFileSync(file), contentType: path.extname(file) === '.css' ? 'text/css' : 'text/javascript' });
      });
      await page.addInitScript(({ scenario, defaults }) => {
        const probe = document.createElement('canvas').getContext('webgl');
        window.__webgl1Available = !!probe; probe?.getExtension('WEBGL_lose_context')?.loseContext();
        const original = HTMLCanvasElement.prototype.getContext;
        HTMLCanvasElement.prototype.getContext = function (type, ...args) {
          if (scenario === 'no-webgl' && ['webgl2', 'webgl', 'experimental-webgl'].includes(type)) return null;
          if (scenario === 'webgl1-only' && type === 'webgl2') return null;
          return original.call(this, type, ...args);
        };
        if (scenario === 'link-failure' || scenario === 'glass-link-failure') {
          const originalParameter = WebGL2RenderingContext.prototype.getProgramParameter;
          const programNumbers = new WeakMap(); let programCount = 0;
          WebGL2RenderingContext.prototype.getProgramParameter = function (program, parameter) {
            if (parameter === this.LINK_STATUS) {
              if (!programNumbers.has(program)) programNumbers.set(program, ++programCount);
              if (scenario === 'link-failure' || programNumbers.get(program) === 2) return false;
            }
            return originalParameter.call(this, program, parameter);
          };
        }
        window.ShengyuStrandsSettings = Object.freeze({ defaults,
          get: () => structuredClone(defaults), subscribe: () => () => {} });
      }, { scenario, defaults: settings.defaults });
      try {
        await page.goto('https://shengyu.local/strands-fixture.html');
        if (scenario === 'normal') {
          await page.waitForFunction(() => window.ShengyuStrandsStatus?.activeInstances === 1 && document.querySelector('[data-strands-host] canvas'), null, { timeout: 5000 });
          assert.equal(await page.evaluate(() => window.ShengyuStrandsStatus.lastError), null);
          assert.equal(await page.evaluate(() => window.ShengyuStrandsStatus.renderMode), 'animated');
        } else {
          await page.waitForFunction(() => window.ShengyuStrandsStatus?.lastError && !document.querySelector('.strands-fallback').hidden, null, { timeout: 5000 });
          await page.waitForTimeout(300);
          assert.equal(await page.evaluate(() => window.ShengyuStrandsStatus.activeInstances), 0);
          assert.match(await page.locator('.strands-fallback').textContent(), /当前图形环境/);
          if (scenario === 'webgl1-only') assert.equal(await page.evaluate(() => window.__webgl1Available), true, 'Counterexample must really support WebGL1');
          if (scenario === 'glass-link-failure') assert.match(await page.evaluate(() => window.ShengyuStrandsStatus.lastError), /glass shader/);
        }
        assert.deepEqual(browserErrors, [], 'No unhandled render-loop errors');
        results.push({ name: scenario, passed: true, renderer: await page.evaluate(() => window.ShengyuStrandsStatus) });
      } catch (error) {
        results.push({ name: scenario, passed: false, detail: String(error.stack || error), browserErrorCount, browserErrors });
      } finally { await context.close(); }
    }
    const result = { passed: results.filter(item => item.passed).length, failed: results.filter(item => !item.passed).length, webRoot, results,
      boundary: 'Production renderer and lifecycle adapter in headless Chromium with controlled graphics capability failures. No desktop or audio access.' };
    fs.writeFileSync(path.join(output, 'graphics-results.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify(result, null, 2));
    if (result.failed) process.exitCode = 1;
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
