/* Application preferences only. The optical renderer remains the unchanged Sam Asante source. */
(function (scope) {
    'use strict';
    const groups = [
        { title: '面板透明度', basic: true, fields: [
            ['panelTransparency','音箱与内容面板',0,100,1,30,'%'],
            ['sidebarTransparency','侧栏面板',0,100,1,24,'%'],
            ['popoverTransparency','外观与设置浮层',0,100,1,18,'%'],
            ['chromeTransparency','导航与工具栏',0,100,1,32,'%'],
            ['panelBlur','内容背景模糊',0,64,1,20,'px'],
            ['popoverBlur','浮层背景模糊',0,64,1,28,'px']
        ] },
        { title: '圆角', basic: true, fields: [
            ['panelRadius','内容面板圆角',0,80,1,40,'px'],
            ['sidebarRadius','右侧卡片圆角',0,64,1,32,'px'],
            ['popoverRadius','外观与设置圆角',0,64,1,32,'px']
        ] },
        { title: '立体玻璃 · 深度与反光', basic: true, fields: [
            ['depth','玻璃深度',0,1,.01,.42,''],
            ['bend','边缘弯曲',0,2,.01,.78,''],
            ['bendWidth','边缘厚度',.01,.6,.001,.24,''],
            ['specular','反光程度',0,3,.01,.85,''],
            ['sheenWidth','高光厚度',0,16,.1,3,'px']
        ] },
        { title: '鼠标边缘光', basic: true, fields: [
            ['pointerLight','光效强度 · 0 为关闭',0,2,.05,.65,''],
            ['pointerReach','边缘感应范围',24,200,1,96,'px'],
            ['pointerHalo','光斑大小',32,280,1,140,'px'],
            ['pointerThickness','亮边厚度',.5,5,.25,1.5,'px']
        ] },
        { title: '折射', fields: [
            ['strength','折射强度',0,.3,.001,.025,''],
            ['curvature','玻璃曲率',0,1,.01,.22,''], ['dispersion','色彩分散',0,2,.01,.16,''],
            ['splay','角部展开',0,2,.01,0,'']
        ] },
        { title: '高光', fields: [
            ['sheen','高光强度',0,3,.01,.65,''], ['sheenAngle','光线角度',0,360,1,50,'°'],
            ['sheenFalloff','高光衰减',.1,5,.1,1.5,'']
        ] },
        { title: '背景光感', fields: [
            ['glow','辉光',0,2,.01,.1,''], ['glowSpread','辉光范围',.01,2,.01,.65,''],
            ['glowFalloff','辉光衰减',.1,5,.1,1.4,''], ['frost','霜化模糊',0,48,.5,3,'px'],
            ['saturate','饱和度',0,3,.01,1.25,''], ['brightness','明暗',-1,1,.01,0,'']
        ] }
    ];
    const fields = groups.flatMap(group => group.fields);
    const defaults = Object.freeze(Object.fromEntries(fields.map(field => [field[0], field[5]])));
    function normalize(input) {
        const source = input && typeof input === 'object' && !Array.isArray(input) ? input : {};
        return Object.fromEntries(fields.map(([key, _label, min, max, step, fallback]) => {
            const candidate = typeof source[key] === 'number' && Number.isFinite(source[key]) ? source[key] : fallback;
            const value = min + Math.round((Math.max(min, Math.min(max, candidate)) - min) / step) * step;
            return [key, Number(Math.max(min, Math.min(max, value)).toFixed(3))];
        }));
    }
    const clarityKey = ((scope.ShengyuAppearance?.prefix || 'shengyu-app-v1-') + 'clarity');
    const clarityValues = ['classic', 'more', 'clear'];
    const clarityScale = value => value === 'more' ? .72 : 1;
    function createClarityPreference(environment) {
        let value = 'classic';
        try {
            const saved = environment.localStorage.getItem(clarityKey);
            if (clarityValues.includes(saved)) value = saved;
        } catch (_) { /* No stored choice always means classic, on every machine. */ }
        return Object.freeze({
            get: () => value,
            set(next) {
                value = clarityValues.includes(next) ? next : 'classic';
                try { environment.localStorage.setItem(clarityKey, value); } catch (_) { /* Session choice remains usable. */ }
                return value;
            }
        });
    }
    function cssValues(input, clarity = 'classic') {
        const prefs = normalize(input);
        const scale = clarityScale(clarity);
        return {
            '--diy-panel-alpha': String((1 - prefs.panelTransparency / 100) * scale),
            '--diy-sidebar-alpha': String((1 - prefs.sidebarTransparency / 100) * scale),
            '--diy-popover-alpha': String((1 - prefs.popoverTransparency / 100) * scale),
            '--diy-chrome-alpha': String((1 - prefs.chromeTransparency / 100) * scale),
            '--diy-panel-blur': prefs.panelBlur + 'px', '--diy-popover-blur': prefs.popoverBlur + 'px',
            '--diy-chrome-blur': prefs.frost + 'px', '--diy-saturation': String(prefs.saturate),
            '--diy-panel-radius':prefs.panelRadius+'px','--diy-sidebar-radius':prefs.sidebarRadius+'px','--diy-popover-radius':prefs.popoverRadius+'px',
            '--pointer-halo': prefs.pointerHalo + 'px', '--pointer-thickness': prefs.pointerThickness + 'px'
        };
    }
    const api = { defaults, normalize, cssValues, createClarityPreference };
    if (typeof module === 'object' && module.exports) module.exports = api;
    if (!scope.document) return;
    const document = scope.document;
    const clarityPreference = createClarityPreference(scope);
    const clarityControls = [];
    const key = (scope.ShengyuAppearance?.prefix || 'shengyu-app-v1-') + 'glass-diy';
    let prefs = normalize({});
    try { prefs = normalize(JSON.parse(scope.localStorage.getItem(key))); } catch (_) { /* Defaults also work without storage. */ }
    const listeners = new Set();
    const controls = [];
    let saveTimer = 0;
    function persist() {
        if (saveTimer) scope.clearTimeout(saveTimer);
        saveTimer = 0;
        try { scope.localStorage.setItem(key, JSON.stringify(prefs)); } catch (_) { /* Session changes still apply. */ }
    }
    function apply() {
        const clarity = clarityPreference.get();
        document.documentElement.dataset.glassClarityPreset = clarity;
        for (const [name, value] of Object.entries(cssValues(prefs, clarity))) document.documentElement.style.setProperty(name, value);
        for (const button of clarityControls) {
            button.setAttribute('aria-pressed', String(button.dataset.glassClarityOption === clarity));
            button.style.removeProperty('color');
        }
        for (const { range, number, output, field } of controls) {
            const value = prefs[field[0]];
            range.value = number.value = String(value);
            output.textContent = value.toFixed(field[4] < .01 ? 3 : field[4] < 1 ? 2 : 0) + field[6];
        }
        for (const listener of listeners) listener();
    }
    function set(input) {
        prefs = normalize({ ...prefs, ...input });
        apply();
        if (saveTimer) scope.clearTimeout(saveTimer);
        saveTimer = scope.setTimeout(persist, 120);
        return { ...prefs };
    }
    function reset() {
        set(defaults);
        persist();
    }
    // Material buttons remain useful starting points; subsequent DIY values are never clamped to a preset.
    document.addEventListener('click', event => {
        const button = event.target.closest('[data-setting-group="material"] [data-value],#apMaterial [data-value]');
        if (!button) return;
        const presets = {
            clear: { chromeTransparency: 64, frost: 3, strength: .025, dispersion: .16 },
            regular: { chromeTransparency: 32, frost: 12, strength: .018, dispersion: .1 },
            tinted: { chromeTransparency: 24, frost: 15, strength: .015, dispersion: .08 }
        };
        if (presets[button.dataset.value]) set(presets[button.dataset.value]);
    });
    let instance = 0;
    function addGroup(parent, group) {
        const section = document.createElement('section');
        section.className = 'diy-group';
        const heading = document.createElement('h4');
        heading.textContent = group.title;
        section.append(heading);
        for (const field of group.fields) {
            const [name, label, min, max, step] = field;
            const row = document.createElement('div');
            row.className = 'diy-control';
            const id = 'diy-' + (++instance) + '-' + name;
            const caption = document.createElement('label');
            caption.htmlFor = id;
            caption.textContent = label;
            const output = document.createElement('output');
            output.htmlFor = id;
            const range = document.createElement('input');
            Object.assign(range, { type: 'range', min, max, step, id });
            range.dataset.glassSetting = name;
            const number = document.createElement('input');
            Object.assign(number, { type: 'number', min, max, step });
            number.setAttribute('aria-label', label + '数值');
            range.addEventListener('input', () => set({ [name]: Number(range.value) }));
            number.addEventListener('change', () => {
                if (!number.value.trim() || !Number.isFinite(number.valueAsNumber)) { apply(); return; }
                set({ [name]: number.valueAsNumber });
            });
            controls.push({ range, number, output, field });
            row.append(caption, output, range, number);
            section.append(row);
        }
        parent.append(section);
    }
    for (const host of document.querySelectorAll('[data-glass-customization]')) {
        const section = document.createElement('section');
        section.className = 'diy-group glass-clarity';
        section.setAttribute('data-glass-clarity-control', '');
        const heading = document.createElement('h4'); heading.textContent = '透感';
        const choices = document.createElement('div');
        choices.className = 'glass-clarity-choices';
        choices.setAttribute('role', 'group'); choices.setAttribute('aria-label', '透感');
        for (const [value, label] of [['classic','经典'],['more','更透'],['clear','清晰']]) {
            const button = document.createElement('button');
            button.type = 'button'; button.textContent = label; button.dataset.glassClarityOption = value;
            button.addEventListener('click', () => { clarityPreference.set(value); apply(); });
            clarityControls.push(button); choices.append(button);
        }
        const description = document.createElement('p');
        description.className = 'diy-note';
        description.textContent = '经典保留柔和透感，更透让背景更通透，清晰增强文字衬底。仅保存在这台机器，声屿与界面演示分开保存。';
        section.append(heading, choices, description); host.append(section);
        for (const group of groups.filter(group => group.basic)) addGroup(host, group);
        const note = document.createElement('p');
        note.className = 'diy-note';
        note.textContent = '透明度只改变底色。深度调节折射向内的范围；边缘厚度调节玻璃轮廓，高光厚度只改变亮边。鼠标光效仅影响外观，不会改变声音设置。';
        host.append(note);
        const details = document.createElement('details');
        details.className = 'diy-advanced';
        const summary = document.createElement('summary');
        summary.textContent = '高级玻璃参数 · 折射与光感';
        details.append(summary);
        const opticalNote = document.createElement('p');
        opticalNote.className = 'diy-note';
        opticalNote.textContent = '原作者的折射、边缘、高光与背景参数均保留。工具栏和浮层使用原版折射；大面板保持文字清楚。鼠标临时改变光线方向，移开后恢复这里的角度。';
        details.append(opticalNote);
        for (const group of groups.filter(group => !group.basic)) addGroup(details, group);
        const actions = document.createElement('div');
        actions.className = 'diy-actions';
        const shuffle = document.createElement('button');
        shuffle.type = 'button'; shuffle.textContent = '随机光感';
        shuffle.addEventListener('click', () => set({
            strength: .015 + Math.random() * .055, depth: .25 + Math.random() * .5,
            curvature: Math.random() * .4, dispersion: Math.random() * .45,
            sheenAngle: Math.random() * 360, sheen: .3 + Math.random() * .7,
            glow: Math.random() * .2
        }));
        const restore = document.createElement('button');
        restore.type = 'button'; restore.textContent = '恢复玻璃默认';
        restore.addEventListener('click', reset);
        actions.append(shuffle, restore);
        host.append(details, actions);
    }
    scope.addEventListener('pagehide', () => { persist(); listeners.clear(); }, { once:true });
    scope.ShengyuGlassSettings = Object.freeze({ ...api, get: () => ({ ...prefs }), set, reset,
        getClarity: clarityPreference.get,
        setClarity(value) { clarityPreference.set(value); apply(); return clarityPreference.get(); },
        scaleAlpha(value) { return value * clarityScale(clarityPreference.get()); },
        subscribe(listener) { listeners.add(listener); return () => listeners.delete(listener); } });
    apply();
})(typeof window === 'undefined' ? globalThis : window);
