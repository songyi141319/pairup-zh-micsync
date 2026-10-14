/* Shengyu preferences for the original React Bits Strands component. */
(function (root, factory) {
  'use strict';
  const api = factory();
  if (typeof module === 'object' && module.exports) { module.exports = api; return; }
  const key = (root.ShengyuAppearance?.prefix || 'shengyu-app-v1-') + 'strands';
  let saved;
  try { saved = JSON.parse(localStorage.getItem(key)); } catch (_) { /* Optional storage. */ }
  let state = api.normalize(saved), persistTimer = 0;
  const subscribers = new Set(), lifetime = new AbortController();
  const copy = () => ({ ...state, colors: state.colors.slice() });
  function persist() {
    clearTimeout(persistTimer); persistTimer = 0;
    try { localStorage.setItem(key, JSON.stringify(state)); } catch (_) { /* Session settings still work. */ }
  }
  function set(patch) {
    state = api.normalize({ ...state, ...patch });
    clearTimeout(persistTimer); persistTimer = setTimeout(persist, 200);
    for (const callback of subscribers) callback(copy());
    return copy();
  }
  root.ShengyuStrandsSettings = Object.freeze({ ...api, get: copy, set, reset: () => set(api.defaults),
    subscribe(callback) { subscribers.add(callback); return () => subscribers.delete(callback); }, storageKey: key });

  const panel = document.getElementById('strands-customization');
  if (!panel) return;
  const controls = new Map();
  const create = (tag, className, text) => {
    const node = document.createElement(tag); if (className) node.className = className;
    if (text !== undefined) node.textContent = text; return node;
  };
  for (const group of api.groups) {
    const section = create('section', 'strands-control-group');
    section.append(create('h3', '', group.title));
    const list = create('div', 'strands-control-grid');
    for (const name of group.names) {
      const field = api.fields[name], row = create('div', 'diy-control strands-control');
      const label = create('label', '', field.label); label.htmlFor = 'strands-' + name;
      const range = create('input'); range.type = 'range'; range.id = label.htmlFor;
      const number = create('input'); number.type = 'number'; number.setAttribute('aria-label', field.label + '数值');
      for (const input of [range, number]) {
        input.min = field.min; input.max = field.max; input.step = field.step;
        input.dataset.strandsSetting = name;
        input.addEventListener('input', () => {
          if (Number.isFinite(input.valueAsNumber)) set({ [name]: input.valueAsNumber });
        }, { signal: lifetime.signal });
        input.addEventListener('change', () => { set({ [name]: Number.isFinite(input.valueAsNumber) ? input.valueAsNumber : state[name] }); number.value = state[name]; }, { signal: lifetime.signal });
      }
      row.append(label, range, number); list.append(row); controls.set(name, { range, number });
    }
    section.append(list); panel.append(section);
  }
  const colorsPanel = document.getElementById('strands-colors');
  function renderColors() {
    colorsPanel.replaceChildren();
    state.colors.forEach((color, index) => {
      const cell = create('div', 'strands-color');
      const label = create('label', '', '颜色 ' + (index + 1)); label.htmlFor = 'strands-color-' + index;
      const input = create('input'); input.type = 'color'; input.id = label.htmlFor; input.value = color; input.dataset.strandsColor = index;
      input.addEventListener('input', () => { const colors = state.colors.slice(); colors[index] = input.value; set({ colors }); }, { signal: lifetime.signal });
      const remove = create('button', 'strands-remove-color', '移除'); remove.type = 'button'; remove.setAttribute('aria-label', '移除颜色 ' + (index + 1));
      remove.addEventListener('click', () => set({ colors: state.colors.filter((_, i) => i !== index) }), { signal: lifetime.signal });
      cell.append(input, label, remove); colorsPanel.append(cell);
    });
    if (!state.colors.length) colorsPanel.append(create('p', 'caption', '正在使用组件内置的连续彩虹。添加颜色可换回自选配色。'));
  }
  const addColor = document.getElementById('strands-add-color');
  addColor.addEventListener('click', () => { if (state.colors.length < 8) set({ colors: [...state.colors, api.defaults.colors[state.colors.length % 3]] }); }, { signal: lifetime.signal });
  document.getElementById('strands-reset').addEventListener('click', () => root.ShengyuStrandsSettings.reset(), { signal: lifetime.signal });
  let colorCount = -1;
  function sync() {
    for (const [name, pair] of controls) {
      pair.range.value = state[name];
      pair.range.style.setProperty('--range-progress', ((state[name] - api.fields[name].min) / (api.fields[name].max - api.fields[name].min) * 100) + '%');
      if (document.activeElement !== pair.number) pair.number.value = state[name];
      pair.range.disabled = pair.number.disabled = Boolean(api.fields[name].glassOnly && !state.glass);
    }
    addColor.disabled = state.colors.length >= 8;
    if (colorCount !== state.colors.length) { colorCount = state.colors.length; renderColors(); }
    else colorsPanel.querySelectorAll('[data-strands-color]').forEach(input => { if (document.activeElement !== input) input.value = state.colors[Number(input.dataset.strandsColor)]; });
    document.getElementById('strands-glass-note').textContent = state.glass ? '玻璃球模式 · 下方折射、色散与球体占比立即生效。' : '切换到玻璃球，可以继续调节折射、色散与球体占比。';
  }
  subscribers.add(sync); sync();
  window.addEventListener('pagehide', () => { if (persistTimer) persist(); lifetime.abort(); subscribers.clear(); }, { once: true });
})(typeof window !== 'undefined' ? window : this, function () {
  'use strict';
  const defaults = Object.freeze({ colors: Object.freeze(['#F97316', '#7C3AED', '#06B6D4']), count: 3, speed: .5,
    amplitude: 1, waviness: 3, thickness: .7, glow: 2.6, taper: .5, spread: 1, intensity: .5,
    saturation: 2, opacity: 1, scale: 2.2, glass: false, refraction: 1, dispersion: 1, glassSize: 1, hueShift: 0 });
  const field = (label, min, max, step, glassOnly = false) => Object.freeze({ label, min, max, step, glassOnly });
  const fields = Object.freeze({
    count: field('线条数量', 1, 10, 1), speed: field('流动速度', 0, 3, .1), amplitude: field('起伏幅度', 0, 3, .1),
    waviness: field('波纹密度', .2, 3, .1), thickness: field('线条粗细', .2, 4, .1), glow: field('辉光', .3, 3, .05),
    taper: field('边缘渐隐', .5, 6, .1), spread: field('线条展开', 0, 3, .1), hueShift: field('色相偏移', 0, 1, .01),
    intensity: field('光线强度', 0, 1, .05), saturation: field('色彩浓度', 0, 2, .05), opacity: field('不透明度', 0, 1, .05),
    scale: field('整体缩放', .3, 3, .1), refraction: field('玻璃折射', 0, 3, .05, true),
    dispersion: field('边缘色散', 0, 4, .05, true), glassSize: field('球体占比', .3, 1, .01, true)
  });
  const groups = Object.freeze([
    Object.freeze({ title: '形态', names: Object.freeze(['count', 'amplitude', 'waviness', 'thickness', 'taper', 'spread', 'scale']) }),
    Object.freeze({ title: '光与流动', names: Object.freeze(['speed', 'glow', 'intensity', 'saturation', 'opacity', 'hueShift']) }),
    Object.freeze({ title: '玻璃球', names: Object.freeze(['refraction', 'dispersion', 'glassSize']) })
  ]);
  function normalize(input) {
    const value = input && typeof input === 'object' ? input : {}, result = { ...defaults };
    for (const [name, spec] of Object.entries(fields)) {
      const number = typeof value[name] === 'number' && Number.isFinite(value[name]) ? value[name] : defaults[name];
      result[name] = Math.max(spec.min, Math.min(spec.max, Number((Math.round((number - spec.min) / spec.step) * spec.step + spec.min).toFixed(4))));
    }
    result.colors = Array.isArray(value.colors) ? value.colors.filter(color => typeof color === 'string' && /^#[0-9a-f]{6}$/i.test(color)).slice(0, 8).map(color => color.toUpperCase()) : defaults.colors.slice();
    result.glass = typeof value.glass === 'boolean' ? value.glass : defaults.glass;
    return result;
  }
  return Object.freeze({ defaults, fields, groups, normalize });
});
