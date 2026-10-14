/* Shengyu's offline view. The host is the authority for all real audio state. */
(() => {
  'use strict';
  const $ = selector => document.querySelector(selector);
  const $$ = selector => Array.from(document.querySelectorAll(selector));
  const root = document.documentElement;
  const getBridge = () => typeof window.chrome?.webview?.postMessage === 'function' ? window.chrome.webview : null;
  const appearance = window.ShengyuAppearance;
  const requestedDemo = appearance.preview;
  const roles = [
    ['FullRange', '完整音乐'], ['FrontLeft', '前方左声道'], ['FrontRight', '前方右声道'],
    ['Center', '前方共同内容'], ['SurroundLeft', '左侧后环绕'],
    ['SurroundRight', '右侧后环绕'], ['Bass', '低音辅助']
  ];
  const roleNames = Object.fromEntries(roles);
  const presetInfo = {
    whole: { title: '全屋同播', description: '每只参与的音箱播放完整音乐，不划分声道或频段。', roles: ['FullRange'] },
    stereo: { title: '左右立体声', description: '请明确指定前方左、右音箱，两边各至少选择一只。', roles: ['FrontLeft', 'FrontRight'] },
    surround: { title: '音乐环绕', description: '请分别指定前方或侧后角色。环绕以较轻的声音加入，保留前方的主体。', roles: ['FrontLeft', 'FrontRight', 'Center', 'SurroundLeft', 'SurroundRight', 'FullRange'] },
    bass: { title: '低音辅助', description: '仅选择你已确认具备低频能力的音箱。先以 100 赫兹为高频截止起点，再按实际听感微调。', roles: ['Bass'] }
  };
  const nodes = new Map(), assignments = new Map(), spatialNodes = new Map();
  const activeEdits = new WeakSet();
  let state = { demo: requestedDemo, sourceName: '', sourceId: '', masterVolume: 0, auditionVolume: 20, isSourceMuted: false, isCalibrating: false, isTesting: false, testingDeviceId: null, audioBusy: false, status: '', devices: [] };
  let received = false, hostReceived = false, page = 'play', preset = 'whole', toastTimer = 0, previousAppearanceFocus = null;
  let readyTimer = 0, readyAttempts = 0, readySent = false, viewReadySent = false, viewReadyPending = false;
  let currentDialog = null, noteEditor = null, toastDetails = '';
  const initialSpatial = () => ({ role: 'FullRange', lowCutHz: 0, highCutHz: 0, trimDb: 0 });
  const finite = (value, fallback = 0) => typeof value === 'number' && Number.isFinite(value) ? value : fallback;
  const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
  const canOperate = () => Boolean(getBridge() && received) || (!hostReceived && requestedDemo);
  const source = device => device.isSystemDefault || (state.sourceId && device.id === state.sourceId);
  const available = device => device.isAvailable === true;
  const forwarding = device => available(device) && !source(device) && device.isConnected;
  const calibrated = () => Boolean(state.isCalibrating);
  const busy = () => Boolean(state.audioBusy || state.isTesting || calibrated());
  const testing = device => state.isTesting && state.testingDeviceId === device.id;
  const getDevice = id => state.devices.find(device => device.id === id);
  const sourceProfile = profile => ({
    role: roleNames[profile?.role] ? profile.role : 'FullRange',
    lowCutHz: finite(profile?.lowCutHz), highCutHz: finite(profile?.highCutHz), trimDb: finite(profile?.trimDb)
  });

  function toast(message, error = false, details = '') {
    clearTimeout(toastTimer);
    const element = $('#toast');
    $('#toast-message').textContent = String(message);
    $('#toast-title').hidden = !error;
    toastDetails = typeof details === 'string' ? details : '';
    $('#toast-details').hidden = !toastDetails.trim();
    element.dataset.error = String(error);
    element.hidden = false;
    toastTimer = setTimeout(() => { element.hidden = true; }, error ? 15000 : 5500);
  }
  function setRangePaint(input) {
    const min = Number(input.min), max = Number(input.max), value = Number(input.value);
    input.style.setProperty('--range-progress', (max > min ? clamp((value - min) / (max - min) * 100, 0, 100) : 0) + '%');
  }
  function setInput(input, value) {
    // Checkboxes always reflect host acknowledgement, including rejected starts.
    // A focused slider is protected only during an actual unfinished edit/drag.
    if (input.type === 'checkbox') input.checked = Boolean(value);
    else if (!activeEdits.has(input)) input.value = String(value);
    if (input.type === 'range') setRangePaint(input);
  }
  function trackEditing(input) {
    if (!['range', 'number'].includes(input.type)) return;
    input.addEventListener('input', () => activeEdits.add(input));
    input.addEventListener('change', () => activeEdits.delete(input));
    input.addEventListener('blur', () => { activeEdits.delete(input); render(); });
    input.addEventListener('pointercancel', () => { activeEdits.delete(input); render(); });
    input.addEventListener('keydown', event => {
      if (event.key === 'Escape') { activeEdits.delete(input); render(); input.blur(); }
      if (event.key === 'Enter' && input.type === 'number') input.blur();
    });
  }
  function auditionPercent() {
    for (const input of [$('#audition-volume-number'), $('#audition-volume')]) {
      if (activeEdits.has(input) && Number.isInteger(input.valueAsNumber) && input.valueAsNumber >= 1 && input.valueAsNumber <= 100)
        return input.valueAsNumber;
    }
    return state.auditionVolume;
  }
  function updateAuditionLabel(device, card) {
    const button = card.querySelector('[data-device-action=test]'), percent = auditionPercent();
    button.setAttribute('aria-label', (testing(device) ? '停止试听' : '试听') + ' · ' + device.name + ' · 试听音量 ' + percent + '%');
    button.title = (testing(device) ? '停止试听：' : '单独试听：') + device.name + ' · 试听音量 ' + percent + '%';
  }
  function syncAuditionControls() {
    const controls = [$('#audition-volume'), $('#audition-volume-number')], disabled = !canOperate() || busy();
    if (disabled) controls.forEach(input => activeEdits.delete(input));
    const percent = auditionPercent();
    for (const input of controls) { setInput(input, percent); input.disabled = disabled; }
    $('#audition-volume-warning').hidden = percent <= 50;
    for (const device of state.devices) { const card = nodes.get(device.id); if (card) updateAuditionLabel(device, card); }
  }
  function iconUse(element, name) { element.querySelector('use')?.setAttribute('href', '#i-' + name); }
  function notifySurfaces() { document.dispatchEvent(new CustomEvent('glasssurfacechange')); }
  function optionsFor(select, values, placeholder = false) {
    const old = select.value;
    select.replaceChildren();
    if (placeholder) { const option = document.createElement('option'); option.value = ''; option.textContent = '请选择位置'; select.append(option); }
    for (const value of values) { const option = document.createElement('option'); option.value = value; option.textContent = roleNames[value]; select.append(option); }
    if (values.includes(old)) select.value = old;
  }
  function openDialog(title, onClose) {
    currentDialog?.close();
    closeAppearance();
    currentDialog = window.ShengyuGlassPopover.open({ document, title, onClose: () => { currentDialog = null; onClose?.(); notifySurfaces(); } });
    return currentDialog;
  }
  function addParagraph(parent, text, className) {
    const paragraph = document.createElement('p'); paragraph.textContent = text;
    if (className) paragraph.className = className;
    parent.append(paragraph); return paragraph;
  }
  function showDeviceName(element, name) {
    // Never substitute aliases or shorten a host-provided endpoint name.
    element.textContent = name;
    element.title = name;
  }
  function showDeviceNote(element, device) {
    element.textContent = device.note;
    element.hidden = !device.note;
    element.title = device.note ? '设备备注：' + device.note : '';
  }
  function editDeviceNote(id) {
    const device = getDevice(id); if (!device || !canOperate()) return;
    const modal = openDialog(device.note ? '编辑设备备注' : '给这台设备加备注', () => {
      if (noteEditor?.id === id) { clearTimeout(noteEditor.timer); noteEditor = null; }
    });
    modal.body.classList.add('device-note-editor');
    addParagraph(modal.body, device.name, 'device-note-original').title = device.name;
    addParagraph(modal.body, '原设备名保留。备注只跟随这台设备，例如“窗边左侧”或“电视旁”。', 'caption');
    const identity = document.createElement('details'), identityTitle = document.createElement('summary');
    identity.className = 'device-note-identity'; identityTitle.textContent = '查看设备编号，区分同名设备'; identity.append(identityTitle);
    addParagraph(identity, device.id); modal.body.append(identity);
    const label = document.createElement('label'); label.className = 'device-note-label'; label.textContent = '备注'; label.htmlFor = 'device-note-input';
    const input = document.createElement('input'); input.id = 'device-note-input'; input.type = 'text'; input.maxLength = 80;
    input.value = device.note; input.placeholder = '例如：窗边左侧'; input.autocomplete = 'off'; input.setAttribute('aria-describedby', 'device-note-count device-note-status');
    const count = addParagraph(modal.body, '', 'caption device-note-count'); count.id = 'device-note-count';
    modal.body.insertBefore(label, count); modal.body.insertBefore(input, count);
    const status = addParagraph(modal.body, '留空保存即可移除备注。', 'caption'); status.id = 'device-note-status'; status.setAttribute('role', 'status');
    const footer = document.createElement('div'); footer.className = 'device-note-footer';
    const cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'secondary-button'; cancel.textContent = '取消'; cancel.addEventListener('click', () => modal.close());
    const save = document.createElement('button'); save.type = 'button'; save.className = 'primary-button'; save.id = 'device-note-save'; save.textContent = '保存备注';
    footer.append(cancel, save); modal.body.append(footer);
    const editor = { id, modal, input, save, status, requested: null, timer: 0 };
    noteEditor = editor;
    const updateCount = () => { count.textContent = input.value.length + ' / 80'; };
    input.addEventListener('input', updateCount); updateCount();
    function saveNote() {
      if (editor.requested !== null) return;
      if (!getDevice(id)) { status.textContent = '设备列表已更新，请关闭后重新选择设备。'; save.disabled = true; return; }
      const note = input.value.trim();
      if (note.length > 80) { status.textContent = '备注最多 80 个字，请缩短后保存。'; return; }
      editor.requested = note; save.disabled = true; input.disabled = true; status.textContent = '正在保存…';
      if (!send({ action: 'deviceNote', id, note })) {
        editor.requested = null; save.disabled = false; input.disabled = false; status.textContent = '未能发送，请稍后再试。'; return;
      }
      if (noteEditor !== editor) return; // Standalone demo acknowledges synchronously.
      editor.timer = setTimeout(() => {
        if (noteEditor !== editor) return;
        editor.requested = null; save.disabled = false; input.disabled = false;
        status.textContent = '尚未收到保存结果，请检查应用连接后重试。';
      }, 8000);
    }
    save.addEventListener('click', saveNote);
    input.addEventListener('keydown', event => { if (event.key === 'Enter' && !event.isComposing) { event.preventDefault(); saveNote(); } });
    requestAnimationFrame(() => { if (noteEditor === editor) input.focus({ preventScroll: true }); });
    notifySurfaces();
  }
  function syncNoteEditor() {
    if (!noteEditor) return;
    const device = getDevice(noteEditor.id);
    if (noteEditor.requested !== null && device?.note === noteEditor.requested) { noteEditor.modal.close(); return; }
    if (!device) {
      clearTimeout(noteEditor.timer); noteEditor.save.disabled = true;
      noteEditor.status.textContent = '设备列表已更新，请关闭后重新选择设备。';
    }
  }
  function showGuestPicker() {
    if (!canOperate()) { toast('这个网页尚未连接声屿应用，不能打开真实访客分享。'); return; }
    const modal = openDialog('选择访客分享的音箱');
    addParagraph(modal.body, '选择一个设备，打开它的访客分享入口。界面演示仅展示示例说明。');
    const select = document.createElement('select'); select.setAttribute('aria-label', '访客分享设备');
    for (const device of state.devices.filter(available)) {
      const option = document.createElement('option'); option.value = device.id; option.textContent = device.name + (device.note ? ' · 备注：' + device.note : ''); option.title = option.textContent; select.append(option);
    }
    modal.body.append(select);
    const selectedName = addParagraph(modal.body, '', 'guest-selected-name');
    const showSelection = () => { const device = getDevice(select.value); showDeviceName(selectedName, device ? device.name + (device.note ? '\n备注：' + device.note : '') : '尚无已连接设备'); };
    select.addEventListener('change', showSelection); showSelection();
    const action = document.createElement('button'); action.type = 'button'; action.className = 'primary-button'; action.textContent = '打开访客分享';
    action.disabled = !select.options.length;
    action.addEventListener('click', () => { const id = select.value; modal.close(); send({ action: 'guest', id }); });
    modal.body.append(action);
  }
  function send(message) {
    if (!canOperate()) { toast('网页尚未连接声屿应用。设备操作需在程序中使用。'); render(); return false; }
    if (busy() && ['device', 'spatial', 'spatialBatch', 'master', 'mute', 'auditionVolume', 'testDevice', 'setSource', 'calibrate'].includes(message.action)) {
      toast(state.isTesting ? '正在试听，请先停止试听后再调整。' : calibrated() ? '正在进行声音校准，请等校准结束后再调整。' : '声音设备正在准备中，请稍候再调整。', true);
      render(); return false;
    }
    const device = message.id ? getDevice(message.id) : null;
    const requiresDevice = ['testDevice', 'setSource', 'spatial'].includes(message.action) || (message.action === 'device' && message.field !== 'isFavorite' && !(message.field === 'isConnected' && message.value === false));
    if (requiresDevice && (!device || !available(device))) {
      toast('Windows 显示这台设备尚未连接，请连接后再操作。', true); render(); return false;
    }
    // Native --ui-preview owns its own isolated demonstration state. Exercise the
    // real allowlisted bridge there; only a standalone browser simulates locally.
    const bridge = getBridge();
    if (!bridge && !hostReceived && requestedDemo) { simulate(message); return true; }
    try { bridge.postMessage(message); return true; }
    catch (_) { toast('请求未能送达应用，请重新打开界面。', true); render(); return false; }
  }
  function simulate(message) {
    const device = getDevice(message.id);
    switch (message.action) {
      case 'master':
        state.masterVolume = clamp(message.value, 0, 100);
        state.devices.filter(output => available(output) && (source(output) || output.isConnected)).forEach(output => { output.volume = state.masterVolume; });
        break;
      case 'mute': state.isSourceMuted = Boolean(message.value); break;
      case 'auditionVolume': state.auditionVolume = Math.round(clamp(finite(message.value, 20), 1, 100)); break;
      case 'deviceNote':
        if (!device) return;
        device.note = String(message.note || '').trim().slice(0, 80);
        toast('演示备注已更新，仅影响本次演示。');
        break;
      case 'testDevice':
        if (!device) return;
        state.isTesting = true; state.testingDeviceId = device.id; state.audioBusy = true;
        toast('演示：正在展示「' + device.name + '」的试听状态，没有播放声音。');
        break;
      case 'stopDeviceTest':
        state.isTesting = false; state.testingDeviceId = null; state.audioBusy = false;
        toast('演示试听已停止。');
        break;
      case 'setSource':
        if (!device || source(device)) return;
        state.devices.forEach(output => { output.isSystemDefault = output.id === device.id; });
        state.sourceId = device.id; state.sourceName = device.name;
        state.masterVolume = device.volume;
        toast('演示：已将「' + device.name + '」显示为主音源，没有修改 Windows 设置。');
        break;
      case 'device':
        if (!device) return;
        if (source(device) && message.field !== 'volume' && message.field !== 'isFavorite') { toast('主音源直接由 Windows 播放，不能在转发链中处理。', true); return; }
        device[message.field] = message.value;
        if (message.field === 'volume' && source(device)) state.masterVolume = message.value;
        break;
      case 'spatial': if (device && !source(device)) device.spatial = sourceProfile(message); break;
      case 'spatialBatch':
        for (const item of message.items) {
          const output = getDevice(item.id);
          if (output && forwarding(output)) output.spatial = sourceProfile(item);
        }
        toast('演示：已应用到所选音箱，真实设备未被操作。'); break;
      case 'guest': {
        const modal = openDialog('访客分享 · 界面演示');
        addParagraph(modal.body, '已选择：' + (device?.name || '示例音箱'));
        addParagraph(modal.body, '真实程序会在这里提供访客访问地址、验证码与二维码。本次演示没有启动分享服务。'); return;
      }
      case 'calibrate': {
        const modal = openDialog('声音校准 · 界面演示');
        addParagraph(modal.body, '实际校准会使用麦克风测量各音箱的到达时间，再补偿延迟。请先摆好设备，在真实程序的校准窗口中按提示开始。');
        addParagraph(modal.body, '本次演示没有播放声音、录音或修改设备延迟。'); return;
      }
      case 'refresh': toast('演示设备已刷新。没有枚举或连接真实音箱。'); break;
      case 'update': toast('界面演示不会检查或安装更新。'); return;
      case 'legacy': toast('界面演示没有原生窗口；实际程序可返回原版界面。'); return;
      case 'close': case 'exit': case 'minimize': case 'maximize': toast('界面演示不会控制应用窗口。'); return;
      default: return;
    }
    render();
  }

  function receive(incoming, fromHost = true) {
    if (!incoming || typeof incoming !== 'object' || !Array.isArray(incoming.devices)) return;
    if (fromHost) hostReceived = true;
    received = true;
    state = {
      demo: requestedDemo || incoming.demo === true,
      sourceName: typeof incoming.sourceName === 'string' ? incoming.sourceName : '',
      sourceId: typeof incoming.sourceId === 'string' ? incoming.sourceId : '',
      masterVolume: clamp(finite(incoming.masterVolume), 0, 100),
      auditionVolume: Math.round(clamp(finite(incoming.auditionVolume, 20), 1, 100)),
      isSourceMuted: incoming.isSourceMuted === true,
      isCalibrating: incoming.isCalibrating === true,
      isTesting: incoming.isTesting === true,
      testingDeviceId: typeof incoming.testingDeviceId === 'string' ? incoming.testingDeviceId : null,
      audioBusy: incoming.audioBusy === true,
      status: typeof incoming.status === 'string' ? incoming.status : '',
      devices: incoming.devices.filter(device => device && typeof device.id === 'string').map(device => ({
        id: device.id, name: String(device.name || '未命名音箱'), kind: String(device.kind || '音频输出'),
        isAvailable: device.isAvailable === true, note: typeof device.note === 'string' ? device.note.trim().slice(0, 80) : '',
        isConnected: device.isConnected === true, isSystemDefault: device.isSystemDefault === true,
        volume: clamp(finite(device.volume), 0, 100), latencyMs: clamp(finite(device.latencyMs), 0, 3000),
        bassBoost: clamp(finite(device.bassBoost), -12, 12), treble: clamp(finite(device.treble), -12, 12),
        isMono: device.isMono === true, isFavorite: device.isFavorite === true,
        syncStatusText: String(device.syncStatusText || ''), syncDetailsText: String(device.syncDetailsText || ''),
        spatial: sourceProfile(device.spatial)
      }))
    };
    render();
    if (fromHost || !getBridge()) window.ShengyuStartup?.ready(fromHost ? 'host-ready' : 'demo-ready');
    // The native startup page stays visible until real host data has rendered.
    // Two frames let layout and its first paint complete before the window appears.
    if (fromHost && !viewReadySent && !viewReadyPending) {
      viewReadyPending = true;
      requestAnimationFrame(() => requestAnimationFrame(() => {
        viewReadyPending = false;
        try { const bridge = getBridge(); if (bridge) { bridge.postMessage({ action: 'viewReady' }); viewReadySent = true; } } catch (_) { /* Retry on the next host state. */ }
      }));
    }
  }
  function makeDevice(device) {
    const card = $('#device-template').content.firstElementChild.cloneNode(true);
    card.dataset.deviceId = device.id;
    const volume = card.querySelector('[data-field=volume]');
    const delay = card.querySelector('[data-field=latencyMs]');
    const delayLabel = card.querySelector('.delay-control>label');
    const identifier = 'device-delay-' + (nodes.size + 1) + '-' + Math.random().toString(36).slice(2, 7);
    delay.id = identifier; delayLabel.htmlFor = identifier;
    volume.setAttribute('aria-label', device.name + '音量');
    card.querySelector('[data-field=isConnected]').setAttribute('aria-label', '开启' + device.name + '转发');
    for (const input of card.querySelectorAll('[data-field]')) {
      trackEditing(input);
      const field = input.dataset.field;
      if (field === 'bassBoost' || field === 'treble') input.setAttribute('aria-label', device.name + (field === 'bassBoost' ? '低音增益' : '高音增益'));
      if (field === 'isMono') input.setAttribute('aria-label', device.name + '合并为单声道');
      input.addEventListener('input', () => {
        if (input.type === 'range') {
          setRangePaint(input);
          const output = input.closest('.volume-control,.eq-field').querySelector('output');
          output.textContent = input.value + (field === 'volume' ? '%' : ' dB');
        }
      });
      input.addEventListener('change', () => {
        const value = input.type === 'checkbox' ? input.checked : input.valueAsNumber;
        if (typeof value === 'number' && (!Number.isFinite(value) || !input.checkValidity())) {
          toast('请输入允许范围内的数值。延迟可精确到 0.1 毫秒。', true); input.reportValidity(); return;
        }
        send({ action: 'device', id: device.id, field, value });
      });
    }
    card.querySelector('.favorite-button').addEventListener('click', () => {
      const current = getDevice(device.id);
      if (current) send({ action: 'device', id: device.id, field: 'isFavorite', value: !current.isFavorite });
    });
    card.querySelector('[data-device-action=note]').addEventListener('click', () => editDeviceNote(device.id));
    card.querySelector('[data-device-action=test]').addEventListener('click', () => {
      const current = getDevice(device.id);
      if (current) send(testing(current) ? { action: 'stopDeviceTest' } : { action: 'testDevice', id: current.id });
    });
    card.querySelector('[data-device-action=set-source]').addEventListener('click', () => {
      const current = getDevice(device.id);
      if (current && !source(current)) send({ action: 'setSource', id: current.id });
    });
    card.querySelectorAll('[data-step]').forEach(button => button.addEventListener('click', () => {
      const current = getDevice(device.id); if (!current || source(current)) return;
      const value = Math.round(clamp(finite(Number(delay.value), current.latencyMs) + Number(button.dataset.step), 0, 3000) * 10) / 10;
      delay.value = value.toFixed(1);
      send({ action: 'device', id: device.id, field: 'latencyMs', value });
    }));
    const eqButton = card.querySelector('.eq-button'), eq = card.querySelector('.eq-controls');
    eqButton.addEventListener('click', () => {
      eq.hidden = !eq.hidden; eqButton.setAttribute('aria-expanded', String(!eq.hidden)); notifySurfaces();
    });
    $('#device-list').append(card); nodes.set(device.id, card); return card;
  }
  function updateDevice(device, card) {
    const isSource = source(device), isTesting = testing(device), isAvailable = available(device), disabled = !canOperate() || busy();
    card.dataset.connected = String(isAvailable && (device.isConnected || isSource)); card.dataset.source = String(isSource);
    card.dataset.available = String(isAvailable);
    card.dataset.testing = String(isTesting);
    showDeviceName(card.querySelector('.device-name'), device.name);
    showDeviceNote(card.querySelector('.device-note-badge'), device);
    card.querySelector('.source-chip').hidden = !isSource;
    card.querySelector('.device-subtitle').textContent = !isAvailable
      ? device.kind + ' · Windows 未连接' + (device.isConnected && !isSource ? ' · 已保留转发选择' : '')
      : isSource ? 'Windows 主音源 · 已连接 · 原声直出' : device.kind + ' · Windows 已连接 · ' + (device.isConnected ? '转发已开启' : '转发未开启');
    iconUse(card.querySelector('.device-icon'), isSource ? 'monitor' : 'speaker');
    const favorite = card.querySelector('.favorite-button'); favorite.setAttribute('aria-pressed', String(device.isFavorite));
    favorite.setAttribute('aria-label', (device.isFavorite ? '取消收藏' : '收藏') + device.name); favorite.disabled = disabled;
    card.querySelector('.device-connection').hidden = isSource;
    for (const input of card.querySelectorAll('[data-field]')) {
      const field = input.dataset.field;
      input.setAttribute('aria-label', device.name + ' · ' + ({ isConnected: '开启转发', volume: '音量', latencyMs: '延迟，毫秒', bassBoost: '低音增益', treble: '高音增益', isMono: '合并为单声道' }[field] || field));
      const unavailable = !isAvailable && !(field === 'isConnected' && device.isConnected);
      if (disabled || unavailable) activeEdits.delete(input);
      setInput(input, field === 'latencyMs' ? device[field].toFixed(1) : device[field]);
      input.disabled = disabled || unavailable || (isSource && field !== 'volume');
      if (field === 'isConnected' && !isAvailable && device.isConnected) input.setAttribute('aria-label', device.name + ' · 取消已保留的转发选择');
      if (input.type === 'range') {
        const output = input.closest('.volume-control,.eq-field').querySelector('output');
        output.textContent = input.value + (field === 'volume' ? '%' : ' dB');
      }
    }
    card.querySelectorAll('[data-step]').forEach(button => { button.disabled = disabled || isSource || !isAvailable; });
    card.querySelector('.delay-control').hidden = isSource;
    card.querySelector('.device-controls').style.gridTemplateColumns = isSource ? '1fr' : '';
    card.querySelector('.eq-button').disabled = disabled || isSource || !isAvailable;
    card.querySelector('.eq-button').hidden = isSource;
    if (isSource) card.querySelector('.eq-controls').hidden = true;
    const testButton = card.querySelector('[data-device-action=test]');
    testButton.querySelector('span').textContent = isTesting ? '停止试听' : '试听';
    testButton.setAttribute('aria-pressed', String(isTesting));
    updateAuditionLabel(device, card);
    testButton.disabled = !canOperate() || (!isTesting && (busy() || !isAvailable));
    iconUse(testButton, isTesting ? 'stop' : 'play');
    const sourceButton = card.querySelector('[data-device-action=set-source]');
    sourceButton.hidden = isSource;
    sourceButton.disabled = disabled || !isAvailable;
    sourceButton.setAttribute('aria-label', '将' + device.name + '设为主音源');
    sourceButton.title = '将「' + device.name + '」设为 Windows 默认播放设备';
    const noteButton = card.querySelector('[data-device-action=note]');
    noteButton.querySelector('span').textContent = device.note ? '编辑备注' : '加备注';
    noteButton.disabled = !canOperate();
    noteButton.setAttribute('aria-label', (device.note ? '编辑备注：' : '添加备注：') + device.name);
    const sync = card.querySelector('.device-sync');
    sync.textContent = isTesting ? (requestedDemo || state.demo ? '演示试听 · 没有发声' : '正在试听 · 可随时停止') : !isAvailable ? '连接恢复后，设备会自动归组' : isSource ? '原声由 Windows 直接播放' : device.syncStatusText || (device.isConnected ? '建议先完成声音同步' : '开启转发后加入播放');
    sync.title = isTesting || !isAvailable ? sync.textContent : device.syncDetailsText;
  }
  function makeAssignment(device) {
    const row = $('#preset-device-template').content.firstElementChild.cloneNode(true);
    row.dataset.deviceId = device.id;
    const check = row.querySelector('input'), position = row.querySelector('.preset-position select'), capability = row.querySelector('.preset-capability select');
    const assignment = { row, check, position, capability };
    const info = presetInfo[preset]; optionsFor(position, info.roles, info.roles.length > 1);
    [check, position, capability].forEach(input => input.addEventListener('change', updatePresetHint));
    $('#preset-devices').append(row); assignments.set(device.id, assignment); return assignment;
  }
  function updateAssignments() {
    const eligible = state.devices.filter(device => !source(device));
    for (const [id, assignment] of assignments) if (!eligible.some(device => device.id === id)) { assignment.row.remove(); assignments.delete(id); }
    for (const device of eligible) {
      const assignment = assignments.get(device.id) || makeAssignment(device);
      showDeviceName(assignment.row.querySelector('.preset-device-name'), device.name);
      showDeviceNote(assignment.row.querySelector('.device-note-badge'), device);
      assignment.row.querySelector('.preset-device-state').textContent = !available(device) ? 'Windows 未连接，暂不能参与' : device.isConnected ? 'Windows 已连接 · 转发已开启' : '已连接，请先在播放页开启转发';
      const disabled = !forwarding(device) || !canOperate() || busy();
      assignment.row.setAttribute('aria-disabled', String(disabled));
      assignment.check.disabled = disabled;
      // Temporary audio work locks editing without erasing a chosen preset.
      if (!forwarding(device) || !canOperate()) assignment.check.checked = false;
      assignment.position.disabled = disabled;
      assignment.capability.disabled = disabled;
      assignment.check.setAttribute('aria-label', '选择' + device.name + '参与预设');
      assignment.position.setAttribute('aria-label', device.name + '的声音角色');
      assignment.capability.setAttribute('aria-label', device.name + '的设备能力');
    }
    let note = $('#preset-source-note');
    if (!note) { note = document.createElement('p'); note.id = 'preset-source-note'; note.className = 'preset-source-note'; $('#preset-devices').prepend(note); }
    note.textContent = state.sourceName ? '主音源「' + state.sourceName + '」保持原声直出，不参与分工。' : '主音源保持原声直出，不参与分工。';
    note.title = state.sourceName;
    if (!eligible.length) note.textContent += '连接其他音箱后，可以在这里安排声音角色。';
    updatePresetHint();
  }
  function presetValidation() {
    const chosen = Array.from(assignments.entries()).filter(([, assignment]) => assignment.check.checked && !assignment.check.disabled);
    if (!chosen.length) return { error: '请勾选已开启转发的音箱。', chosen };
    if (chosen.some(([, assignment]) => !assignment.position.value)) return { error: '请为每只所选音箱指定位置。', chosen };
    if (chosen.some(([, assignment]) => assignment.capability.value === 'unknown')) return { error: '请确认每只所选音箱的能力，声屿不会根据型号猜测。', chosen };
    if (preset === 'stereo') {
      const selectedRoles = chosen.map(([, assignment]) => assignment.position.value);
      if (!selectedRoles.includes('FrontLeft') || !selectedRoles.includes('FrontRight')) return { error: '左右立体声需要至少一只左音箱和一只右音箱。', chosen };
    }
    if (preset === 'bass' && chosen.some(([, assignment]) => assignment.capability.value !== 'bass')) return { error: '低音辅助只适用于已确认具有低频能力的音箱。', chosen };
    if (preset === 'surround' && !chosen.some(([, assignment]) => ['SurroundLeft', 'SurroundRight'].includes(assignment.position.value))) return { error: '音乐环绕请至少指定一只侧后环绕音箱。', chosen };
    return { chosen };
  }
  function updatePresetHint() {
    if (busy()) {
      $('#preset-hint').textContent = state.isTesting ? '试听结束后可以继续调整分工，已勾选的音箱会保留。' : '声音设备准备完成后可以继续调整分工，已勾选的音箱会保留。';
      $('#apply-preset').disabled = true;
      return;
    }
    const validation = presetValidation();
    const count = validation.chosen.length;
    $('#preset-hint').textContent = validation.error || ('将更新 ' + count + ' 只音箱的分工。音量、开关与延迟保持原值。');
    $('#apply-preset').disabled = Boolean(validation.error) || !canOperate() || busy();
  }
  function selectPreset(value) {
    if (!presetInfo[value]) return;
    preset = value;
    for (const button of $$('[data-preset]')) {
      const selected = button.dataset.preset === value;
      button.setAttribute('aria-pressed', String(selected)); button.classList.toggle('selected', selected);
    }
    $('#preset-title').textContent = presetInfo[value].title;
    $('#preset-description').textContent = presetInfo[value].description;
    for (const assignment of assignments.values()) {
      const available = presetInfo[value].roles;
      optionsFor(assignment.position, available, available.length > 1);
      // A new listening use must be reviewed explicitly, even if a role overlaps.
      assignment.check.checked = false;
      assignment.position.value = available.length === 1 ? available[0] : '';
    }
    updatePresetHint(); notifySurfaces();
  }
  function applyPreset() {
    const validation = presetValidation(); if (validation.error) { toast(validation.error, true); return; }
    const items = validation.chosen.map(([id, assignment]) => {
      const role = assignment.position.value, rear = role.startsWith('Surround');
      return { id, role, lowCutHz: (preset === 'surround' && assignment.capability.value === 'small') ? 100 : 0,
        highCutHz: preset === 'bass' ? 100 : 0, trimDb: rear ? -6 : preset === 'bass' ? -3 : 0 };
    });
    send({ action: 'spatialBatch', items });
  }
  function makeSpatial(device) {
    const card = $('#spatial-template').content.firstElementChild.cloneNode(true);
    card.dataset.deviceId = device.id;
    optionsFor(card.querySelector('[data-spatial=role]'), roles.map(([role]) => role));
    card.querySelector('.apply-spatial').addEventListener('click', () => {
      const current = getDevice(device.id); if (!current || source(current)) return;
      const controls = Array.from(card.querySelectorAll('[data-spatial]'));
      const failed = controls.find(input => !input.checkValidity() || (input.type === 'number' && !Number.isFinite(input.valueAsNumber)));
      if (failed) { failed.reportValidity(); toast('请检查声音分工的数值范围。', true); return; }
      const profile = Object.fromEntries(controls.map(input => [input.dataset.spatial, input.type === 'number' ? input.valueAsNumber : input.value]));
      if (profile.lowCutHz && profile.highCutHz && profile.lowCutHz >= profile.highCutHz) { toast('低频截止需要小于高频截止，或填 0 表示不限。', true); return; }
      if (send({ action: 'spatial', id: device.id, ...profile })) {
        card.dataset.dirty = 'false';
        if (requestedDemo && !hostReceived && !getBridge()) toast('演示：已应用声音分工。没有修改真实设备。');
        else toast('分工请求已发送，应用将返回实际设置。');
      }
    });
    card.querySelectorAll('[data-spatial]').forEach(input => input.addEventListener('input', () => { card.dataset.dirty = 'true'; }));
    $('#spatial-devices').append(card); spatialNodes.set(device.id, card); return card;
  }
  function updateSpatial(device, card) {
    const isSource = source(device), disabled = !forwarding(device) || !canOperate() || busy();
    card.dataset.source = String(isSource);
    showDeviceName(card.querySelector('h3'), device.name);
    showDeviceNote(card.querySelector('.device-note-badge'), device);
    card.querySelector('.spatial-heading .caption').textContent = !available(device) ? device.kind + ' · Windows 未连接' : isSource ? '主音源 · 原声直出' : device.kind + ' · ' + roleNames[device.spatial.role];
    const controls = Array.from(card.querySelectorAll('[data-spatial]'));
    if (card.dataset.dirty !== 'true' && !card.contains(document.activeElement)) {
      for (const input of controls) setInput(input, device.spatial[input.dataset.spatial]);
    }
    controls.forEach(input => { input.disabled = disabled; input.setAttribute('aria-label', device.name + ' ' + (input.closest('label').querySelector('span')?.textContent || input.dataset.spatial)); });
    card.querySelector('.apply-spatial').disabled = disabled;
    card.querySelector('.spatial-notice').textContent = !available(device) ? 'Windows 当前未连接这台设备，声音设置已保留。连接后再调整。' : isSource
      ? '这路声音由 Windows 直接播放，无法在声屿的转发处理中改变声音角色、频段或衰减。'
      : !device.isConnected ? '这台设备已连接，请先在播放页开启转发，再应用声音分工。'
      : '赫兹（Hz）表示频率；分贝（dB）表示声音增减。数值只是起点，实际音色请试听确认。';
  }
  function render() {
    const demo = requestedDemo || state.demo;
    $('#mode-badge').hidden = !demo;
    const note = $('#connection-note');
    note.dataset.mode = demo ? 'demo' : getBridge() && hostReceived ? 'connected' : 'disconnected';
    note.textContent = demo ? '界面演示 · 使用示例音箱，所有调整仅影响演示数据' : getBridge() && hostReceived ? '已连接声屿应用 · 音箱状态由程序实时提供' : '独立网页尚未连接应用 · 设备操作不可用，可通过 ?demo=1 查看演示';
    showDeviceName($('#source-name'), state.sourceName || (received ? '尚未找到主音源' : '等待连接应用'));
    $('.source-tag').textContent = state.devices.some(device => source(device) && available(device)) ? '原声直出' : '等待连接';
    $('#device-count').textContent = state.devices.filter(device => available(device) && (source(device) || device.isConnected)).length;
    $('#active-outputs').textContent = state.devices.filter(forwarding).length + ' 路转发';
    $('#app-status').textContent = demo ? (state.isTesting ? '界面演示 · 正在模拟试听，没有播放声音' : '界面演示 · 没有播放或录制声音') : state.status || (received ? '应用已连接' : '等待应用连接');
    if (busy()) activeEdits.delete($('#master-volume'));
    $('#master-value').textContent = canOperate() ? (activeEdits.has($('#master-volume')) ? $('#master-volume').value : Math.round(state.masterVolume)) : '—';
    setInput($('#master-volume'), state.masterVolume); $('#master-volume').disabled = !canOperate() || busy();
    $('#master-mute').disabled = !canOperate() || busy();
    $('#master-mute').setAttribute('aria-pressed', String(state.isSourceMuted));
    $('#master-mute').setAttribute('aria-label', state.isSourceMuted ? '取消主音源静音' : '静音主音源');
    iconUse($('#master-mute'), state.isSourceMuted ? 'mute' : 'volume');
    syncAuditionControls();
    for (const [id, card] of nodes) if (!getDevice(id)) { card.remove(); nodes.delete(id); }
    for (const [id, card] of spatialNodes) if (!getDevice(id)) { card.remove(); spatialNodes.delete(id); }
    for (const device of state.devices) {
      updateDevice(device, nodes.get(device.id) || makeDevice(device));
      updateSpatial(device, spatialNodes.get(device.id) || makeSpatial(device));
    }
    groupPlaybackDevices();
    $('#devices-empty').hidden = state.devices.length > 0;
    updateAssignments();
    syncNoteEditor();
    $$('[data-action]').forEach(button => { button.disabled = !canOperate() || (busy() && button.dataset.action === 'calibrate'); });
    $$('[data-preset]').forEach(button => { button.disabled = busy(); });
    notifySurfaces();
  }

  function groupPlaybackDevices() {
    const group = $('#inactive-devices'), availableGroup = $('#available-devices'), focus = document.activeElement;
    const focusedCard = focus?.closest('.device-card');
    const primary = state.devices.filter(device => source(device) && available(device));
    const active = state.devices.filter(forwarding);
    const unused = state.devices.filter(device => available(device) && !source(device) && !device.isConnected);
    const inactive = state.devices.filter(device => !available(device));
    // Keep card instances and only move a node when its position changes. Routine
    // host refreshes must not interrupt keyboard focus or unfinished input edits.
    for (const [container, devices] of [[$('#device-list'), [...primary, ...active]], [$('#available-device-list'), unused], [$('#inactive-device-list'), inactive]]) {
      let cursor = container.firstElementChild;
      for (const device of devices) {
        const card = nodes.get(device.id);
        if (card !== cursor) container.insertBefore(card, cursor);
        cursor = card.nextElementSibling;
      }
    }
    group.hidden = inactive.length === 0;
    availableGroup.hidden = unused.length === 0;
    $('#available-device-count').textContent = unused.length;
    $('#inactive-device-count').textContent = inactive.length;
    $('#active-devices-empty').hidden = primary.length + active.length > 0 || state.devices.length === 0;
    $('#active-devices-empty').textContent = unused.length ? '还没有正在使用的音箱。可以从下方已连接的设备中开启转发。' : '当前没有正在使用的音箱。请先在 Windows 中连接设备。';
    if (focusedCard && focus.isConnected) {
      if (!group.open && $('#inactive-device-list').contains(focusedCard)) $('#inactive-devices-summary').focus({ preventScroll: true });
      else if (document.activeElement !== focus) focus.focus({ preventScroll: true });
    } else if ((group.hidden && group.contains(focus)) || (availableGroup.hidden && availableGroup.contains(focus))) {
      $('#devices-heading').setAttribute('tabindex', '-1');
      $('#devices-heading').focus({ preventScroll: true });
    }
  }

  function navigate(target, updateHash = true) {
    if (!['play', 'space', 'guide', 'appearance'].includes(target)) return;
    page = target;
    for (const section of $$('.page')) section.hidden = section.id !== 'page-' + target;
    for (const button of $$('[data-page]')) {
      const selected = button.dataset.page === target;
      button.classList.toggle('selected', selected);
      if (selected) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
    }
    if (updateHash && location.hash !== '#' + target) history.replaceState(null, '', '#' + target);
    window.scrollTo({ top: 0, behavior: 'instant' });
    notifySurfaces();
  }
  function applyAppearance() {
    const prefs = appearance.prefs;
    root.dataset.theme = prefs.theme === 'system' ? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light') : prefs.theme;
    root.dataset.ambient = prefs.ambient ? 'on' : 'off';
    root.dataset.motionActive = prefs.motion ? 'on' : 'off';
    root.dataset.transparency = prefs.transparency;
    $('#ambient-toggle').checked = prefs.ambient;
    $('#motion-toggle').checked = prefs.motion;
    $('#transparency-toggle').checked = prefs.transparency === 'reduce';
    $$('[data-theme-choice]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.themeChoice === prefs.theme)));
    try { localStorage.setItem(appearance.prefix + 'appearance', JSON.stringify(prefs)); } catch (_) { /* Session choices work without storage. */ }
    notifySurfaces();
  }
  function syncClarity() {
    const clarity = window.ShengyuGlassSettings.getClarity();
    $$('[data-clarity]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.clarity === clarity)));
    $$('input[type=range]').forEach(setRangePaint);
  }
  function openAppearance(trigger) {
    const popover = $('#appearance-popover');
    if (!popover.hidden) { closeAppearance(); return; }
    previousAppearanceFocus = trigger || document.activeElement;
    popover.hidden = false;
    $$('.quick-appearance').forEach(button => button.setAttribute('aria-expanded', 'true'));
    $('#close-appearance').focus({ preventScroll: true }); notifySurfaces();
  }
  function closeAppearance() {
    const popover = $('#appearance-popover'); if (popover.hidden) return;
    popover.hidden = true;
    $$('.quick-appearance').forEach(button => button.setAttribute('aria-expanded', 'false'));
    if (previousAppearanceFocus?.isConnected) previousAppearanceFocus.focus({ preventScroll: true });
    previousAppearanceFocus = null; notifySurfaces();
  }
  function hostMessage(message) {
    if (!message || typeof message !== 'object') return;
    if (message.error && noteEditor && noteEditor.requested !== null) {
      clearTimeout(noteEditor.timer); noteEditor.requested = null;
      noteEditor.save.disabled = false; noteEditor.input.disabled = false;
      noteEditor.status.textContent = String(message.message || '未能保存，请稍后重试。');
    }
    if (message.link || message.pin || message.qr) {
      const modal = openDialog('访客分享');
      if (message.message) addParagraph(modal.body, String(message.message));
      if (message.link) addParagraph(modal.body, '访问地址：' + String(message.link), 'guest-detail');
      if (message.pin) addParagraph(modal.body, '验证码：' + String(message.pin), 'guest-detail');
      if (message.link) {
        const copy = document.createElement('button'); copy.type = 'button'; copy.className = 'secondary-button'; copy.textContent = '复制访客访问资料';
        copy.addEventListener('click', async () => {
          try {
            await navigator.clipboard.writeText(String(message.link) + (message.pin ? '\n验证码：' + String(message.pin) : ''));
            toast('访客访问资料已复制。');
          } catch (_) { toast('未能自动复制，请选中访问地址后按 Ctrl+C。', true); }
        });
        modal.body.append(copy);
      }
      // Only host-supplied inline image data can become an image. Never navigate or fetch.
      if (typeof message.qr === 'string' && /^data:image\/(png|jpeg|webp);base64,[A-Za-z0-9+/=]+$/.test(message.qr) && message.qr.length < 2000000) {
        const image = document.createElement('img'); image.src = message.qr; image.alt = '访客分享二维码'; modal.body.append(image);
      }
      addParagraph(modal.body, '将访问地址和验证码提供给你允许连接的访客。');
    } else if (typeof message.message === 'string') toast(message.message, Boolean(message.error), message.details);
  }

  $$('[data-page]').forEach(button => button.addEventListener('click', () => navigate(button.dataset.page)));
  $$('[data-go]').forEach(button => button.addEventListener('click', () => navigate(button.dataset.go)));
  $('.brand').addEventListener('click', event => { event.preventDefault(); navigate('play'); });
  $$('[data-action]').forEach(button => button.addEventListener('click', () => {
    if (button.dataset.action === 'guest') showGuestPicker();
    else send({ action: button.dataset.action });
  }));
  $$('[data-preset]').forEach(button => button.addEventListener('click', () => selectPreset(button.dataset.preset)));
  $('#apply-preset').addEventListener('click', applyPreset);
  $('#toast-close').addEventListener('click', () => { clearTimeout(toastTimer); $('#toast').hidden = true; });
  for (const event of ['pointerenter', 'focusin']) $('#toast').addEventListener(event, () => clearTimeout(toastTimer));
  $('#toast-details').addEventListener('click', () => {
    const details = toastDetails, summary = $('#toast-message').textContent;
    if (!details) return;
    clearTimeout(toastTimer); $('#toast').hidden = true;
    const modal = openDialog('操作详情');
    addParagraph(modal.body, summary);
    addParagraph(modal.body, '下面保留完整原因与诊断信息。可以选中文字，或点击复制后发给需要排查的人。', 'caption');
    const text = document.createElement('textarea'); text.className = 'notice-details'; text.readOnly = true; text.value = details;
    text.setAttribute('aria-label', '完整诊断信息，只读'); modal.body.append(text);
    const copy = document.createElement('button'); copy.type = 'button'; copy.className = 'primary-button'; copy.textContent = '复制完整详情';
    const copyStatus = addParagraph(modal.body, '', 'caption'); copyStatus.setAttribute('role', 'status');
    copy.addEventListener('click', async () => {
      try { await navigator.clipboard.writeText(details); copyStatus.textContent = '已复制完整详情。'; }
      catch { copyStatus.textContent = '暂时无法自动复制。请选中上方文字后手动复制。'; }
    });
    modal.body.append(copy); notifySurfaces();
  });
  $('#inactive-devices').addEventListener('toggle', () => {
    $('#inactive-devices .inactive-devices-hint').textContent = $('#inactive-devices').open ? '收起' : '展开查看';
    notifySurfaces();
  });
  trackEditing($('#master-volume'));
  $('#master-volume').addEventListener('input', event => { $('#master-value').textContent = event.target.value; setRangePaint(event.target); });
  $('#master-volume').addEventListener('change', event => send({ action: 'master', value: event.target.valueAsNumber }));
  $('#master-mute').addEventListener('click', () => send({ action: 'mute', value: !state.isSourceMuted }));
  for (const input of [$('#audition-volume'), $('#audition-volume-number')]) {
    trackEditing(input);
    input.addEventListener('input', syncAuditionControls);
    input.addEventListener('change', () => {
      if (!Number.isInteger(input.valueAsNumber) || !input.checkValidity()) {
        toast('试听音量请输入 1–100 之间的整数。', true); render(); return;
      }
      send({ action: 'auditionVolume', value: input.valueAsNumber });
    });
  }
  $$('[data-theme-choice]').forEach(button => button.addEventListener('click', () => { appearance.prefs.theme = button.dataset.themeChoice; applyAppearance(); }));
  $('#ambient-toggle').addEventListener('change', event => { appearance.prefs.ambient = event.target.checked; applyAppearance(); });
  $('#motion-toggle').addEventListener('change', event => { appearance.prefs.motion = event.target.checked; applyAppearance(); });
  $('#transparency-toggle').addEventListener('change', event => { appearance.prefs.transparency = event.target.checked ? 'reduce' : 'normal'; applyAppearance(); });
  $$('[data-clarity]').forEach(button => button.addEventListener('click', () => window.ShengyuGlassSettings.setClarity(button.dataset.clarity)));
  $$('.quick-appearance').forEach(button => button.addEventListener('click', () => openAppearance(button)));
  $('#close-appearance').addEventListener('click', closeAppearance);
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && !$('#appearance-popover').hidden) { event.preventDefault(); closeAppearance(); } });
  $('#show-licenses').addEventListener('click', () => {
    const modal = openDialog('开源组件与署名');
    addParagraph(modal.body, '声屿基于 PairUp，由 Jamim Mehdi 创作的原项目及其中文增强版本发展而来。原项目许可证与署名随程序保留。');
    addParagraph(modal.body, '玻璃效果使用 Sam Asante 的 liquid-glass 0.1.1 原始折射组件（固定提交 4e7b769e1df7e5a7d3669fef22417fe3d2f79ade），并随离线页面包含 React、React DOM 与 Scheduler。');
    addParagraph(modal.body, '完整许可文本位于程序 Web/glass 目录：LICENSE-samasante.txt、LICENSE-react.txt、LICENSE-react-dom.txt、LICENSE-scheduler.txt。SOURCE-NOTICE.md 记录来源与适配范围。');
    addParagraph(modal.body, '彩色波形使用 David Haz 的 React Bits Strands，固定提交 496968c4117e21278f13917201b7d615bc5e7450。它采用 MIT + Commons Clause，完整许可位于 Web/strands/vendor/LICENSE-react-bits.md；允许作为应用的一部分使用，组件本身的单独销售、再许可和再分发受到限制。');
    addParagraph(modal.body, '波形所需 React、React DOM、Scheduler 与 OGL 均随程序本地包含；对应许可与适配说明位于 Web/strands。原始波形绘制算法和着色器保留。');
  });
  window.addEventListener('hashchange', () => navigate(location.hash.slice(1), false));
  const colorScheme = matchMedia('(prefers-color-scheme: dark)');
  colorScheme.addEventListener('change', applyAppearance);
  const unsubscribeAppearance = window.ShengyuGlassSettings.subscribe(syncClarity);
  window.addEventListener('pagehide', () => { clearTimeout(toastTimer); clearTimeout(readyTimer); unsubscribeAppearance(); colorScheme.removeEventListener('change', applyAppearance); currentDialog?.close(); }, { once: true });
  window.shengyuReceive = receive;
  window.shengyuMessage = hostMessage;
  window.ShengyuUI = Object.freeze({
    getState: () => structuredClone(state),
    inspect: () => ({ page, preset, demo: requestedDemo || state.demo, received, hostReceived, bridgeLive: Boolean(getBridge()), canOperate: canOperate(), audioBusy: busy(),
      selected: Array.from(assignments).filter(([, entry]) => entry.check.checked).map(([id]) => id),
      storagePrefix: appearance.prefix, deviceCount: nodes.size, diyCount: $$('[data-glass-setting]').length,
      activeGlass: $$('[data-glass-optical][data-liquid-glass]').length })
  });
  applyAppearance(); syncClarity(); navigate(location.hash.slice(1) || 'play', false);
  if (requestedDemo) receive({ demo: true, sourceName: '扬声器 (Realtek(R) Audio)', sourceId: 'demo-source', masterVolume: 64, status: '界面演示', devices: [
    { id: 'demo-source', name: '扬声器 (Realtek(R) Audio)', note: '桌面主音箱', kind: '有线音频', isAvailable: true, isSystemDefault: true, isConnected: true, volume: 64, latencyMs: 0, isFavorite: true, spatial: initialSpatial() },
    { id: 'demo-left', name: 'JBL CHARGE 5 Stereo', note: '窗边左侧', kind: '蓝牙音频', isAvailable: true, isConnected: true, volume: 52, latencyMs: 126.5, isFavorite: true, syncStatusText: '演示延迟 · 未实测', spatial: initialSpatial() },
    { id: 'demo-right', name: '扬声器 (2- USB Audio Device)', note: '沙发旁', kind: '有线音频', isAvailable: true, isConnected: true, volume: 45, latencyMs: 83.2, syncStatusText: '演示延迟 · 未实测', spatial: initialSpatial() },
    { id: 'demo-extra', name: '耳机 (WH-1000XM4 Stereo)', note: '备用耳机', kind: '蓝牙音频', isAvailable: true, isConnected: false, volume: 38, latencyMs: 0, syncStatusText: '转发未开启', spatial: initialSpatial() },
    { id: 'demo-offline', name: '扬声器 (3- USB Audio Device)', note: '电视旁', kind: '有线音频', isAvailable: false, isConnected: true, volume: 35, latencyMs: 42.5, spatial: initialSpatial() }
  ] }, false); else render();
  if (!getBridge()) window.ShengyuStartup?.ready('standalone-ready');
  function ready() {
    if (readySent) return;
    const bridge = getBridge();
    if (bridge) { try { bridge.postMessage({ action: 'ready' }); readySent = true; render(); return; } catch (_) { /* Retry only the read-only handshake. */ } }
    if (++readyAttempts < 20) readyTimer = setTimeout(ready, 250);
  }
  ready();
})();
