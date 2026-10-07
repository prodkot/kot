'use strict';
(() => {
  const descriptions = [
    'Проверь задержку и сохрани любимые серверы. Режим «Авто» сам выберет быстрый доступный сервер.',
    'Адреса, процессы и маршруты текущих соединений. Тут же можно открыть журнал и скопировать полный лог.',
    'Светлая или тёмная тема, акцентный цвет, автозапуск и Kill switch. Новые версии приходят с GitHub.'
  ];
  const tabs = [...document.querySelectorAll('[role="tab"]')];
  function selectTab(index, focus = false) {
    tabs.forEach((tab, i) => {
      const active = i === index;
      tab.setAttribute('aria-selected', String(active));
      tab.tabIndex = active ? 0 : -1;
      document.getElementById(tab.getAttribute('aria-controls')).hidden = !active;
    });
    document.getElementById('feature-description').textContent = descriptions[index];
    if (focus) tabs[index].focus();
  }
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => selectTab(index));
    tab.addEventListener('keydown', event => {
      let next;
      if (event.key === 'ArrowRight') next = (index + 1) % tabs.length;
      if (event.key === 'ArrowLeft') next = (index + tabs.length - 1) % tabs.length;
      if (event.key === 'Home') next = 0;
      if (event.key === 'End') next = tabs.length - 1;
      if (next !== undefined) { event.preventDefault(); selectTab(next, true); }
    });
  });
  const frame = document.getElementById('client-demo');
  const demoWindow = document.getElementById('demo-window');
  const expandButton = document.getElementById('expand-demo');
  const control = action => frame.contentWindow.postMessage({ kind: 'kot-demo-control', action }, '*');
  document.getElementById('reset-demo').addEventListener('click', () => control('reset'));
  // The same dialog stays in place, so its iframe never reloads on expand/close.
  expandButton.addEventListener('click', () => {
    demoWindow.close(); demoWindow.showModal();
    document.body.classList.add('demo-open');
    expandButton.setAttribute('aria-expanded', 'true');
    document.getElementById('close-demo').focus();
  });
  function closeExpanded() {
    if (!demoWindow.matches(':modal')) return;
    demoWindow.close(); demoWindow.show();
    document.body.classList.remove('demo-open');
    expandButton.setAttribute('aria-expanded', 'false'); expandButton.focus({ preventScroll: true });
  }
  document.getElementById('close-demo').addEventListener('click', closeExpanded);
  demoWindow.addEventListener('cancel', event => { event.preventDefault(); closeExpanded(); });
  window.addEventListener('message', event => {
    if (event.source !== frame.contentWindow || event.data?.kind !== 'kot-demo') return;
    if (event.data.dialog && !demoWindow.matches(':modal')) {
      frame.scrollIntoView({ block: 'center', behavior: 'instant' });
    }
    if (event.data.close) closeExpanded();
  });
  // Same-origin metadata is rebuilt after a release. No tokens or tracking in the browser.
  fetch('release.json', { cache: 'no-cache' }).then(response => {
    if (!response.ok) throw new Error('Release metadata unavailable');
    return response.json();
  }).then(release => {
    if (!/^\d+\.\d+\.\d+$/.test(release.version)) return;
    const expected = `https://github.com/prodkot/kot/releases/download/v${release.version}/Kot-Setup-${release.version}-Windows-x64.exe`;
    if (release.download !== expected || !Number.isSafeInteger(release.size) || release.size <= 0) return;
    document.querySelectorAll('.download-link').forEach(link => { link.href = expected; });
    document.querySelectorAll('[data-release-version]').forEach(label => { label.textContent = `v${release.version}`; });
    document.getElementById('download-size').textContent = `${Math.round(release.size / 1024 / 1024)} МБ`;
  }).catch(() => { /* The releases page remains a working fallback. */ });
})();
