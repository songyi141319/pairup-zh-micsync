const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright');
(async () => {
  const webRoot = path.resolve(__dirname, '../../src/PairUp.App/Web');
  const browser = await chromium.launch({ headless: true, executablePath: process.env.SHENGYU_BROWSER_PATH });
  try {
    const page = await browser.newPage({ viewport: { width: 1180, height: 724 } });
    await page.route('**/*', async route => {
      const url = new URL(route.request().url());
      const file = path.resolve(webRoot, '.' + decodeURIComponent(url.pathname));
      if (url.hostname !== 'shengyu.local' || !file.startsWith(webRoot + path.sep) || !fs.existsSync(file)) return route.abort();
      await route.fulfill({ body: fs.readFileSync(file), contentType: ({ '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript', '.svg': 'image/svg+xml' })[path.extname(file)] || 'application/octet-stream' });
    });
    await page.goto('https://shengyu.local/index.html?demo=1');
    await page.waitForFunction(() => window.ShengyuUI?.inspect().received);
    console.log(JSON.stringify(await page.evaluate(async () => {
      const input = document.querySelector('#master-volume'), trace = [];
      for (const type of ['focus', 'blur', 'input', 'change']) input.addEventListener(type, () => trace.push({ type, value: input.value, active: document.activeElement?.id }));
      input.focus(); const focused = document.activeElement === input;
      let incoming = window.ShengyuUI.getState(); incoming.masterVolume = 41; window.shengyuReceive(incoming); const first = input.value;
      input.value = 57; input.dispatchEvent(new Event('input', { bubbles: true }));
      incoming.masterVolume = 44; window.shengyuReceive(incoming); const editing = input.value;
      input.blur();
      return { focused, first, editing, after: input.value, actual: window.ShengyuUI.getState().masterVolume, trace };
    }), null, 2));
  } finally { await browser.close(); }
})();
