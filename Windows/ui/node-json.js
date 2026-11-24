'use strict';
window.KotNodeJson = ({getModel, rpc, openDialog, closeDialog, share, animate, cancelMotion}) => {
  const $ = id => document.getElementById(id);
  const menu = $('nodeMenu'), dialog = $('nodeJsonDialog'), code = $('nodeJsonCode');
  const views = [...dialog.querySelectorAll('[data-json-view]')];
  let target = null, view = 'parsed', revision = null, request = 0, copying = false;
  const tokens = getComputedStyle(document.documentElement);
  const reduce = matchMedia('(prefers-reduced-motion: reduce)');
  let menuGeneration = 0, menuClosing = false, menuExit = null;

  function trigger() {
    return target && $('serverList').querySelector('[data-node="'+CSS.escape(target.id)+'"] .qr-node');
  }
  function menuPose() {
    const style = getComputedStyle(menu);
    return {opacity:style.opacity, transform:style.transform};
  }
  function foldedMenu() {
    // Same compact popover family as the subscription picker, not a modal.
    return {opacity:0, transform:'translateY('+(menu.dataset.upwards === 'true' ? '4' : '-4')+'px) scale(.985)'};
  }
  function moveMenu(from, to) {
    return animate(menu, [from,to], {duration:parseFloat(tokens.getPropertyValue('--motion-exit'))});
  }
  function closeMenu({focus = false, immediate = false} = {}) {
    if (!menu.matches(':popover-open')) return;
    if (menuClosing) { if (immediate) menuExit?.(); return; }
    const from = menuPose(), generation = ++menuGeneration;
    menuClosing = true;
    menu.dataset.closing = 'true';
    trigger()?.setAttribute('aria-expanded','false');
    if (focus) trigger()?.focus({preventScroll:true});
    menu.inert = true;
    const finish = () => {
      if (generation !== menuGeneration) return;
      menuExit = null; menuClosing = false;
      cancelMotion(menu); menu.hidePopover();
      delete menu.dataset.closing; menu.inert = false;
    };
    menuExit = finish;
    const motion = immediate ? null : moveMenu(from, foldedMenu());
    if (motion) motion.finished.then(finish, finish); else finish();
  }
  function positionMenu(button) {
    // DOMRect is in rendered pixels, CSS inset/size are before root UI zoom.
    // Keep fractional coordinates; integer offsetWidth creates drift at 125%.
    const style = getComputedStyle(menu);
    const zoom = menu.getBoundingClientRect().width / parseFloat(style.width);
    const edge = parseFloat(tokens.getPropertyValue('--space-xs'));
    const gap = edge / 2;
    const width = innerWidth / zoom, height = innerHeight / zoom;
    menu.style.maxWidth = Math.max(0,width - edge * 2)+'px';
    menu.style.maxHeight = Math.max(0,height - edge * 2)+'px';
    const box = button.getBoundingClientRect(), size = menu.getBoundingClientRect();
    const below = height - box.bottom / zoom - gap - edge;
    const above = box.top / zoom - gap - edge;
    const upwards = size.height / zoom > below && above > below;
    menu.dataset.upwards = String(upwards);
    menu.style.left = Math.max(edge,Math.min(box.right / zoom - size.width / zoom,width - edge - size.width / zoom))+'px';
    menu.style.top = Math.max(edge,Math.min(upwards ? box.top / zoom - gap - size.height / zoom : box.bottom / zoom + gap,height - edge - size.height / zoom))+'px';
  }
  function showMenu(item, button, last = false) {
    const visible = menu.matches(':popover-open');
    const same = target?.id === item.id && target?.subscriptionId === getModel().activeSubscription;
    if (visible && same && !menuClosing) { closeMenu({focus:true}); return; }
    // Capture before cancellation, so reversing an exit starts at its painted pose.
    const from = visible && same ? menuPose() : null;
    trigger()?.setAttribute('aria-expanded','false');
    ++menuGeneration; menuClosing = false; menuExit = null;
    cancelMotion(menu); menu.inert = false; delete menu.dataset.closing;
    target = {id:item.id, subscriptionId:getModel().activeSubscription};
    $('nodeMenuQr').disabled = !!item.providerRouting;
    $('nodeMenuQr').title = item.providerRouting ? 'QR не сохраняет правила подписки.' : '';
    if (!visible) menu.showPopover();
    if (!from) positionMenu(button);
    button.setAttribute('aria-expanded','true');
    moveMenu(from || foldedMenu(), {opacity:1, transform:'translateY(0) scale(1)'});
    (last && !$('nodeMenuQr').disabled ? $('nodeMenuQr') : $('nodeMenuJson')).focus({preventScroll:true});
  }
  menu.addEventListener('toggle', () => {
    if (menu.matches(':popover-open')) return;
    ++menuGeneration; menuExit = null; menuClosing = false;
    cancelMotion(menu); delete menu.dataset.closing; menu.inert = false;
    trigger()?.setAttribute('aria-expanded','false');
  });
  function dismissOnMove(event) {
    if (!menu.contains(event.target)) closeMenu({immediate:true});
  }
  addEventListener('resize', () => closeMenu({immediate:true}));
  document.addEventListener('scroll',dismissOnMove,true);
  document.addEventListener('pointerdown', event => {
    if (!menu.contains(event.target) && !event.target.closest('.qr-node')) closeMenu();
  });
  document.addEventListener('visibilitychange', () => { if (document.hidden) closeMenu({immediate:true}); });
  reduce.addEventListener('change', () => { if (reduce.matches) { cancelMotion(menu); menuExit?.(); } });
  function dismissMenu() {
    closeMenu({focus:true});
  }
  menu.addEventListener('keydown', event => {
    if (event.key === 'Tab') { dismissMenu(); return; }
    if (event.key === 'Escape') {
      event.preventDefault(); dismissMenu(); return;
    }
    if (['ArrowDown','ArrowUp','Home','End'].includes(event.key)) {
      event.preventDefault();
      const items = [...menu.querySelectorAll('[role=menuitem]:not(:disabled)')];
      const index = items.indexOf(document.activeElement);
      items[event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1 :
        (index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length]?.focus({preventScroll:true});
    }
  });
  menu.addEventListener('focusout', event => {
    if (event.relatedTarget && !menu.contains(event.relatedTarget) && !event.relatedTarget.closest('.qr-node')) closeMenu();
  });
  $('nodeMenuQr').onclick = () => { dismissMenu(); share(target.id); };
  $('nodeMenuJson').onclick = () => { dismissMenu(); openDialog(dialog); load('parsed'); };

  function highlight(text) {
    const fragment = document.createDocumentFragment();
    const tokens = /"(?:\\.|[^"\\])*"(?:\s*:)?|\b(?:true|false|null)\b|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g;
    let end = 0;
    for (const match of text.matchAll(tokens)) {
      const token = match[0];
      fragment.append(text.slice(end,match.index));
      if (token.startsWith('"') && !token.endsWith(':')) fragment.append(token);
      else {
        const span = document.createElement('span');
        span.className = token.endsWith(':') ? 'json-key' : 'json-literal';
        span.textContent = token; fragment.append(span);
      }
      end = match.index + token.length;
    }
    fragment.append(text.slice(end)); code.replaceChildren(fragment);
  }
  function controls() {
    $('copyNodeJson').disabled = !revision;
    $('copyNodeJson').setAttribute('aria-disabled', String(!revision || copying));
    $('copyNodeJson').setAttribute('aria-busy', String(copying));
  }
  function clear() {
    ++request; revision = null; copying = false;
    code.replaceChildren(); $('nodeJsonFeedback').textContent = ''; controls();
  }
  function current(ticket) { return ticket === request && dialog.open && !dialog.dataset.closing; }
  async function load(nextView) {
    clear(); view = nextView;
    const ticket = request;
    views.forEach(b => b.setAttribute('aria-pressed',b.dataset.jsonView === view));
    views[0].parentElement.dataset.selected = view === 'parsed' ? '0' : '1';
    $('nodeJsonNote').textContent = 'Загрузка…';
    $('nodeJsonScroll').setAttribute('aria-busy','true');
    $('nodeJsonScroll').scrollTo(0,0);
    try {
      // Full configuration is requested only inside this explicitly opened dialog.
      // Never put it in the shared model, storage, notices or error details.
      const data = await rpc('readNodeJson',{...target, view, full:true});
      if (!current(ticket)) return;
      $('nodeJsonNote').textContent = data.note;
      if (data.text && data.revision) {
        highlight(data.text); revision = data.revision;
      }
    } catch {
      if (current(ticket)) $('nodeJsonNote').textContent = 'Не удалось прочитать outbound из сохранённых данных.';
    } finally {
      if (current(ticket)) {
        $('nodeJsonScroll').setAttribute('aria-busy','false'); controls();
      }
    }
  }
  async function copy() {
    if (!revision || copying) return;
    const ticket = request;
    copying = true; $('nodeJsonFeedback').textContent = ''; controls();
    try {
      // The host validates the displayed revision before writing the whole JSON.
      await rpc('copyNodeJson',{...target, view, full:true, revision});
      if (current(ticket)) $('nodeJsonFeedback').textContent = 'Скопировано';
    } catch {
      if (current(ticket)) {
        $('nodeJsonFeedback').textContent = 'Не скопировано';
        $('nodeJsonNote').textContent = 'Копирование не удалось. Если сервер обновился, откройте JSON заново.';
        $('nodeJsonScroll').scrollTo(0,0);
      }
    } finally {
      if (current(ticket)) { copying = false; controls(); }
    }
  }
  for (const button of views) button.onclick = () => {
    if (button.dataset.jsonView !== view) load(button.dataset.jsonView);
  };
  $('copyNodeJson').onclick = copy;
  code.addEventListener('dragstart', e => e.preventDefault());
  $('closeNodeJson').onclick = () => { clear(); closeDialog(dialog); };
  dialog.addEventListener('close', () => { if (!dialog.open) clear(); });
  function sync() {
    if (!target || !(dialog.open || menu.matches(':popover-open'))) return;
    const model = getModel();
    if (model.activeSubscription === target.subscriptionId && model.nodes.some(n => n.id === target.id)) return;
    closeMenu({immediate:true});
    if (dialog.open) { clear(); closeDialog(dialog); }
    target = null;
  }
  return {showMenu, sync, closeMenu, menuOpenFor:id => target?.id === id && !menuClosing && menu.matches(':popover-open')};
};
