/* Appearance preferences never share keys with the source workbench or demo. */
(() => {
  'use strict';
  const preview = new URLSearchParams(location.search).get('demo') === '1';
  const prefix = preview ? 'shengyu-preview-v1-' : 'shengyu-app-v1-';
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(prefix + 'appearance')) || {}; } catch (_) { /* Storage is optional. */ }
  const prefs = {
    theme: ['light', 'dark', 'system'].includes(saved.theme) ? saved.theme : 'system',
    ambient: saved.ambient !== false,
    motion: saved.motion !== false,
    transparency: saved.transparency === 'reduce' ? 'reduce' : 'normal'
  };
  const root = document.documentElement;
  root.dataset.theme = prefs.theme === 'system' ? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light') : prefs.theme;
  root.dataset.ambient = prefs.ambient ? 'on' : 'off';
  root.dataset.motionActive = prefs.motion ? 'on' : 'off';
  root.dataset.transparency = prefs.transparency;
  window.ShengyuAppearance = { prefix, preview, prefs };
})();
