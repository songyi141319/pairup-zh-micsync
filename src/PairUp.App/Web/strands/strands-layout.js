/* Visual placement is independent of the original 18-value Strands recipe. */
(function (root, factory) {
  'use strict';
  const api = factory();
  if (typeof module === 'object' && module.exports) { module.exports = api; return; }
  const settings = root.ShengyuStrandsSettings;
  if (!settings) return;
  const key = (root.ShengyuAppearance?.prefix || 'shengyu-app-v1-') + 'strands-layout';
  let saved;
  try { saved = JSON.parse(localStorage.getItem(key)); } catch (_) { /* Optional storage. */ }
  let state = api.normalize(saved), persistTimer = 0;
  const subscribers = new Set(), lifetime = new AbortController(), controls = new Map();
  const copy = () => ({ ...state });
  function persist() {
    clearTimeout(persistTimer); persistTimer = 0;
    try { localStorage.setItem(key, JSON.stringify(state)); } catch (_) { /* Session values remain usable. */ }
  }
  function set(patch) {
    state = api.normalize({ ...state, ...patch });
    clearTimeout(persistTimer); persistTimer = setTimeout(persist, 200);
    sync();
    for (const callback of subscribers) callback(copy());
    return copy();
  }
  function applyPreset(name) {
    const preset = api.presets[name]; if (!preset) return copy();
    // Only the existing glass mode flag changes in the recipe. Colours and all
    // numeric recipe values remain exactly as the user set them.
    set(preset.layout);
    if (settings.get().glass !== preset.glass) settings.set({ glass: preset.glass });
    return copy();
  }
  root.ShengyuStrandsLayout = Object.freeze({ ...api, get: copy, set, reset: () => set(api.defaults), applyPreset,
    subscribe(callback) { subscribers.add(callback); return () => subscribers.delete(callback); }, storageKey: key });
  const create = (tag, className, text) => {
    const node = document.createElement(tag); if (className) node.className = className;
    if (text !== undefined) node.textContent = text; return node;
  };
  for (const [name, spec] of Object.entries(api.fields)) {
    const parent = document.getElementById(spec.mode === 'wave' ? 'strands-layout-wave' : 'strands-layout-sphere');
    if (!parent) continue;
    const row = create('div', 'diy-control strands-control strands-layout-control');
    const label = create('label', '', spec.label); label.htmlFor = 'strands-layout-' + name;
    const unit = create('span', 'strands-layout-unit', spec.unit); label.append(unit);
    const range = create('input'); range.type = 'range'; range.id = label.htmlFor;
    const number = create('input'); number.type = 'number'; number.setAttribute('aria-label', spec.label + '数值，' + spec.unit);
    for (const input of [range, number]) {
      input.min = spec.min; input.max = spec.max; input.step = spec.step; input.dataset.strandsLayout = name;
      input.addEventListener('input', () => { if (Number.isFinite(input.valueAsNumber)) set({ [name]: input.valueAsNumber }); }, { signal: lifetime.signal });
      input.addEventListener('change', () => { set({ [name]: Number.isFinite(input.valueAsNumber) ? input.valueAsNumber : state[name] }); number.value = state[name]; }, { signal: lifetime.signal });
      input.addEventListener('blur', () => { number.value = state[name]; }, { signal: lifetime.signal });
    }
    row.append(label, range, number); parent.append(row); controls.set(name, { range, number });
  }
  document.querySelectorAll('button[data-strands-mode]').forEach(button => button.addEventListener('click', () => {
    settings.set({ glass: button.dataset.strandsMode === 'sphere' });
  }, { signal: lifetime.signal }));
  document.querySelectorAll('[data-strands-align]').forEach(button => button.addEventListener('click', () => set({ align: button.dataset.strandsAlign }), { signal: lifetime.signal }));
  document.querySelectorAll('[data-strands-preset]').forEach(button => button.addEventListener('click', () => applyPreset(button.dataset.strandsPreset), { signal: lifetime.signal }));
  document.getElementById('strands-layout-reset')?.addEventListener('click', () => root.ShengyuStrandsLayout.reset(), { signal: lifetime.signal });
  function sync() {
    const glass = settings.get().glass, mode = glass ? 'sphere' : 'wave', doc = document.documentElement;
    doc.style.setProperty('--strands-wave-width', state.widthPercent + '%');
    doc.style.setProperty('--strands-wave-height', state.heightPx + 'px');
    doc.style.setProperty('--strands-sphere-size', state.sphereSizePx + 'px');
    doc.style.setProperty('--strands-justify', { left: 'flex-start', center: 'center', right: 'flex-end' }[state.align]);
    document.querySelectorAll('[data-strands-host]').forEach(host => { host.dataset.strandsShape = mode; });
    document.querySelectorAll('button[data-strands-mode]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.strandsMode === mode)));
    document.querySelectorAll('[data-strands-align]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.strandsAlign === state.align)));
    document.querySelectorAll('[data-strands-preset]').forEach(button => {
      const preset = api.presets[button.dataset.strandsPreset];
      button.setAttribute('aria-pressed', String(preset.glass === glass && Object.entries(preset.layout).every(([name, value]) => state[name] === value)));
    });
    for (const [name, pair] of controls) {
      pair.range.value = state[name];
      if (document.activeElement !== pair.number) pair.number.value = state[name];
      const spec = api.fields[name];
      pair.range.style.setProperty('--range-progress', ((state[name] - spec.min) / (spec.max - spec.min) * 100) + '%');
    }
    const wave = document.getElementById('strands-layout-wave'), sphere = document.getElementById('strands-layout-sphere');
    if (wave) wave.hidden = glass; if (sphere) sphere.hidden = !glass;
    const summary = document.getElementById('strands-layout-summary');
    if (summary) summary.textContent = glass ? '玻璃球区域 ' + state.sphereSizePx + ' 像素 · 保持正圆' : '波形长度 ' + state.widthPercent + '% · 高度 ' + state.heightPx + ' 像素';
  }
  const unsubscribeSettings = settings.subscribe(sync); sync();
  window.addEventListener('pagehide', () => { if (persistTimer) persist(); lifetime.abort(); unsubscribeSettings(); subscribers.clear(); }, { once: true });
})(typeof window !== 'undefined' ? window : this, function () {
  'use strict';
  const defaults = Object.freeze({ widthPercent: 100, heightPx: 150, align: 'center', sphereSizePx: 200 });
  const fields = Object.freeze({
    widthPercent: Object.freeze({ label: '波形长度', unit: '%', min: 20, max: 100, step: 1, mode: 'wave' }),
    heightPx: Object.freeze({ label: '波形高度', unit: '像素', min: 80, max: 320, step: 1, mode: 'wave' }),
    sphereSizePx: Object.freeze({ label: '球形区域大小', unit: '像素', min: 80, max: 320, step: 1, mode: 'sphere' })
  });
  const presets = Object.freeze({
    fill: Object.freeze({ glass: false, layout: Object.freeze({ widthPercent: 100, heightPx: 150 }) }),
    medium: Object.freeze({ glass: false, layout: Object.freeze({ widthPercent: 60, heightPx: 150 }) }),
    mini: Object.freeze({ glass: false, layout: Object.freeze({ widthPercent: 30, heightPx: 100 }) }),
    sphere: Object.freeze({ glass: true, layout: Object.freeze({ sphereSizePx: 200 }) })
  });
  function normalize(input) {
    const value = input && typeof input === 'object' ? input : {}, result = { ...defaults };
    for (const [name, spec] of Object.entries(fields)) {
      const number = typeof value[name] === 'number' && Number.isFinite(value[name]) ? value[name] : defaults[name];
      result[name] = Math.max(spec.min, Math.min(spec.max, Math.round(number)));
    }
    result.align = ['left', 'center', 'right'].includes(value.align) ? value.align : defaults.align;
    return result;
  }
  return Object.freeze({ defaults, fields, presets, normalize });
});
