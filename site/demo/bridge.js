'use strict';
(() => {
  // Browser-only stand-in for WebView. All state stays in this frame's memory.
  // No subscriptions, hardware IDs, system settings or network connections are read.
  const version = document.body.dataset.demoVersion;
  const pages = ['home', 'servers', 'logs', 'settings'];
  const listeners = new Set();
  const timers = new Map();
  let model, tick = 0, subscriptionSequence = 1;
  const samples = () => [
    { id: 'nl', name: 'Нидерланды', countryCode: 'NL', code: 'VL', protocol: 'vless', latency: 42 },
    { id: 'de', name: 'Германия', countryCode: 'DE', code: 'VL', protocol: 'vless', latency: 68 },
    { id: 'fi', name: 'Финляндия', countryCode: 'FI', code: 'TR', protocol: 'trojan', latency: 91 },
    { id: 'lv', name: 'Латвия', countryCode: 'LV', code: 'HY', protocol: 'hysteria2', latency: 56 }
  ].map(node => ({ ...node, ping: { status: 'ok', ms: node.latency, mode: 'http', successes: 2, attempts: 2 } }));
  function emit(data) { listeners.forEach(listener => listener({ data: structuredClone(data) })); }
  function notify(data) { parent.postMessage({ kind: 'kot-demo', ...data }, '*'); }
  function snapshot() {
    model.telemetry.count = model.telemetry.connections.length;
    emit({ kind: 'snapshot', data: model });
    document.getElementById('demo-failure').disabled = model.state !== 'connected';
  }
  function log(text, level = 'INFO') {
    model.journal += `${new Date().toLocaleTimeString('ru-RU')} ${level} [демо] ${text}\n`;
    model.journal = model.journal.split('\n').slice(-80).join('\n');
  }
  function stop(key) { clearTimeout(timers.get(key)); timers.delete(key); }
  function later(key, callback, delay) {
    stop(key);
    timers.set(key, setTimeout(() => { timers.delete(key); callback(); }, delay));
  }
  function emptyTraffic() {
    return { available: true, downloadRate: 0, uploadRate: 0, downloadTotal: 0, uploadTotal: 0, count: 0, connections: [] };
  }
  function chooseAuto() {
    const pool = model.nodes.filter(node => !model.favorites.length || model.favorites.includes(node.id));
    model.automaticNode = [...pool].sort((a, b) => a.latency - b.latency)[0]?.id || model.nodes[0]?.id;
  }
  function selectedName() { return model.nodes.find(node => node.id === (model.selected === 'auto' ? model.automaticNode : model.selected))?.name || 'Сервер'; }
  function connections() {
    const names = ['cdn.example.org:443', 'chat.example.org:443', 'music.example.org:443'];
    model.telemetry.connections = names.map((destination, index) => ({
      id: 'connection-' + index, destination, process: index === 1 ? 'messenger.exe' : 'browser.exe',
      network: 'tcp', inbound: model.settings.mode === 'tun' ? 'tun/tun-in' : 'mixed/system-in', chains: [selectedName()], rule: 'final',
      download: (index + 1) * 2097152, upload: 65536, start: new Date().toISOString()
    }));
  }
  function reset() {
    timers.forEach(clearTimeout); timers.clear(); tick = 0; subscriptionSequence = 1;
    model = {
      version, name: 'Личная подписка', hasSubscription: true, nodes: samples(), selected: 'nl', automaticNode: 'nl',
      state: 'idle', error: '', warnings: [], backgroundError: '', favorites: [], updated: null,
      settings: { theme: 'dark', accent: 'lime', mode: 'tun', startup: false, autoConnect: false, tray: true, sendHwid: false, killSwitch: false, killSwitchActive: false, bypass: '' },
      subscriptions: [{ id: 'sample-1', name: 'Личная подписка', count: 4 }], activeSubscription: 'sample-1',
      automation: { reconnect: true, refreshHours: 6, pingMinutes: 0, autoMinutes: 3, autoToleranceMs: 50 },
      ping: { busy: false, done: 0, total: 0, error: '', settings: { mode: 'http', url: 'https://example.org', timeoutMs: 5000, attempts: 2, parallelism: 3, sort: 'none' } },
      updates: { busy: false, status: '', error: '', availableVersion: null }, telemetry: emptyTraffic(), journal: ''
    };
    log('Интерфейс готов. Можно выбрать сервер и подключиться.');
  }
  function navigate(page) {
    if (!pages.includes(page)) return;
    emit({ kind: 'navigate', page }); notify({ page });
  }
  function ping(nodes) {
    if (model.ping.busy || !nodes.length) return;
    model.ping.busy = true; model.ping.done = 0; model.ping.total = nodes.length;
    nodes.forEach(node => { node.ping = { status: 'checking' }; });
    let index = 0;
    function next() {
      const node = nodes[index++];
      node.ping = node.protocol === 'hysteria2' && model.ping.settings.mode === 'tcp'
        ? { status: 'unsupported', mode: 'tcp', error: 'Hysteria2 использует UDP. Выбери HTTP.' }
        : { status: 'ok', ms: node.latency, mode: model.ping.settings.mode, successes: model.ping.settings.attempts, attempts: model.ping.settings.attempts };
      model.ping.done = index; model.ping.busy = index < nodes.length;
      if (model.ping.busy) later('ping', next, 280);
      else { chooseAuto(); log('Проверка серверов завершена. Значения пинга демонстрационные.'); }
      snapshot();
    }
    later('ping', next, 400);
  }
  function stopPing() {
    stop('ping'); model.ping.busy = false;
    model.nodes.forEach(node => { if (node.ping.status === 'checking') node.ping = { status: 'cancelled' }; });
  }
  async function action(name, data) {
    switch (name) {
      case 'ready': notify({ ready: true, page: 'home' }); break;
      case 'view': notify({ page: data.page }); break;
      case 'toggle':
        stop('connect'); model.error = '';
        if (['connected', 'connecting', 'waiting'].includes(model.state)) {
          model.state = 'idle'; model.settings.killSwitchActive = false; model.telemetry.connections = [];
          log('Подключение отключено вручную.');
        } else if (model.nodes.length) {
          model.state = 'connecting'; log('Подключение: ' + selectedName());
          later('connect', () => { model.state = 'connected'; model.settings.killSwitchActive = false; model.telemetry.downloadRate = 2516582; model.telemetry.uploadRate = 398459; connections(); log('Подключено. Трафик в этом окне показан для примера.'); snapshot(); }, 850);
        }
        break;
      case 'select':
        if (data.id !== 'auto' && !model.nodes.some(node => node.id === data.id)) throw Error('Сервер не найден.');
        model.selected = data.id; chooseAuto();
        if (model.state === 'connected') connections();
        log('Выбран сервер: ' + selectedName()); break;
      case 'favorite':
        if (!model.nodes.some(node => node.id === data.id)) throw Error('Сервер не найден.');
        model.favorites = model.favorites.includes(data.id) ? model.favorites.filter(id => id !== data.id) : [...model.favorites, data.id];
        chooseAuto(); if (model.state === 'connected' && model.selected === 'auto') connections(); break;
      case 'settings':
        if (!Object.hasOwn(model.settings, data.key) || data.key === 'killSwitchActive') throw Error('Неизвестная настройка.');
        if (data.key === 'mode' && !['tun', 'proxy'].includes(data.value)) throw Error('Неизвестный режим подключения.');
        if (data.key === 'bypass' && /:\/\//.test(data.value)) throw Error('Введи домены без https://');
        model.settings[data.key] = data.value;
        if (data.key === 'mode' && model.state === 'connected') connections();
        if (data.key === 'killSwitch' && !data.value) { model.settings.killSwitchActive = false; model.error = ''; }
        break;
      case 'pingAll': ping(model.nodes); break;
      case 'pingNode': ping(model.nodes.filter(node => node.id === data.id)); break;
      case 'cancelPing': stopPing(); break;
      case 'pingSettings': model.ping.settings = { ...data }; break;
      case 'automationSettings': model.automation = { ...data }; break;
      case 'refresh': log('Пример подписки обновлён.'); break;
      case 'import': {
        // Intentionally ignore the supplied URL. This action never makes a request.
        stopPing(); const id = 'sample-' + (++subscriptionSequence);
        const title = (typeof data.name === 'string' && data.name.trim().slice(0, 60)) || 'Пример подписки';
        model.subscriptions.push({ id, name: title, count: 4 });
        model.activeSubscription = id; model.name = title; model.hasSubscription = true;
        model.nodes = samples(); model.selected = 'nl'; model.favorites = [];
        if (model.state === 'connected') connections();
        log('Добавлен пример подписки. Ссылки в демо не загружаются.'); break;
      }
      case 'cancelImport': break;
      case 'switchSubscription': {
        const sub = model.subscriptions.find(sub => sub.id === data.id);
        if (!sub) throw Error('Подписка не найдена.');
        model.activeSubscription = sub.id; model.name = sub.name; break;
      }
      case 'remove': {
        stop('connect'); stopPing(); model.state = 'idle'; model.telemetry = emptyTraffic(); model.error = ''; model.settings.killSwitchActive = false;
        model.subscriptions = model.subscriptions.filter(sub => sub.id !== model.activeSubscription);
        const sub = model.subscriptions[0]; model.activeSubscription = sub?.id || ''; model.name = sub?.name || ''; model.hasSubscription = !!sub;
        model.nodes = sub ? samples() : []; model.selected = sub ? 'nl' : ''; model.favorites = []; break;
      }
      case 'checkUpdates':
        model.updates.busy = true; model.updates.status = 'Проверка'; snapshot();
        await new Promise(resolve => setTimeout(resolve, 500));
        model.updates.busy = false; model.updates.status = `Демо kot. ${version}. Настоящие обновления доступны в программе.`; break;
      case 'cancelUpdate': model.updates.busy = false; break;
      case 'readHwid': return { hwid: 'DEMO-DEVICE-ID', name: `kot. windows (${version}) · пример` };
      case 'copyHwid': case 'copyLog': throw Error('Это пример. Копирование доступно в установленной программе.');
      case 'backupExport': case 'backupImport': throw Error('Резервные копии доступны в установленной программе.');
      case 'drag': case 'resize': break;
      default: throw Error('Это действие доступно в установленной программе.');
    }
    snapshot();
  }
  window.chrome = { webview: {
    addEventListener(type, listener) { if (type === 'message') listeners.add(listener); },
    postMessage(message) {
      Promise.resolve().then(() => action(message.action, message.data || {})).then(data => {
        emit({ kind: 'reply', id: message.id, ok: true, data });
      }).catch(error => emit({ kind: 'reply', id: message.id, ok: false, error: error.message }));
    }
  } };
  const failure = document.createElement('button');
  failure.id = 'demo-failure'; failure.className = 'demo-failure'; failure.textContent = 'Сбой подключения';
  failure.title = 'Проверить сценарий сбоя и Kill switch';
  document.querySelector('.chrome').replaceChildren(failure);
  failure.addEventListener('click', () => {
    if (model.state !== 'connected') return;
    stop('connect'); model.state = 'idle'; model.settings.killSwitchActive = model.settings.killSwitch;
    model.error = 'Сбой в демо.' + (model.settings.killSwitch ? ' Kill switch блокирует прямой интернет.' : 'Подключись снова или включи Kill switch в настройках.');
    model.telemetry.connections = []; log('Соединение прервано.' + (model.settings.killSwitch ? ' Прямой интернет заблокирован.' : ''), 'ERROR');
    snapshot(); navigate('home');
  });
  document.querySelectorAll('[data-page]').forEach(button => { button.setAttribute('aria-label', button.textContent.trim()); });
  document.querySelector('#deviceDialog .dialog-note').textContent = 'Это вымышленный ID. Демо не читает данные твоего устройства.';
  document.querySelector('#onlineUpdateDialog > .dialog-note:last-child').textContent = 'На сайте ничего не скачивается и не устанавливается. Эта проверка показывает только интерфейс.';
  document.getElementById('addTitle').textContent = 'Добавить пример';
  document.querySelector('label[for=profileLink]').textContent = 'Пример ссылки';
  document.getElementById('profileLink').type = 'text';
  document.getElementById('profileLink').placeholder = 'Настоящая ссылка не нужна';
  document.getElementById('replaceNote').textContent = 'Добавится пример подписки с четырьмя серверами. Ссылки в демо не загружаются.';
  // The client clears the form on open; prefill after it has opened.
  document.querySelectorAll('[data-open-add]').forEach(button => button.addEventListener('click', () => {
    setTimeout(() => { document.getElementById('profileLink').value = 'https://example.org/demo'; }, 0);
  }));
  document.getElementById('homeServer').addEventListener('click', () => {
    if (!model.nodes.length) setTimeout(() => { document.getElementById('profileLink').value = 'https://example.org/demo'; }, 0);
  });
  // A dialog is centered in the embedded viewport. Ask the page to reveal
  // that viewport, even when opening it from a button near the bottom.
  const dialogObserver = new MutationObserver(records => {
    if (records.some(record => record.target.open)) notify({ dialog: true });
  });
  document.querySelectorAll('dialog').forEach(dialog => {
    dialogObserver.observe(dialog, { attributes: true, attributeFilter: ['open'] });
  });
  window.addEventListener('message', event => {
    if (event.source !== parent || event.data?.kind !== 'kot-demo-control') return;
    if (event.data.action === 'navigate') navigate(event.data.page);
    // A reload cancels all pending UI operations as well as simulation timers.
    if (event.data.action === 'reset') location.reload();
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && !document.querySelector('dialog[open]')) notify({ close: true });
  });
  reset();
  setInterval(() => {
    if (document.hidden || model.state !== 'connected') return;
    const t = model.telemetry; tick++;
    t.downloadRate = Math.round((2.4 + Math.sin(tick * .7) * .6) * 1048576);
    t.uploadRate = Math.round((.38 + Math.cos(tick * .5) * .12) * 1048576);
    t.downloadTotal += t.downloadRate; t.uploadTotal += t.uploadRate;
    t.connections.forEach((connection, i) => { connection.download += Math.round(t.downloadRate / (i + 2)); connection.upload += Math.round(t.uploadRate / 3); });
    snapshot();
  }, 1000);
})();
