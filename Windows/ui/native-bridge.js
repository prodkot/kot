/* Transport only. Native origin validation and authorization remain in C#. */
'use strict';
window.KotNativeBridge = host => {
  let sequence = 0;
  const pending = new Map();

  function rpc(action, data = {}) {
    if (!host) return Promise.reject(new Error('Откройте интерфейс через Kot.exe.'));
    return new Promise((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => {
        pending.delete(id);
        reject(new Error('Операция заняла слишком много времени.'));
      }, action === 'downloadUpdate' ? 720000 : 120000);
      pending.set(id, {resolve, reject, timer});
      host.postMessage({id, action, data});
    });
  }

  function start({snapshot, navigate}) {
    host.addEventListener('message', ({data}) => {
      if (data.kind === 'heartbeat') {
        rpc('heartbeat').catch(() => {});
        return;
      }
      if (data.kind === 'navigate' && ['home', 'servers', 'statistics', 'diagnostics', 'logs', 'services', 'settings'].includes(data.page)) {
        navigate(data.page);
      } else if (data.kind === 'snapshot') {
        snapshot(data.data);
      } else if (data.kind === 'reply') {
        const request = pending.get(data.id);
        if (request) {
          pending.delete(data.id);
          clearTimeout(request.timer);
          data.ok ? request.resolve(data.data) : request.reject(new Error(data.error));
        }
      }
    });
  }

  return {rpc, start};
};
