/* Offline UI foundation checks. No browser, device, microphone, or network use. */
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const root = __dirname;
let checks = 0;
function check(name, run) { run(); checks++; process.stdout.write('PASS ' + name + '\n'); }
const settings = require('./glass/glass-settings.js');
const contrast = require('./glass/glass-contrast.js');
const pointer = require('./glass/glass-pointer.js');
const strands = require('./strands/strands-settings.js');
const strandsLayout = require('./strands/strands-layout.js');
check('31 个玻璃参数完整保留', () => assert.equal(Object.keys(settings.defaults).length, 31));
check('失效参数恢复默认并保留合法零值', () => {
  const result = settings.normalize({ depth: 0, specular: 0, panelTransparency: 200, pointerLight: NaN, brightness: Infinity });
  assert.equal(result.depth, 0); assert.equal(result.specular, 0); assert.equal(result.panelTransparency, 100);
  assert.equal(result.pointerLight, settings.defaults.pointerLight); assert.equal(result.brightness, settings.defaults.brightness);
});
check('无存储默认经典且会话选择仍可用', () => {
  const preference = settings.createClarityPreference({ localStorage: { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); } } });
  assert.equal(preference.get(), 'classic'); assert.equal(preference.set('more'), 'more'); assert.equal(preference.set('invalid'), 'classic');
});
check('三档透感不会改变 DIY 配方', () => {
  const recipe = { ...settings.defaults, panelTransparency: 73, strength: 0.071 };
  settings.cssValues(recipe, 'classic'); settings.cssValues(recipe, 'more'); settings.cssValues(recipe, 'clear');
  assert.equal(recipe.panelTransparency, 73); assert.equal(recipe.strength, 0.071);
});
for (const clarity of ['classic', 'more', 'clear']) {
  for (const [name, samples] of [['浅色', [[235, 244, 248], [215, 222, 240], [245, 227, 237]]], ['深色', [[17, 23, 36], [28, 48, 60], [55, 39, 70]]], ['混合', [[15, 28, 50], [230, 234, 247], [151, 182, 194]]]]) {
    check(clarity + ' / ' + name + ' 背景主次说明对比度 ≥4.5', () => {
      const choice = contrast.choose(samples, { clarity });
      for (const role of ['primary', 'secondary', 'tertiary']) {
        for (const sample of samples) assert.ok(contrast.contrast(choice[role], contrast.composite(choice.backing, sample, choice.protect)) >= 4.5, role);
      }
    });
  }
}
check('鼠标光只在圆角边缘附近增强', () => {
  const box = { left: 0, top: 0, width: 600, height: 400, radius: 32 };
  assert.ok(pointer.sampleLight({ x: 0, y: 200 }, box, 96).gain > .99);
  assert.equal(pointer.sampleLight({ x: 300, y: 200 }, box, 96).gain, 0);
  assert.equal(pointer.sampleLight({ x: 1000, y: 1000 }, box, 96).gain, 0);
});
check('原始离线运行脚本未修改', () => {
  const hash = crypto.createHash('sha256').update(fs.readFileSync(path.join(root, 'glass/glass-runtime.global.js'))).digest('hex');
  assert.equal(hash, '1327b6278c4cc6de4324959860a4ec1d53e9833eb11b7d152b0c4e5fb1758cdc');
});
check('四份开源许可证完整存在', () => {
  for (const name of ['samasante', 'react', 'react-dom', 'scheduler']) assert.ok(fs.readFileSync(path.join(root, 'glass/LICENSE-' + name + '.txt'), 'utf8').length > 500);
});
check('页面脚本语法有效，静态资源全部离线', () => {
  const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  for (const match of html.matchAll(/<script\s+src="([^"]+)"/g)) {
    assert.ok(!/^https?:/.test(match[1])); new vm.Script(fs.readFileSync(path.join(root, match[1]), 'utf8'), { filename: match[1] });
  }
  for (const match of html.matchAll(/<link\s+rel="stylesheet"\s+href="([^"]+)"/g)) assert.ok(fs.existsSync(path.join(root, match[1])));
  assert.ok(html.includes("connect-src 'none'")); assert.ok(!html.includes('user-profile.js'));
});
check('界面说明写明预设与真实设备边界', () => {
  const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  assert.ok(html.includes('不会开启设备')); assert.ok(html.includes('主音源 · 原声'));
  assert.ok(html.includes('模拟扩展')); assert.ok(html.includes('不是杜比 5.1'));
});
check('Strands 18 项默认值逐项符合用户配方', () => {
  assert.deepEqual(strands.defaults, { colors: ['#F97316', '#7C3AED', '#06B6D4'], count: 3, speed: .5,
    amplitude: 1, waviness: 3, thickness: .7, glow: 2.6, taper: .5, spread: 1, intensity: .5,
    saturation: 2, opacity: 1, scale: 2.2, glass: false, refraction: 1, dispersion: 1, glassSize: 1, hueShift: 0 });
  assert.equal(Object.keys(strands.fields).length, 16);
  assert.deepEqual(strands.normalize(), { ...strands.defaults, colors: [...strands.defaults.colors] });
});
check('Strands 范围与官方演示一致，合法零值和颜色均保留', () => {
  const reference = fs.readFileSync(path.join(root, 'strands/vendor/StrandsDemo.reference.jsx'), 'utf8');
  for (const [name, spec] of Object.entries(strands.fields)) {
    const block = [...reference.matchAll(/<PreviewSlider[\s\S]*?\/>/g)].find(match => match[0].includes('value={' + name + '}'))?.[0];
    assert.ok(block, name);
    for (const key of ['min', 'max', 'step']) assert.equal(Number(block.match(new RegExp(key + '=\\{([\\d.]+)\\}'))?.[1]), spec[key], name + '.' + key);
  }
  const result = strands.normalize({ speed: 0, amplitude: 0, spread: 0, intensity: 0, opacity: 0, refraction: 0, dispersion: 0, colors: [], glass: true, count: 999, waviness: -99 });
  for (const name of ['speed', 'amplitude', 'spread', 'intensity', 'opacity', 'refraction', 'dispersion']) assert.equal(result[name], 0);
  assert.deepEqual(result.colors, []); assert.equal(result.glass, true); assert.equal(result.count, 10); assert.equal(result.waviness, .2);
  assert.deepEqual(strands.normalize({ colors: ['#abcdef', 'red', '<script>', '#123456'] }).colors, ['#ABCDEF', '#123456']);
});
check('Strands 原始来源及三个着色器哈希保持一致', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'strands/build-manifest.json'), 'utf8'));
  const source = fs.readFileSync(path.join(root, 'strands/vendor/Strands.jsx'), 'utf8');
  assert.equal(manifest.commit, '496968c4117e21278f13917201b7d615bc5e7450');
  assert.equal(manifest.sourceSha256, '52901975284c1536114a899f6e03494088ef12085f4f1a8931bbd34f4c8006e9');
  assert.equal(crypto.createHash('sha256').update(source).digest('hex'), manifest.sourceSha256);
  for (const match of source.matchAll(/const (VERT|FRAG|GLASS_FRAG) = `([\s\S]*?)`;/g)) assert.equal(crypto.createHash('sha256').update(match[2]).digest('hex'), manifest.shaders[match[1]]);
  assert.equal(Object.keys(manifest.shaders).length, 3);
});
check('Strands 限制许可和全部运行依赖许可随包保留', () => {
  const license = fs.readFileSync(path.join(root, 'strands/vendor/LICENSE-react-bits.md'), 'utf8');
  assert.ok(license.includes('MIT + Commons Clause')); assert.ok(license.includes('David Haz')); assert.ok(license.includes('do not sell, sublicense, or redistribute'));
  for (const name of ['react', 'react-dom', 'scheduler', 'ogl']) assert.ok(fs.readFileSync(path.join(root, 'strands/LICENSE-' + name + '.txt'), 'utf8').length > 500);
});
check('动效布局默认铺满且与 18 项配方分开', () => {
  assert.deepEqual(strandsLayout.defaults, { widthPercent: 100, heightPx: 150, align: 'center', sphereSizePx: 200 });
  assert.deepEqual(strandsLayout.normalize(), strandsLayout.defaults);
  assert.equal(Object.keys(strands.defaults).length, 18);
  assert.ok(!Object.hasOwn(strandsLayout.normalize({ glass: true, colors: ['#FFFFFF'] }), 'glass'));
  assert.ok(!Object.hasOwn(strandsLayout.defaults, 'colors'));
});
check('动效布局范围、步长、无效值和对齐规范化', () => {
  assert.deepEqual(strandsLayout.normalize({ widthPercent: 0, heightPx: -1, sphereSizePx: 9999, align: 'right' }), { widthPercent: 20, heightPx: 80, sphereSizePx: 320, align: 'right' });
  assert.deepEqual(strandsLayout.normalize({ widthPercent: 999, heightPx: 999, sphereSizePx: 0, align: 'left' }), { widthPercent: 100, heightPx: 320, sphereSizePx: 80, align: 'left' });
  assert.deepEqual(strandsLayout.normalize({ widthPercent: NaN, heightPx: Infinity, sphereSizePx: '120', align: 'invalid' }), strandsLayout.defaults);
  assert.deepEqual(strandsLayout.normalize({ widthPercent: 39.8, heightPx: 111.4, sphereSizePx: 211.8, align: 'center' }), { widthPercent: 40, heightPx: 111, sphereSizePx: 212, align: 'center' });
  for (const field of Object.values(strandsLayout.fields)) assert.equal(field.step, 1);
});
check('快捷尺寸只含布局和唯一玻璃模式字段', () => {
  assert.deepEqual(strandsLayout.presets, {
    fill: { glass: false, layout: { widthPercent: 100, heightPx: 150 } },
    medium: { glass: false, layout: { widthPercent: 60, heightPx: 150 } },
    mini: { glass: false, layout: { widthPercent: 30, heightPx: 100 } },
    sphere: { glass: true, layout: { sphereSizePx: 200 } }
  });
  const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  assert.ok(!html.includes('id="strands-glass"'));
  for (const mode of ['wave', 'sphere']) assert.ok(html.includes('data-strands-mode="' + mode + '"'));
});
process.stdout.write('完成：' + checks + ' 项离线界面基础检查通过。真实浏览器和音频设备效果需另行验收。\n');
