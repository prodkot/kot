'use strict';
(() => {
  const ui = window.KotUI;
  const explorer = document.querySelector('[data-explorer]');
  const screens = {
    home: { title: 'Подключение', copy: 'Выбираешь сервер и подключаешься. Здесь же переключаются TUN и системный прокси.', facts: ['Два режима подключения', 'Скорость и время сеанса'], page: 'home', alt: 'Подключение kot. 0.8.0: кнопка подключения, сервер и выбор режима' },
    servers: { title: 'Серверы', copy: 'Подписки и отдельные серверы в одном списке. Нужный можно найти по названию, проверить и добавить в избранное.', facts: ['Поиск, задержка и избранное', 'Автовыбор доступного сервера'], page: 'servers', alt: 'Серверы kot. 0.8.0: список, поиск, пинг и избранное' },
    routing: { title: 'Правила', copy: 'Выбираешь, какие сайты и приложения идут через сервер, какие напрямую, а какие нужно заблокировать.', facts: ['Правила для сайтов и приложений', 'Отдельные настройки DNS'], page: 'settings', alt: 'Маршрутизация kot. 0.8.0: правила, приложения и DNS', label: 'Открыть настройки в демо' },
    statistics: { title: 'Статистика', copy: 'Сколько скачал и отправил, как долго был подключён. Можно посмотреть текущий сеанс или историю за другой период.', facts: ['Трафик по периодам', 'История подключений'], page: 'statistics', alt: 'Статистика kot. 0.8.0: сеансы и история трафика' },
    settings: { title: 'Настройки', copy: 'Автозапуск, подключение при старте и работа в трее. Если что-то пошло не так, здесь же есть диагностика.', facts: ['Автозапуск и трей', 'Маршрутизация и диагностика'], page: 'settings', alt: 'Настройки kot. 0.8.0: внешний вид и поведение приложения' }
  };
  const tabs = [...explorer.querySelectorAll('[data-explore]')];
  const list = explorer.querySelector('[role=tablist]');
  const updateTrack = ui.tabIndicator(list, 'tile');
  const image = explorer.querySelector('[data-explorer-image]');
  const demo = explorer.querySelector('[data-explorer-demo]');
  const text = explorer.querySelector('[data-explorer-text]');
  const copy = explorer.querySelector('[data-explorer-copy]');
  const number = explorer.querySelector('[data-explorer-number]');
  const status = explorer.querySelector('[data-explorer-status]');
  const progress = document.createElement('span');
  progress.className = 'vw-tour-fill';
  list.querySelector('.tab-track').append(progress);
  const interval = 5200;
  const manualPause = 5000;
  let index = 0, timer, progressAnimation, manualUntil = 0;
  let modalOpen = false, heldPointer = null, disposed = false;
  image.dataset.screenKey = 'home';

  function suspend() {
    clearTimeout(timer); progressAnimation?.cancel();
    list.classList.remove('is-playing');
  }
  function canAutoplay() {
    return !disposed && !document.hidden && !modalOpen && heldPointer === null && !ui.reduced.matches;
  }
  function schedule(delay = interval) {
    suspend();
    if (!canAutoplay()) return;
    const wait = Math.max(delay, manualUntil - Date.now());
    list.classList.add('is-playing');
    progressAnimation = ui.animate(progress, [{ transform: 'scaleX(0)' }, { transform: 'scaleX(1)' }], { duration: wait, easing: 'linear', fill: 'forwards' });
    timer = setTimeout(() => {
      if (canAutoplay()) select(index + 1, false, false, 1);
      else suspend();
    }, wait);
  }
  function pauseAfterInteraction() {
    manualUntil = Date.now() + manualPause;
    schedule(manualPause);
  }
  function revealTab(tab) {
    if (list.scrollWidth <= list.clientWidth) return;
    const bounds = list.getBoundingClientRect(), target = tab.getBoundingClientRect();
    const left = target.left - bounds.left - list.clientLeft;
    const delta = left < 0 ? left : Math.max(0, left + target.width - list.clientWidth);
    if (Math.abs(delta) > 1) list.scrollTo({ left: list.scrollLeft + delta, behavior: ui.reduced.matches ? 'instant' : 'smooth' });
  }
  function select(next, focus = false, manual = true, direction) {
    if (manual) manualUntil = Date.now() + manualPause;
    const selected = (next + tabs.length) % tabs.length;
    const tab = tabs[selected];
    if (focus) tab.focus({ preventScroll: true });
    if (selected === index) { schedule(manual ? manualPause : interval); return; }
    direction ??= selected > index ? 1 : -1;
    index = selected;
    const item = screens[tab.dataset.explore];
    tabs.forEach(button => { button.setAttribute('aria-selected', String(button === tab)); button.tabIndex = button === tab ? 0 : -1; });
    explorer.querySelector('[role=tabpanel]').setAttribute('aria-labelledby', tab.id);
    image.dataset.screenKey = tab.dataset.explore;
    ui.swapImage(image, tab.querySelector('img').src, item.alt, direction);
    explorer.querySelector('[data-explorer-title]').textContent = item.title;
    window.KotPunctuation.setText(copy, item.copy);
    number.textContent = String(index + 1).padStart(2, '0');
    // Automatic changes stay quiet for screen readers and never move keyboard focus.
    status.setAttribute('aria-live', manual ? 'polite' : 'off');
    status.textContent = 'Экран ' + (index + 1) + ' из ' + tabs.length;
    const facts = explorer.querySelector('[data-explorer-facts]');
    facts.replaceChildren(...item.facts.map(label => { const li = document.createElement('li'); li.textContent = label; return li; }));
    demo.firstChild.textContent = (item.label || 'Открыть демо') + ' ';
    demo.dataset.demoPage = item.page;
    ui.enterText(text); ui.enterText(number, direction * 13);
    updateTrack(); revealTab(tab); schedule(manual ? manualPause : interval);
  }
  tabs.forEach((tab, selected) => {
    tab.addEventListener('click', () => select(selected));
    tab.addEventListener('keydown', event => {
      let next;
      if (['ArrowRight', 'ArrowDown'].includes(event.key)) next = selected + 1;
      if (['ArrowLeft', 'ArrowUp'].includes(event.key)) next = selected - 1;
      if (event.key === 'Home') next = 0;
      if (event.key === 'End') next = tabs.length - 1;
      if (next !== undefined) { event.preventDefault(); select(next, true); }
    });
  });
  explorer.querySelector('[data-explore-prev]').addEventListener('click', () => select(index - 1, false, true, -1));
  explorer.querySelector('[data-explore-next]').addEventListener('click', () => select(index + 1, false, true, 1));
  ui.swipe(explorer.querySelector('[data-zoom]'), direction => select(index + direction, false, true, direction));
  explorer.addEventListener('pointerdown', event => {
    if (!event.isPrimary || !event.target.closest('[data-explore],[data-explore-prev],[data-explore-next],[data-zoom]')) return;
    heldPointer = event.pointerId;
    pauseAfterInteraction();
  }, { passive: true });
  function releasePointer(event) {
    if (event.pointerId !== heldPointer) return;
    heldPointer = null;
    pauseAfterInteraction();
  }
  addEventListener('pointerup', releasePointer, { passive: true });
  addEventListener('pointercancel', releasePointer, { passive: true });
  list.addEventListener('wheel', pauseAfterInteraction, { passive: true });
  list.addEventListener('focusin', pauseAfterInteraction);
  addEventListener('kot:modal', event => { modalOpen = event.detail.open; schedule(); });
  addEventListener('kot:motion', () => schedule());
  document.addEventListener('visibilitychange', () => { heldPointer = null; schedule(); });
  addEventListener('pagehide', () => { disposed = true; suspend(); });
  addEventListener('pageshow', () => { disposed = false; schedule(); });
  schedule();

  // Reserve space for the longest caption at this width so that switching does not move controls.
  let measuredWidth = 0;
  function reserveCopyHeight(force = false) {
    const width = copy.getBoundingClientRect().width;
    if (width < 1 || (!force && Math.abs(width - measuredWidth) < 1)) return;
    measuredWidth = width;
    const measure = copy.cloneNode(false);
    measure.removeAttribute('data-explorer-copy');
    measure.setAttribute('aria-hidden', 'true');
    measure.style.cssText = `position:absolute;visibility:hidden;pointer-events:none;width:${width}px;min-height:0;margin:0;top:0;left:0;`;
    text.append(measure);
    let maximum = 0;
    for (const item of Object.values(screens)) { window.KotPunctuation.setText(measure, item.copy); maximum = Math.max(maximum, measure.getBoundingClientRect().height); }
    measure.remove();
    copy.style.minHeight = Math.ceil(maximum) + 'px';
  }
  const resize = new ResizeObserver(() => reserveCopyHeight());
  resize.observe(text);
  document.fonts?.ready.then(() => reserveCopyHeight(true));
  reserveCopyHeight();
  addEventListener('pagehide', () => resize.disconnect(), { once: true });

  const install = document.querySelector('#install');
  const links = [...document.querySelectorAll('.vw-header nav a[href^="#"]')];
  const navObserver = new IntersectionObserver(entries => {
    entries.forEach(entry => {
      const current = entry.isIntersecting ? '#install' : '#overview';
      links.forEach(link => {
        if (link.getAttribute('href') === current) link.setAttribute('aria-current', 'location');
        else link.removeAttribute('aria-current');
      });
    });
  }, { threshold: .12 });
  navObserver.observe(install);
  addEventListener('pagehide', () => navObserver.disconnect(), { once: true });

  // Touch browsers do not consistently expose :active before navigation.
  // Show the same small paw on contact without intercepting or delaying the link.
  document.querySelectorAll('.kot-download').forEach(link => {
    let pressTimer;
    function clearPress() { clearTimeout(pressTimer); link.classList.remove('is-pressed'); }
    link.addEventListener('pointerdown', event => {
      if (event.pointerType === 'mouse') return;
      clearTimeout(pressTimer);
      link.classList.add('is-pressed');
      pressTimer = setTimeout(clearPress, 850);
    }, { passive: true });
    link.addEventListener('pointerup', () => {
      if (!link.classList.contains('is-pressed')) return;
      clearTimeout(pressTimer);
      pressTimer = setTimeout(clearPress, 240);
    }, { passive: true });
    link.addEventListener('pointercancel', clearPress, { passive: true });
    addEventListener('pagehide', clearPress, { once: true });
  });
})();
