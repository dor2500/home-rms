// Home RMS web console (Central Hub). Plain ES2020, no dependencies, no inline handlers.
// XSS safety: every value from the API is rendered with textContent / DOM properties; HTML strings are never
// used with data. URLs are built with encodeURIComponent. State-changing calls carry the CSRF header.
'use strict';

(() => {
  const CSRF = 'X-HomeRms-Request';
  const app = document.getElementById('app');
  const toasts = document.getElementById('toasts');

  const state = {
    authed: false,
    overview: null,
    endpoints: [],
    cleanups: [],
    epFilter: { text: '', chip: 'all' },
    shellHistory: [],
    deploy: { selected: new Map(), targets: new Set(), jobId: null, opts: { silent: true, machineScope: false, upgrade: true } },
  };

  // ---------------- DOM helpers ----------------
  function h(tag, props, ...kids) {
    const el = document.createElement(tag);
    if (props) {
      for (const [k, v] of Object.entries(props)) {
        if (v === undefined || v === null || v === false) continue;
        if (k === 'class') el.className = v;
        else if (k === 'text') el.textContent = String(v);
        else if (k === 'style') Object.assign(el.style, v);
        else if (k === 'dataset') Object.assign(el.dataset, v);
        else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
        else if (k in el && typeof v !== 'string') el[k] = v;
        else el.setAttribute(k, v === true ? '' : String(v));
      }
    }
    append(el, kids);
    return el;
  }
  function append(el, kids) {
    for (const k of kids.flat(Infinity)) {
      if (k === null || k === undefined || k === false) continue;
      el.appendChild(k instanceof Node ? k : document.createTextNode(String(k)));
    }
    return el;
  }
  function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); return el; }

  const ICONS = {
    home: 'M3 11l9-8 9 8v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z',
    server: 'M4 4h16v6H4zM4 14h16v6H4zM8 7h.01M8 17h.01',
    deploy: 'M12 3v12m0 0l-4-4m4 4l4-4M5 21h14',
    power: 'M12 3v9M6.3 6.3a8 8 0 1 0 11.4 0',
    restart: 'M4 12a8 8 0 1 0 2.3-5.7L4 8.6M4 4v4.6h4.6',
    sleep: 'M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z',
    lock: 'M6 11h12v9H6zM8 11V8a4 4 0 0 1 8 0v3',
    wake: 'M12 2v4M12 18v4M4.9 4.9l2.8 2.8M16.3 16.3l2.8 2.8M2 12h4M18 12h4M4.9 19.1l2.8-2.8M16.3 7.7l2.8-2.8',
    logoff: 'M15 4h4v16h-4M10 8l-4 4 4 4M6 12h10',
    cancel: 'M6 6l12 12M18 6L6 18',
    refresh: 'M20 11a8 8 0 0 0-14.9-3M4 4v4h4M4 13a8 8 0 0 0 14.9 3M20 20v-4h-4',
    folder: 'M3 6h6l2 2h10v11H3z',
    file: 'M6 3h8l4 4v14H6zM14 3v4h4',
    up: 'M12 19V5M5 12l7-7 7 7',
    upload: 'M12 16V4m0 0L8 8m4-4l4 4M5 20h14',
    download: 'M12 4v12m0 0l-4-4m4 4l4-4M5 20h14',
    search: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM21 21l-5-5',
    signout: 'M10 4H5v16h5M15 8l4 4-4 4M19 12H9',
    play: 'M7 5l12 7-12 7z',
  };
  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('fill', 'none');
    svg.setAttribute('stroke', 'currentColor');
    svg.setAttribute('stroke-width', '1.8');
    svg.setAttribute('stroke-linecap', 'round');
    svg.setAttribute('stroke-linejoin', 'round');
    svg.setAttribute('aria-hidden', 'true');
    const p = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    p.setAttribute('d', ICONS[name] || '');
    svg.appendChild(p);
    return svg;
  }
  function btn(label, onclick, opts = {}) {
    return h('button', { type: 'button', class: 'btn ' + (opts.cls || ''), onclick, disabled: opts.disabled, title: opts.title },
      opts.icon ? icon(opts.icon) : null, label ? h('span', { text: label }) : null);
  }

  // ---------------- Formatting ----------------
  function fmtBytes(b) {
    b = Number(b) || 0;
    if (b >= 1 << 30) return (b / (1 << 30)).toFixed(1) + ' GB';
    if (b >= 1 << 20) return (b / (1 << 20)).toFixed(1) + ' MB';
    if (b >= 1 << 10) return Math.round(b / 1024) + ' KB';
    return b + ' B';
  }
  function fmtRate(b) { return fmtBytes(b) + '/s'; }
  function fmtAgo(iso) {
    if (!iso) return 'never';
    const t = new Date(iso).getTime();
    if (!t) return 'never';
    const s = Math.max(0, (Date.now() - t) / 1000);
    if (s < 60) return 'just now';
    if (s < 3600) return Math.floor(s / 60) + ' min ago';
    if (s < 86400) return Math.floor(s / 3600) + ' h ago';
    return new Date(t).toLocaleString();
  }
  function fmtUptime(sec) {
    sec = Number(sec) || 0;
    if (!sec) return '--';
    const d = Math.floor(sec / 86400), hh = Math.floor((sec % 86400) / 3600), m = Math.floor((sec % 3600) / 60);
    return d ? `${d}d ${hh}h` : hh ? `${hh}h ${m}m` : `${m}m`;
  }
  function fmtDate(ms) { return ms ? new Date(Number(ms)).toLocaleString() : ''; }

  // ---------------- Toasts / dialogs ----------------
  function toast(text, kind = '') {
    const t = h('div', { class: 'toast ' + kind, text });
    toasts.appendChild(t);
    setTimeout(() => t.remove(), kind === 'err' ? 7000 : 4000);
  }
  function dialog({ title, message, confirm = 'OK', danger = false, input = null, cancel = 'Cancel' }) {
    return new Promise(resolve => {
      let field = null;
      const close = v => { back.remove(); document.removeEventListener('keydown', onKey); resolve(v); };
      const ok = () => close(input !== null ? field.value : true);
      const onKey = e => { if (e.key === 'Escape') close(null); if (e.key === 'Enter' && input !== null) ok(); };
      if (input !== null) field = h('input', { class: 'input', value: input, 'aria-label': title });
      const back = h('div', { class: 'backdrop', onclick: e => { if (e.target === back) close(null); } },
        h('div', { class: 'panel dialog', role: 'dialog', 'aria-modal': 'true' },
          h('h3', { text: title }),
          message ? h('p', { text: message }) : null,
          field,
          h('div', { class: 'row' },
            btn(cancel, () => close(null)),
            btn(confirm, ok, { cls: danger ? 'danger' : 'primary' }))));
      document.body.appendChild(back);
      document.addEventListener('keydown', onKey);
      (field || back.querySelector('.btn.primary, .btn.danger')).focus();
      if (field) field.select();
    });
  }

  // ---------------- API ----------------
  class ApiError extends Error {
    constructor(message, status, code) { super(message); this.status = status; this.code = code; }
  }
  async function api(path, { method = 'GET', body } = {}) {
    const opt = { method, headers: {}, credentials: 'same-origin', cache: 'no-store' };
    if (method !== 'GET') opt.headers[CSRF] = '1';
    if (body !== undefined) { opt.headers['Content-Type'] = 'application/json'; opt.body = JSON.stringify(body); }
    let r;
    try { r = await fetch(path, opt); }
    catch { throw new ApiError('The hub is not reachable.', 0, 'network'); }
    if (r.status === 401) { onSignedOut(); throw new ApiError('Sign-in required.', 401, 'auth'); }
    let data = null;
    try { data = await r.json(); } catch { /* not JSON */ }
    if (!r.ok || (data && data.ok === false)) throw new ApiError((data && data.error) || `Request failed (HTTP ${r.status}).`, r.status, data && data.code);
    return data;
  }
  const ep = id => '/api/v2/endpoints/' + encodeURIComponent(id);

  // ---------------- Lifecycle ----------------
  function every(ms, fn) {
    // A view that is left while it was still loading must not keep polling: stop when the route changes.
    const owner = location.hash;
    const t = setInterval(() => {
      if (location.hash !== owner) { clearInterval(t); return; }
      if (!document.hidden) fn();
    }, ms);
    state.cleanups.push(() => clearInterval(t));
  }
  function runCleanups() { for (const c of state.cleanups.splice(0)) { try { c(); } catch { /* ignore */ } } }
  function onSignedOut() {
    if (!state.authed) return;
    state.authed = false;
    runCleanups();
    renderSignIn();
  }

  // ---------------- Sign-in ----------------
  async function boot() {
    try {
      const r = await fetch('/api/auth/status', { cache: 'no-store' });
      const s = await r.json();
      state.authed = !!s.authenticated;
    } catch { state.authed = false; }
    if (state.authed) route(); else renderSignIn();
  }

  function renderSignIn(message) {
    runCleanups();
    const code = h('input', { class: 'input', type: 'password', autocomplete: 'off', placeholder: 'Access key or 2FA code', 'aria-label': 'Access key or 2FA code' });
    const remember = h('input', { type: 'checkbox', checked: true });
    const err = h('div', { class: 'error-box hidden' });
    const submit = async () => {
      err.classList.add('hidden');
      try {
        const r = await fetch('/api/auth/verify', {
          method: 'POST', headers: { 'Content-Type': 'application/json', [CSRF]: '1' },
          body: JSON.stringify({ code: code.value, remember: remember.checked }),
        });
        const b = await r.json().catch(() => ({}));
        if (!r.ok) throw new Error(b.error || 'Sign-in failed.');
        state.authed = true;
        code.value = '';
        route();
      } catch (e) {
        err.textContent = e.message;
        err.classList.remove('hidden');
      }
    };
    code.addEventListener('keydown', e => { if (e.key === 'Enter') submit(); });
    clear(app).appendChild(h('div', { class: 'signin' },
      h('div', { class: 'panel' },
        h('div', { class: 'brand' }, h('div', { class: 'logo' }, icon('home')), h('div', null, 'Home RMS', h('small', { text: 'Web console' }))),
        h('h1', { text: 'Sign in' }),
        h('div', { class: 'muted', text: message || 'Enter the hub access key (Controller: About / Server & PKI) or a 6-digit 2FA or backup code.' }),
        code,
        h('label', { class: 'check' }, remember, 'Keep me signed in on this device'),
        err,
        btn('Sign in', submit, { cls: 'primary' }))));
    code.focus();
  }

  async function signOut() {
    try { await fetch('/api/auth/logout', { method: 'POST', headers: { [CSRF]: '1' } }); } catch { /* ignore */ }
    state.authed = false;
    renderSignIn('You are signed out.');
  }

  // ---------------- Layout & routing ----------------
  function parseRoute() {
    const parts = (location.hash || '#/overview').replace(/^#\/?/, '').split('/').map(decodeURIComponent);
    return { view: parts[0] || 'overview', id: parts[1], tab: parts[2] };
  }

  function layout(title, sub, actions) {
    const r = parseRoute();
    const nav = [['overview', 'Overview', 'home'], ['endpoints', 'Endpoints', 'server'], ['deploy', 'Deploy', 'deploy']];
    const navItem = ([v, label, ic]) => h('a', {
      class: 'item' + (r.view === v || (v === 'endpoints' && r.view === 'e') ? ' active' : ''), href: '#/' + v,
    }, icon(ic), label);
    const content = h('section', { class: 'content' });
    clear(app).appendChild(h('div', { class: 'shell' },
      h('aside', { class: 'nav' },
        h('div', { class: 'brand' }, h('div', { class: 'logo' }, icon('home')), h('div', null, 'Home RMS', h('small', { text: 'Web console' }))),
        nav.map(navItem),
        h('div', { class: 'foot' },
          h('div', { class: 'ellipsis', text: state.overview ? `Hub ${state.overview.hub.machine}:${state.overview.hub.port}` : 'Central Hub' }),
          h('div', { class: 'ellipsis faint mono', text: state.overview ? state.overview.operator : '' }),
          btn('Sign out', signOut, { icon: 'signout' }))),
      h('main', { class: 'main' },
        h('header', { class: 'topbar' },
          h('div', { class: 'grow' }, h('h1', { text: title }), sub ? h('div', { class: 'sub', text: sub }) : null),
          actions || null),
        content),
      h('nav', { class: 'tabbar' },
        nav.map(([v, label, ic]) => h('a', { href: '#/' + v, class: r.view === v || (v === 'endpoints' && r.view === 'e') ? 'active' : '' }, icon(ic), label)),
        h('a', { href: '#/overview', onclick: e => { e.preventDefault(); signOut(); } }, icon('signout'), 'Sign out'))));
    return content;
  }

  function route() {
    if (!state.authed) return renderSignIn();
    runCleanups();
    const r = parseRoute();
    if (r.view === 'endpoints') return viewEndpoints();
    if (r.view === 'e' && r.id) return viewEndpoint(r.id, r.tab || 'telemetry');
    if (r.view === 'deploy') return viewDeploy();
    return viewOverview();
  }
  window.addEventListener('hashchange', route);

  function errorBox(e) { return h('div', { class: 'error-box', text: e.message || String(e) }); }
  function meter(pct) {
    const v = Math.max(0, Math.min(100, Number(pct) || 0));
    return h('div', { class: 'meter' + (v >= 90 ? ' hot' : v >= 75 ? ' warm' : '') }, h('i', { style: { width: v + '%' } }));
  }
  function statusDot(e) { return h('span', { class: 'dot' + (e.connected ? ' conn' : e.online ? ' on' : ''), title: e.online ? 'Online' : 'Offline' }); }
  function osBadge(e) {
    return h('span', { class: 'badge ' + (e.osFamily === 'linux' ? 'warn' : e.osFamily === 'windows' ? 'accent' : ''), text: e.os || e.kind });
  }

  // ---------------- Power actions ----------------
  const POWER = {
    wake: { label: 'Wake', icon: 'wake', danger: false },
    restart: { label: 'Restart', icon: 'restart', danger: true },
    shutdown: { label: 'Shut down', icon: 'power', danger: true },
    sleep: { label: 'Sleep', icon: 'sleep', danger: true },
    lock: { label: 'Lock', icon: 'lock', danger: false },
    logoff: { label: 'Sign out user', icon: 'logoff', danger: true },
    cancel: { label: 'Cancel shutdown', icon: 'cancel', danger: false },
  };
  async function power(e, action) {
    const p = POWER[action];
    if (p.danger && !(await dialog({ title: `${p.label}: ${e.name}?`, message: `This sends "${p.label}" to ${e.name} (${e.address}) right away.`, confirm: p.label, danger: true })))
      return;
    try {
      const r = await api(ep(e.id) + '/power', { method: 'POST', body: { action } });
      toast(`${e.name}: ${r.message || p.label + ' sent.'}`, 'ok');
    } catch (err) { toast(`${e.name}: ${err.message}`, 'err'); }
  }
  function powerButtons(e, which, small = true) {
    return (e.caps.power || []).filter(a => which.includes(a)).map(a =>
      btn(small ? '' : POWER[a].label, () => power(e, a), { icon: POWER[a].icon, cls: (small ? 'small icon ' : '') + (POWER[a].danger ? 'danger' : ''), title: POWER[a].label }));
  }

  // ---------------- Overview ----------------
  async function viewOverview() {
    const content = layout('Overview', 'Fleet status, live load and alerts');
    const body = h('div', { class: 'stack' }, h('div', { class: 'muted', text: 'Loading…' }));
    content.appendChild(body);

    const render = async () => {
      let o;
      try { o = await api('/api/v2/overview'); state.overview = o; }
      catch (e) { clear(body).appendChild(errorBox(e)); return; }
      const stat = (label, value, extra) => h('div', { class: 'panel stat' }, h('div', { class: 'label', text: label }), h('div', { class: 'value' }, value), extra || null);
      const alerts = o.alerts.items;
      clear(body).append(
        h('div', { class: 'stats' },
          stat('Endpoints online', [String(o.endpoints.online), h('small', { text: ` / ${o.endpoints.total}` })]),
          stat('Offline', String(o.endpoints.offline)),
          stat('Agents', [String(o.endpoints.agents), h('small', { text: o.endpoints.linux ? ` (${o.endpoints.linux} Linux)` : '' })]),
          stat('Fleet CPU', [`${o.fleet.cpu}`, h('small', { text: '%' })], meter(o.fleet.cpu)),
          stat('Fleet RAM', [`${o.fleet.ram}`, h('small', { text: '%' })], meter(o.fleet.ram)),
          stat('Unread alerts', String(o.alerts.unread))),
        h('div', { class: 'cols' },
          h('div', { class: 'panel' },
            h('div', { class: 'head' }, h('h2', { class: 'grow', text: 'Alerts' }),
              alerts.length ? btn('Acknowledge all', async () => {
                try { await api('/api/v2/alerts/ack', { method: 'POST', body: {} }); render(); } catch (e) { toast(e.message, 'err'); }
              }, { cls: 'small' }) : null),
            h('div', { class: 'body flush' }, alerts.length === 0
              ? h('div', { class: 'empty', text: 'No alerts. All quiet.' })
              : alerts.map(a => h('div', { class: 'alert-item' + (a.acknowledged ? ' read' : '') },
                h('span', { class: 'badge ' + (a.severity === 'CRITICAL' ? 'err' : a.severity === 'WARNING' ? 'warn' : 'accent'), text: a.severity }),
                h('div', { class: 'grow' },
                  h('div', null, h('strong', { text: a.title }), h('span', { class: 'muted', text: ` · ${a.endpoint}` })),
                  h('div', { class: 'muted', text: a.detail })),
                h('span', { class: 'faint nowrap', text: fmtAgo(a.time) }))))),
          h('div', { class: 'panel' },
            h('div', { class: 'head' }, h('h2', { text: 'Busiest endpoints' })),
            h('div', { class: 'body flush' }, o.fleet.hottest.length === 0
              ? h('div', { class: 'empty', text: 'No live telemetry yet. Agents stream CPU / RAM while they are online and paired.' })
              : h('div', { class: 'result-list' }, o.fleet.hottest.map(x => h('div', { class: 'item' },
                h('a', { class: 'grow ellipsis', href: `#/e/${encodeURIComponent(x.id)}/telemetry`, text: x.name }),
                h('span', { class: 'mono muted nowrap', text: `CPU ${x.cpu}% · RAM ${x.ram}%` }))))))));
    };
    await render();
    every(5000, render);
  }

  // ---------------- Endpoints ----------------
  async function loadEndpoints() {
    const r = await api('/api/v2/endpoints');
    state.endpoints = r.endpoints;
    return r.endpoints;
  }

  async function viewEndpoints() {
    const scan = btn('Scan / probe', async () => {
      try { await api('/api/v2/endpoints-scan', { method: 'POST', body: {} }); toast('Probing the fleet…', 'ok'); setTimeout(render, 2500); }
      catch (e) { toast(e.message, 'err'); }
    }, { icon: 'refresh' });
    const content = layout('Endpoints', 'Saved endpoints of this Controller', scan);
    const filter = h('input', { class: 'input grow', placeholder: 'Filter by name, address or OS…', value: state.epFilter.text, 'aria-label': 'Filter endpoints' });
    const chips = h('div', { class: 'chips' });
    const grid = h('div', { class: 'grid' });
    content.append(h('div', { class: 'row' }, filter, chips), grid);

    const CHIPS = [['all', 'All'], ['online', 'Online'], ['windows', 'Windows'], ['linux', 'Linux'], ['other', 'Other']];
    const drawChips = () => {
      clear(chips).append(...CHIPS.map(([k, label]) => h('button', {
        type: 'button', class: 'chip' + (state.epFilter.chip === k ? ' on' : ''), text: label,
        onclick: () => { state.epFilter.chip = k; drawChips(); draw(); },
      })));
    };
    const match = e => {
      const c = state.epFilter.chip;
      if (c === 'online' && !e.online) return false;
      if ((c === 'windows' || c === 'linux' || c === 'other') && e.osFamily !== c) return false;
      const q = state.epFilter.text.trim().toLowerCase();
      return !q || [e.name, e.address, e.os, e.mode].some(v => String(v || '').toLowerCase().includes(q));
    };
    const card = e => h('div', { class: 'panel ep' },
      h('div', { class: 'title' }, statusDot(e),
        h('a', { class: 'grow ellipsis', href: `#/e/${encodeURIComponent(e.id)}/${e.isAgent ? 'telemetry' : 'shell'}`, text: e.name, title: e.name }),
        osBadge(e)),
      h('div', { class: 'meta' },
        h('span', { class: 'mono ellipsis', text: e.address }),
        h('span', { text: `${e.mode} · ${e.online ? (e.connected ? 'connected' : 'online') : 'offline'} · seen ${fmtAgo(e.lastSeen)}` })),
      e.isAgent ? h('div', { class: 'bars' },
        h('div', null, `CPU ${e.online ? e.cpu + '%' : '--'}`, meter(e.online ? e.cpu : 0)),
        h('div', null, `RAM ${e.online ? e.ram + '%' : '--'}`, meter(e.online ? e.ram : 0))) : null,
      h('div', { class: 'actions' },
        powerButtons(e, ['wake', 'restart', 'shutdown', 'sleep', 'lock'], false).map(b => { b.classList.add('small'); return b; }),
        e.caps.power.length === 0 ? h('span', { class: 'faint', text: 'No power actions for this endpoint type' }) : null));
    const draw = () => {
      const list = state.endpoints.filter(match);
      clear(grid).append(...(list.length ? list.map(card)
        : [h('div', { class: 'panel empty', text: state.endpoints.length ? 'No endpoint matches the filter.' : 'No endpoints saved yet. Add them in the Controller or run Scan LAN there.' })]));
    };
    const render = async () => {
      try { await loadEndpoints(); draw(); }
      catch (e) { clear(grid).appendChild(errorBox(e)); }
    };
    filter.addEventListener('input', () => { state.epFilter.text = filter.value; draw(); });
    drawChips();
    await render();
    every(5000, render);
  }

  // ---------------- Endpoint detail ----------------
  const TABS = [
    ['telemetry', 'Overview', c => c.telemetry],
    ['shell', 'Shell', c => c.shell],
    ['files', 'Files', c => c.files],
    ['services', 'Services', c => c.services],
    ['tasks', 'Scheduled tasks', c => c.tasks],
    ['inventory', 'Processes & apps', c => c.inventory],
    ['screen', 'Screen', c => c.screen],
  ];

  async function viewEndpoint(id, tab) {
    let e;
    try { e = (await api(ep(id))).endpoint; }
    catch (err) { layout('Endpoint', '').appendChild(errorBox(err)); return; }
    const tabs = TABS.filter(([, , cap]) => cap(e.caps));
    if (!tabs.some(([t]) => t === tab) && tabs.length) tab = tabs[0][0];

    const actions = h('div', { class: 'row' },
      powerButtons(e, ['wake', 'restart', 'shutdown', 'sleep', 'lock', 'logoff', 'cancel'], false).map(b => { b.classList.add('small'); return b; }),
      e.caps.upgradeAll ? btn('Update all apps', () => upgradeAll(e), { cls: 'small', icon: 'deploy' }) : null);
    const content = layout(e.name, `${e.address} · ${e.os} · ${e.mode} · ${e.online ? 'online' : 'offline'}`, actions);
    content.appendChild(h('div', { class: 'tabs' }, tabs.map(([t, label]) =>
      h('a', { href: `#/e/${encodeURIComponent(e.id)}/${t}`, class: t === tab ? 'active' : '', text: label }))));
    const body = h('div', { class: 'stack' });
    content.appendChild(body);
    if (!e.online) body.appendChild(h('div', { class: 'note', text: 'This endpoint looks offline; requests may time out.' }));
    const views = { telemetry: tabTelemetry, shell: tabShell, files: tabFiles, services: tabServices, tasks: tabTasks, inventory: tabInventory, screen: tabScreen };
    if (views[tab]) await views[tab](e, body);
    else body.appendChild(h('div', { class: 'panel empty', text: 'Nothing to manage on this endpoint type from the web console.' }));
  }

  async function upgradeAll(e) {
    // TODO(UpgradeAll): becomes live when the Controller wires HubConsoleServices.UpgradeAll (caps.upgradeAll).
    if (!(await dialog({ title: `Update all apps on ${e.name}?`, message: 'Runs "winget upgrade --all" on the endpoint.', confirm: 'Update all' }))) return;
    try { const r = await api(ep(e.id) + '/upgrade-all', { method: 'POST', body: {} }); toast(r.message || 'Update All finished.', 'ok'); }
    catch (err) { toast(err.message, 'err'); }
  }

  // ---- telemetry ----
  function sparkline(values, max) {
    const ns = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(ns, 'svg');
    svg.setAttribute('viewBox', '0 0 100 40');
    svg.setAttribute('preserveAspectRatio', 'none');
    const vals = (values || []).slice(-120);
    if (vals.length > 1) {
      const top = max || Math.max(1, ...vals);
      const pts = vals.map((v, i) => `${(i / (vals.length - 1)) * 100},${40 - Math.min(1, v / top) * 38 - 1}`);
      const area = document.createElementNS(ns, 'path');
      area.setAttribute('d', `M0,40 L${pts.join(' L')} L100,40 Z`);
      const line = document.createElementNS(ns, 'polyline');
      line.setAttribute('points', pts.join(' '));
      svg.append(area, line);
    }
    return svg;
  }

  async function tabTelemetry(e, body) {
    const sparks = h('div', { class: 'spark-grid' });
    const info = h('div', { class: 'panel' }, h('div', { class: 'head' }, h('h2', { text: 'Agent' })), h('div', { class: 'body muted', text: 'Loading…' }));
    const status = h('div', { class: 'muted' });
    body.append(status, sparks, info);
    const card = (label, value, series, max) => h('div', { class: 'panel spark' },
      h('div', { class: 'muted', text: label }), h('div', { class: 'v', text: value }), sparkline(series, max));
    const render = async () => {
      try {
        const t = await api(ep(e.id) + '/telemetry');
        if (!t.live && !(t.cpu && t.cpu.length)) {
          status.textContent = t.status || 'No live telemetry.';
          clear(sparks);
          return;
        }
        const last = a => (a && a.length ? a[a.length - 1] : 0);
        status.textContent = `${t.status || 'Live'} · uptime ${fmtUptime(t.uptime)} · ${t.processes || '--'} processes · refreshed ${new Date().toLocaleTimeString()}`;
        clear(sparks).append(
          card('CPU', `${last(t.cpu).toFixed(0)}%`, t.cpu, 100),
          card('Memory', `${last(t.ram).toFixed(0)}%`, t.ram, 100),
          card('Disk (fullest)', `${last(t.disk).toFixed(0)}%`, t.disk, 100),
          card('Network', `${fmtRate(t.rx)} in · ${fmtRate(t.tx)} out`, t.net, 0));
      } catch (err) { status.textContent = err.message; }
    };
    await render();
    every(3000, render);
    try {
      const i = await api(ep(e.id) + '/info');
      clear(info.lastChild).classList.remove('muted');
      info.lastChild.appendChild(h('dl', { class: 'kv' },
        [['Machine', i.machineName], ['User', i.user], ['OS', i.osVersion], ['Agent', i.agentVersion], ['Screen', `${i.screen}${i.monitors > 1 ? ` (${i.monitors} monitors)` : ''}`]]
          .map(([k, v]) => [h('dt', { text: k }), h('dd', { text: v || '--' })])));
    } catch (err) { clear(info.lastChild).appendChild(errorBox(err)); }
  }

  // ---- shell ----
  const QUICK = {
    PowerShell: ['hostname', 'Get-ComputerInfo | Select-Object CsName, OsName, OsVersion, OsUptime', 'Get-Process | Sort-Object CPU -Descending | Select-Object -First 10 Name, Id, CPU, WS', 'ipconfig /all', 'Get-Volume'],
    bash: ['hostname; uptime', 'df -h', 'free -h', 'ps aux --sort=-%cpu | head -n 12', 'ip -br addr', 'journalctl -p err -b --no-pager -n 40'],
    ssh: ['uptime', 'df -h', 'free -m'],
  };
  async function tabShell(e, body) {
    const shellName = e.caps.shellName;
    const cmd = h('textarea', { class: 'input', rows: 5, spellcheck: 'false', placeholder: `${shellName} command or script (Ctrl+Enter to run)`, 'aria-label': 'Command' });
    const out = h('pre', { class: 'out', text: 'Ready.' });
    const meta = h('span', { class: 'muted' });
    const run = btn('Run', async () => {
      const command = cmd.value;
      if (!command.trim()) return;
      run.disabled = true;
      meta.textContent = 'Running…';
      state.shellHistory = [command, ...state.shellHistory.filter(c => c !== command)].slice(0, 15);
      drawHistory();
      const started = performance.now();
      try {
        const r = await fetch(ep(e.id) + '/shell', {
          method: 'POST', headers: { 'Content-Type': 'application/json', [CSRF]: '1' }, body: JSON.stringify({ command }),
        });
        if (r.status === 401) return onSignedOut();
        const d = await r.json().catch(() => ({}));
        const secs = ((performance.now() - started) / 1000).toFixed(1);
        if (!r.ok && d.code) { out.textContent = d.error || 'Failed.'; meta.textContent = `Error · ${secs}s`; return; }
        out.textContent = d.output || '(no output)';
        meta.textContent = `${d.shell || shellName} · ${d.exitCode === null || d.exitCode === undefined ? '' : 'exit ' + d.exitCode + ' · '}${secs}s${d.truncated ? ' · output truncated' : ''}`;
        meta.className = d.ok ? 'badge ok' : 'badge err';
      } catch (err) { out.textContent = err.message; meta.textContent = 'Error'; }
      finally { run.disabled = false; }
    }, { cls: 'primary', icon: 'play' });
    cmd.addEventListener('keydown', ev => { if (ev.key === 'Enter' && (ev.ctrlKey || ev.metaKey)) { ev.preventDefault(); run.click(); } });
    const history = h('div', { class: 'chips' });
    const drawHistory = () => clear(history).append(...state.shellHistory.map(c =>
      h('button', { type: 'button', class: 'chip mono', text: c.length > 40 ? c.slice(0, 40) + '…' : c, title: c, onclick: () => { cmd.value = c; cmd.focus(); } })));
    const quick = (QUICK[shellName] || []).map(q => h('button', { type: 'button', class: 'chip mono', text: q, onclick: () => { cmd.value = q; run.click(); } }));
    body.append(
      h('div', { class: 'panel' },
        h('div', { class: 'head' }, h('h2', { class: 'grow', text: `Run a ${shellName} command` }), h('span', { class: 'badge', text: shellName })),
        h('div', { class: 'body stack' },
          cmd,
          h('div', { class: 'row spread' }, h('div', { class: 'chips' }, quick), h('div', { class: 'row' }, meta, run)),
          history)),
      out);
    drawHistory();
    cmd.focus();
  }

  // ---- files ----
  async function tabFiles(e, body) {
    let linux = e.isLinux;
    let cwd = '';
    const drives = h('select', { class: 'input', 'aria-label': 'Drive' });
    const pathBox = h('input', { class: 'input grow mono', 'aria-label': 'Path' });
    const listEl = h('div', { class: 'table-wrap' });
    const info = h('span', { class: 'muted' });
    const transfers = h('div', { class: 'stack' });
    const fileInput = h('input', { type: 'file', multiple: true, class: 'hidden' });
    const sep = () => (linux ? '/' : '\\');
    const join = (dir, name) => (dir.endsWith(sep()) ? dir + name : dir + sep() + name);

    const nav = async path => {
      if (!path) return;
      info.textContent = 'Loading…';
      try {
        const r = await api(`${ep(e.id)}/files/list?path=${encodeURIComponent(path)}`);
        linux = r.linux;
        cwd = r.path;
        pathBox.value = r.path;
        info.textContent = `${r.entries.filter(x => x.dir).length} folders · ${r.entries.filter(x => !x.dir).length} files${r.truncated ? ' · truncated' : ''}`;
        draw(r);
      } catch (err) { info.textContent = ''; clear(listEl).appendChild(errorBox(err)); }
    };

    const draw = r => {
      const rows = [];
      if (r.parent) rows.push(h('tr', null, h('td', { colspan: 4 }, h('span', { class: 'link row nowrap', onclick: () => nav(r.parent) }, icon('up'), '..'))));
      for (const f of r.entries) {
        const dl = `${ep(e.id)}/files/download?path=${encodeURIComponent(f.path)}`;
        rows.push(h('tr', null,
          h('td', null, h('span', { class: 'link row nowrap', onclick: () => (f.dir ? nav(f.path) : null) },
            icon(f.dir ? 'folder' : 'file'), h('span', { class: 'ellipsis', text: f.name + (f.link ? '  →' : '') }))),
          h('td', { class: 'num muted', text: f.dir ? '' : fmtBytes(f.size) }),
          h('td', { class: 'muted nowrap', text: fmtDate(f.modified) }),
          h('td', { class: 'actions' },
            f.dir ? null : h('a', { class: 'btn small', href: dl, download: f.name, title: 'Download' }, icon('download')),
            btn('Rename', () => rename(f), { cls: 'small' }),
            btn('Delete', () => remove(f), { cls: 'small danger' }))));
      }
      clear(listEl).appendChild(rows.length
        ? h('table', { class: 'data' }, h('thead', null, h('tr', null, h('th', { text: 'Name' }), h('th', { class: 'num', text: 'Size' }), h('th', { text: 'Modified' }), h('th'))), h('tbody', null, rows))
        : h('div', { class: 'empty', text: 'This folder is empty.' }));
    };

    const post = async (path, payload, okText) => {
      try { const r = await api(ep(e.id) + path, { method: 'POST', body: payload }); toast(r.message || okText, 'ok'); await nav(cwd); }
      catch (err) { toast(err.message, 'err'); }
    };
    const rename = async f => {
      const name = await dialog({ title: `Rename "${f.name}"`, input: f.name, confirm: 'Rename' });
      if (name && name !== f.name) await post('/files/rename', { path: f.path, newName: name }, 'Renamed.');
    };
    const remove = async f => {
      const ok = await dialog({ title: `Delete "${f.name}"?`, message: f.dir ? 'The folder and everything inside it will be deleted.' : 'The file will be deleted.', confirm: 'Delete', danger: true });
      if (ok) await post('/files/delete', { path: f.path, recursive: f.dir }, 'Deleted.');
    };
    const mkdir = async () => {
      if (!cwd) return;
      const name = await dialog({ title: 'New folder', input: 'New folder', confirm: 'Create' });
      if (name) await post('/files/mkdir', { dir: cwd, name }, 'Folder created.');
    };

    const upload = file => new Promise(resolve => {
      const bar = h('i', { style: { width: '0%' } });
      const label = h('span', { class: 'muted', text: 'Starting…' });
      const row = h('div', { class: 'panel body row nowrap' }, icon('upload'), h('span', { class: 'grow ellipsis', text: file.name }), h('div', { class: 'progress' }, bar), label);
      transfers.prepend(row);
      const xhr = new XMLHttpRequest();
      const qs = `dir=${encodeURIComponent(cwd)}&name=${encodeURIComponent(file.name)}&overwrite=0`;
      xhr.open('POST', `${ep(e.id)}/files/upload?${qs}`);
      xhr.setRequestHeader(CSRF, '1');
      xhr.setRequestHeader('Content-Type', 'application/octet-stream');
      xhr.upload.onprogress = ev => {
        if (ev.lengthComputable) { const p = (ev.loaded / ev.total) * 100; bar.style.width = p + '%'; label.textContent = `${p.toFixed(0)}% of ${fmtBytes(ev.total)}`; }
      };
      xhr.onload = () => {
        let d = {};
        try { d = JSON.parse(xhr.responseText); } catch { /* not JSON */ }
        const ok = xhr.status === 200 && d.ok !== false;
        bar.parentElement.classList.add(ok ? 'ok' : 'err');
        bar.style.width = '100%';
        label.textContent = ok ? 'Uploaded' : (d.error || `Failed (HTTP ${xhr.status})`);
        resolve(ok);
      };
      xhr.onerror = () => { bar.parentElement.classList.add('err'); label.textContent = 'Network error'; resolve(false); };
      xhr.send(file);
    });
    fileInput.addEventListener('change', async () => {
      const files = [...fileInput.files];
      fileInput.value = '';
      for (const f of files) {
        if (f.size > 2 * 1024 * 1024 * 1024) { toast(`${f.name}: uploads are limited to 2 GB.`, 'err'); continue; }
        await upload(f);
      }
      await nav(cwd);
    });

    pathBox.addEventListener('keydown', ev => { if (ev.key === 'Enter') nav(pathBox.value.trim()); });
    drives.addEventListener('change', () => nav(drives.value));
    body.append(
      h('div', { class: 'panel' },
        h('div', { class: 'head' }, drives, pathBox, btn('Go', () => nav(pathBox.value.trim()), { cls: 'small' }),
          btn('', () => nav(cwd), { icon: 'refresh', cls: 'small icon', title: 'Refresh' }),
          btn('New folder', mkdir, { cls: 'small', icon: 'folder' }),
          btn('Upload', () => fileInput.click(), { cls: 'small primary', icon: 'upload' }), fileInput),
        h('div', { class: 'body flush' }, listEl),
        h('div', { class: 'body row spread' }, info, h('span', { class: 'faint', text: 'Downloads stream through the hub · uploads up to 2 GB' }))),
      transfers);

    try {
      const d = await api(ep(e.id) + '/files/drives');
      linux = d.linux;
      for (const x of d.drives) drives.appendChild(h('option', { value: x.name, text: x.total ? `${x.name}  (${fmtBytes(x.free)} free)` : x.name }));
      const start = d.drives.find(x => x.name === '/') || d.drives[0];
      if (start) await nav(start.name); else clear(listEl).appendChild(h('div', { class: 'empty', text: 'No drives reported.' }));
    } catch (err) { clear(listEl).appendChild(errorBox(err)); }
  }

  // ---- generic table views (services / tasks / processes / apps) ----
  function tableView({ columns, rows, actions, filterPlaceholder, empty, onRefresh, countText }) {
    const filter = h('input', { class: 'input grow', placeholder: filterPlaceholder || 'Filter…', 'aria-label': 'Filter' });
    const wrap = h('div', { class: 'table-wrap' });
    const count = h('span', { class: 'muted' });
    const draw = () => {
      const q = filter.value.trim().toLowerCase();
      const list = q ? rows.filter(r => columns.some(([k]) => String(r[k] ?? '').toLowerCase().includes(q))) : rows;
      count.textContent = countText ? countText(list, rows) : `${list.length} of ${rows.length}`;
      const shown = list.slice(0, 1500);
      clear(wrap).appendChild(shown.length ? h('table', { class: 'data' },
        h('thead', null, h('tr', null, columns.map(([, label, cls]) => h('th', { class: cls || '', text: label })), actions ? h('th') : null)),
        h('tbody', null, shown.map(r => h('tr', null,
          columns.map(([k, , cls, fmt]) => h('td', { class: cls || '' }, fmt ? fmt(r[k], r) : String(r[k] ?? ''))),
          actions ? h('td', { class: 'actions' }, actions(r)) : null))))
        : h('div', { class: 'empty', text: rows.length ? 'Nothing matches the filter.' : (empty || 'Nothing to show.') }));
    };
    filter.addEventListener('input', draw);
    draw();
    return h('div', { class: 'panel' },
      h('div', { class: 'head' }, filter, count, onRefresh ? btn('', onRefresh, { icon: 'refresh', cls: 'small icon', title: 'Refresh' }) : null),
      h('div', { class: 'body flush' }, wrap));
  }
  function stateBadge(v) {
    const s = String(v || '');
    const cls = /^(Running|Ready|Succeeded)$/i.test(s) ? 'ok' : /Failed|Disabled/i.test(s) ? 'err' : /Pending|Inactive|Read-only/i.test(s) ? 'warn' : '';
    return h('span', { class: 'badge ' + cls, text: s || '--' });
  }

  async function tabServices(e, body) {
    const load = async () => {
      clear(body).appendChild(h('div', { class: 'muted', text: 'Loading services…' }));
      let r;
      try { r = await api(ep(e.id) + '/services'); } catch (err) { clear(body).appendChild(errorBox(err)); return; }
      const act = async (row, action) => {
        if (action !== 'start' && !(await dialog({ title: `${action === 'stop' ? 'Stop' : 'Restart'} ${row.Name}?`, message: `On ${e.name}.`, confirm: action === 'stop' ? 'Stop' : 'Restart', danger: true }))) return;
        try { const x = await api(`${ep(e.id)}/services/${action}`, { method: 'POST', body: { name: row.Name } }); toast(x.message || 'Done.', 'ok'); load(); }
        catch (err) { toast(err.message, 'err'); }
      };
      clear(body).append(
        r.elevated ? null : h('div', { class: 'note', text: 'The agent is not running elevated (administrator / root); starting or stopping services may be refused.' }),
        tableView({
          rows: r.rows,
          columns: [['Name', 'Name', 'mono'], ['DisplayName', 'Display name'], ['State', 'State', '', stateBadge], ['StartMode', 'Startup']],
          filterPlaceholder: 'Filter services…',
          countText: (list, all) => `${list.length} of ${all.length} · ${all.filter(x => x.State === 'Running').length} running`,
          actions: row => [
            btn('Start', () => act(row, 'start'), { cls: 'small', disabled: row.State === 'Running' }),
            btn('Stop', () => act(row, 'stop'), { cls: 'small danger', disabled: row.State !== 'Running' }),
            btn('Restart', () => act(row, 'restart'), { cls: 'small' })],
          onRefresh: load,
        }));
    };
    await load();
  }

  async function tabTasks(e, body) {
    const load = async () => {
      clear(body).appendChild(h('div', { class: 'muted', text: 'Loading scheduled tasks…' }));
      let r;
      try { r = await api(ep(e.id) + '/tasks'); } catch (err) { clear(body).appendChild(errorBox(err)); return; }
      const target = row => (row.TaskPath || '') + (row.TaskName || '');
      const act = async (row, action) => {
        if (action === 'disable' && !(await dialog({ title: `Disable ${row.TaskName}?`, message: `On ${e.name}.`, confirm: 'Disable', danger: true }))) return;
        try { const x = await api(`${ep(e.id)}/tasks/${action}`, { method: 'POST', body: { name: target(row) } }); toast(x.message || 'Done.', 'ok'); load(); }
        catch (err) { toast(err.message, 'err'); }
      };
      const cols = [['TaskName', 'Task', '', (v, row) => h('div', null, h('div', { text: v }), h('div', { class: 'faint mono', text: row.TaskPath || '' }))], ['State', 'State', '', stateBadge]];
      if (r.columns.includes('Type')) cols.unshift(['Type', 'Type']);
      if (r.columns.includes('Schedule')) cols.push(['Schedule', 'Schedule', 'mono']);
      if (r.columns.includes('NextRun')) cols.push(['NextRun', 'Next run', 'nowrap']);
      if (r.columns.includes('Command')) cols.push(['Command', 'Command', 'mono']);
      clear(body).appendChild(tableView({
        rows: r.rows,
        columns: cols,
        filterPlaceholder: 'Filter tasks…',
        countText: (list, all) => `${list.length} of ${all.length} · ${all.filter(x => x.State === 'Disabled').length} disabled`,
        actions: row => row.ReadOnly === 'Yes'
          ? h('span', { class: 'faint', text: 'read-only (cron)', title: 'Edit cron entries on the device (crontab -e).' })
          : [btn('Run', () => act(row, 'run'), { cls: 'small' }),
            btn('Enable', () => act(row, 'enable'), { cls: 'small', disabled: row.State !== 'Disabled' }),
            btn('Disable', () => act(row, 'disable'), { cls: 'small danger', disabled: row.State === 'Disabled' })],
        onRefresh: load,
      }));
    };
    await load();
  }

  async function tabInventory(e, body) {
    const load = async () => {
      clear(body).appendChild(h('div', { class: 'muted', text: 'Collecting inventory…' }));
      let r;
      try { r = await api(ep(e.id) + '/inventory'); } catch (err) { clear(body).appendChild(errorBox(err)); return; }
      const kill = async p => {
        if (!(await dialog({ title: `End ${p.name} (PID ${p.pid})?`, message: `On ${e.name}. Unsaved work in that program is lost.`, confirm: 'End process', danger: true }))) return;
        try { const x = await api(ep(e.id) + '/processes/kill', { method: 'POST', body: { pid: p.pid } }); toast(x.message || 'Process ended.', 'ok'); load(); }
        catch (err) { toast(err.message, 'err'); }
      };
      const hw = r.hardware;
      clear(body).append(
        h('div', { class: 'panel' }, h('div', { class: 'head' }, h('h2', { text: 'Hardware' })),
          h('div', { class: 'body' }, h('dl', { class: 'kv' },
            [['CPU', `${hw.cpu}${hw.cores ? ` (${hw.cores} logical cores)` : ''}`], ['Memory', fmtBytes(hw.ramBytes)], ['GPU', hw.gpu],
              ['Disks', (hw.disks || []).map(d => `${d.name} ${fmtBytes(d.free)} free of ${fmtBytes(d.total)}`).join(' · ')]]
              .map(([k, v]) => [h('dt', { text: k }), h('dd', { text: v || '--' })])))),
        h('h3', { class: 'muted', text: `Processes (${r.processes.length})` }),
        tableView({
          rows: r.processes, filterPlaceholder: 'Filter processes…',
          columns: [['name', 'Name'], ['pid', 'PID', 'num mono'], ['memory', 'Memory', 'num', v => fmtBytes(v)]],
          actions: p => btn('End', () => kill(p), { cls: 'small danger' }),
          onRefresh: load,
        }),
        h('h3', { class: 'muted', text: `Installed apps (${r.apps.length})` }),
        tableView({ rows: r.apps, filterPlaceholder: 'Filter apps…', columns: [['name', 'Name'], ['version', 'Version', 'mono'], ['publisher', 'Publisher']] }));
    };
    await load();
  }

  // ---- screen ----
  async function tabScreen(e, body) {
    const img = h('img', { class: 'screen-img hidden', alt: `Screen of ${e.name}` });
    const status = h('span', { class: 'muted', text: 'Snapshots are periodic JPEG frames (no remote control). The device may ask its user for consent first.' });
    const interval = h('select', { class: 'input', 'aria-label': 'Refresh interval' },
      [['3000', 'every 3 s'], ['5000', 'every 5 s'], ['10000', 'every 10 s']].map(([v, t]) => h('option', { value: v, text: t })));
    let timer = null;
    let busy = false;
    let lastUrl = null;
    const snap = async () => {
      if (busy) return;
      busy = true;
      try {
        const r = await fetch(`${ep(e.id)}/screen.jpg?t=${Date.now()}`, { cache: 'no-store' });
        if (r.status === 401) return onSignedOut();
        if (!r.ok) { const d = await r.json().catch(() => ({})); throw new Error(d.error || `HTTP ${r.status}`); }
        const blob = await r.blob();
        const url = URL.createObjectURL(blob);
        img.src = url;
        img.classList.remove('hidden');
        if (lastUrl) URL.revokeObjectURL(lastUrl);
        lastUrl = url;
        status.textContent = `Snapshot ${new Date().toLocaleTimeString()} · ${fmtBytes(blob.size)}`;
      } catch (err) { status.textContent = err.message; stop(); }
      finally { busy = false; }
    };
    const stop = () => { if (timer) clearInterval(timer); timer = null; toggle.querySelector('span').textContent = 'Auto refresh'; };
    const toggle = btn('Auto refresh', () => {
      if (timer) return stop();
      snap();
      timer = setInterval(() => { if (!document.hidden) snap(); }, Number(interval.value));
      toggle.querySelector('span').textContent = 'Stop';
    }, { cls: 'small' });
    state.cleanups.push(() => { stop(); if (lastUrl) URL.revokeObjectURL(lastUrl); });
    body.appendChild(h('div', { class: 'panel' },
      h('div', { class: 'head' }, btn('Snapshot', snap, { cls: 'small primary', icon: 'refresh' }), toggle, interval, status),
      h('div', { class: 'body' }, img)));
  }

  // ---------------- Deploy ----------------
  async function viewDeploy() {
    const content = layout('Deploy apps', 'Install or upgrade winget packages on Windows agents');
    const d = state.deploy;
    const q = h('input', { class: 'input grow', placeholder: 'Search the winget catalog (e.g. firefox, 7zip, vlc)…', 'aria-label': 'Search apps' });
    const results = h('div', { class: 'result-list' });
    const resultInfo = h('span', { class: 'muted' });
    const selected = h('div', { class: 'chips' });
    const targets = h('div', { class: 'stack' });
    const jobBox = h('div', { class: 'stack' });
    const deployBtn = btn('Deploy', () => start(), { cls: 'primary', icon: 'deploy' });

    const drawSelected = () => {
      clear(selected).append(...(d.selected.size ? [...d.selected.values()].map(p =>
        h('button', { type: 'button', class: 'chip on', title: 'Remove', text: `${p.name}  ×`, onclick: () => { d.selected.delete(p.id); drawSelected(); drawResults(lastResults); } }))
        : [h('span', { class: 'faint', text: 'No apps selected yet.' })]));
      deployBtn.disabled = !d.selected.size || !d.targets.size;
      deployBtn.querySelector('span').textContent = d.selected.size && d.targets.size ? `Deploy ${d.selected.size} app(s) to ${d.targets.size} endpoint(s)` : 'Deploy';
    };
    let lastResults = [];
    const drawResults = items => {
      lastResults = items;
      clear(results).append(...items.map(p => h('div', { class: 'item' },
        h('div', { class: 'grow' }, h('div', null, h('strong', { text: p.name }), h('span', { class: 'faint', text: p.version ? `  ${p.version}` : '' })),
          h('div', { class: 'mono faint ellipsis', text: p.id + (p.publisher ? ` · ${p.publisher}` : '') })),
        d.selected.has(p.id)
          ? btn('Added', () => { d.selected.delete(p.id); drawSelected(); drawResults(lastResults); }, { cls: 'small' })
          : btn('Add', () => { d.selected.set(p.id, p); drawSelected(); drawResults(lastResults); }, { cls: 'small primary' }))));
    };
    let searchTimer = null;
    const search = async () => {
      try {
        const r = await api(`/api/v2/winget/search?q=${encodeURIComponent(q.value.trim())}&limit=30`);
        if (!r.available) { resultInfo.textContent = r.message; drawResults([]); return; }
        resultInfo.textContent = `${r.total} match(es)${r.total > r.items.length ? `, showing ${r.items.length}` : ''}`;
        drawResults(r.items);
      } catch (err) { resultInfo.textContent = err.message; }
    };
    q.addEventListener('input', () => { clearTimeout(searchTimer); searchTimer = setTimeout(search, 250); });

    const opt = (key, label) => {
      const c = h('input', { type: 'checkbox', checked: d.opts[key] });
      c.addEventListener('change', () => { d.opts[key] = c.checked; });
      return h('label', { class: 'check' }, c, label);
    };

    const drawTargets = list => {
      const rows = list.filter(e => e.isAgent || e.osFamily === 'windows').map(e => {
        const supported = e.caps.deploy;
        const c = h('input', { type: 'checkbox', checked: supported && d.targets.has(e.id), disabled: !supported });
        c.addEventListener('change', () => { if (c.checked) d.targets.add(e.id); else d.targets.delete(e.id); drawSelected(); });
        return h('label', { class: 'check', title: supported ? e.address : 'winget deployment is available on Windows agents only' }, c, statusDot(e),
          h('span', { text: e.name }), h('span', { class: supported ? 'faint' : 'badge warn', text: supported ? e.address : (e.osFamily === 'linux' ? 'Windows only' : 'Agent required') }));
      });
      clear(targets).append(...(rows.length ? rows : [h('div', { class: 'faint', text: 'No Windows agent endpoints saved.' })]));
    };

    const drawJob = j => {
      clear(jobBox).append(h('div', { class: 'panel' },
        h('div', { class: 'head' }, h('h2', { class: 'grow', text: `Deployment ${j.id}` }), h('span', { class: 'badge ' + (j.done ? 'ok' : 'accent'), text: j.done ? 'Finished' : 'Running' }),
          j.done ? null : btn('Cancel', async () => { try { await api(`/api/v2/deploy/${encodeURIComponent(j.id)}/cancel`, { method: 'POST', body: {} }); } catch (err) { toast(err.message, 'err'); } }, { cls: 'small danger' })),
        h('div', { class: 'body stack' }, j.targets.map(t => h('div', { class: 'stack' },
          h('div', { class: 'row' }, h('strong', { text: t.name }), stateBadge(t.status), t.error ? h('span', { class: 'error-box', text: t.error }) : null),
          h('table', { class: 'data' }, h('tbody', null, t.packages.map(p => h('tr', null,
            h('td', { class: 'mono', text: p.id }), h('td', null, stateBadge(p.phase)),
            h('td', null, h('div', { class: 'progress' + (p.phase === 'Succeeded' ? ' ok' : p.phase === 'Failed' ? ' err' : '') }, h('i', { style: { width: Math.max(0, p.percent) + '%' } }))),
            h('td', { class: 'muted', text: p.detail }))))),
          t.log.length ? h('pre', { class: 'out', text: t.log.join('\n') }) : null)))));
    };
    const poll = async () => {
      if (!d.jobId) return;
      try { const j = await api(`/api/v2/deploy/${encodeURIComponent(d.jobId)}`); drawJob(j); if (j.done) { toast(`Deployment ${j.id} finished.`, 'ok'); d.jobId = null; } }
      catch (err) { if (err.status === 404) d.jobId = null; }
    };
    const start = async () => {
      const names = [...d.selected.values()].map(p => p.name).join(', ');
      if (!(await dialog({ title: 'Deploy apps?', message: `${names}\n\nto ${d.targets.size} endpoint(s).`, confirm: 'Deploy' }))) return;
      try {
        const j = await api('/api/v2/deploy', { method: 'POST', body: { packages: [...d.selected.keys()], endpoints: [...d.targets], ...d.opts } });
        d.jobId = j.id;
        drawJob(j);
        toast('Deployment started.', 'ok');
      } catch (err) { toast(err.message, 'err'); }
    };

    content.append(
      h('div', { class: 'cols' },
        h('div', { class: 'panel' },
          h('div', { class: 'head' }, icon('search'), q),
          h('div', { class: 'body flush' }, results),
          h('div', { class: 'body' }, resultInfo)),
        h('div', { class: 'stack' },
          h('div', { class: 'panel' }, h('div', { class: 'head' }, h('h2', { text: 'Selected apps' })), h('div', { class: 'body' }, selected)),
          h('div', { class: 'panel' }, h('div', { class: 'head' }, h('h2', { text: 'Target endpoints' })), h('div', { class: 'body' }, targets)),
          h('div', { class: 'panel' }, h('div', { class: 'head' }, h('h2', { text: 'Options' })),
            h('div', { class: 'body stack' }, opt('silent', 'Silent install'), opt('machineScope', 'Machine scope (all users)'), opt('upgrade', 'Upgrade when already installed'), deployBtn)))),
      jobBox);
    drawSelected();
    search();
    try { drawTargets(await loadEndpoints()); } catch (err) { clear(targets).appendChild(errorBox(err)); }
    if (!d.jobId) {
      try { const r = await api('/api/v2/deploy'); if (r.jobs.length) drawJob(r.jobs[0]); } catch { /* none */ }
    } else poll();
    every(1500, poll);
  }

  boot();
})();
