'use strict';
(() => {
  const body = document.body;
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');
  const activeAnimations = new Set();
  const textAnimations = new WeakMap();
  const imageChanges = new WeakMap();

  function animate(element, keyframes, options = {}) {
    if (reduced.matches || !element?.animate) return null;
    const animation = element.animate(keyframes, {
      duration: 360,
      easing: 'cubic-bezier(.22,.75,.25,1)',
      ...options
    });
    activeAnimations.add(animation);
    animation.finished.then(
      () => activeAnimations.delete(animation),
      () => activeAnimations.delete(animation)
    );
    return animation;
  }

  function enterText(element, distance = 9) {
    textAnimations.get(element)?.cancel();
    const animation = animate(element, [
      { opacity: .25, transform: `translateY(${distance}px)` },
      { opacity: 1, transform: 'translateY(0)' }
    ], { duration: 330 });
    if (animation) textAnimations.set(element, animation);
  }

  function swapImage(image, source, alt, direction = 1) {
    const previous = imageChanges.get(image);
    previous?.animations.forEach(a => a?.cancel());
    previous?.ghost?.remove();
    const state = { ghost: null, animations: [] };
    imageChanges.set(image, state);
    if (image.getAttribute('src') === source) { image.alt = alt; return; }
    if (!reduced.matches && image.complete && image.naturalWidth) {
      const ghost = image.cloneNode(false);
      [...ghost.attributes].forEach(attribute => {
        if (attribute.name.startsWith('data-') || attribute.name === 'id') ghost.removeAttribute(attribute.name);
      });
      ghost.className = 'transition-snapshot';
      ghost.setAttribute('aria-hidden', 'true');
      ghost.alt = '';
      ghost.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;object-fit:contain;pointer-events:none;';
      image.parentElement.append(ghost);
      state.ghost = ghost;
    }
    image.src = source;
    image.alt = alt;
    const decoded = image.decode ? image.decode() : Promise.resolve();
    decoded.then(() => {
      if (imageChanges.get(image) !== state) return;
      if (!state.ghost || reduced.matches) { state.ghost?.remove(); return; }
      state.animations = [
        animate(image, [
          { opacity: .2, transform: `translateX(${direction * 14}px) scale(.995)` },
          { opacity: 1, transform: 'translateX(0) scale(1)' }
        ], { duration: 430 }),
        animate(state.ghost, [
          { opacity: 1, transform: 'translateX(0)' },
          { opacity: 0, transform: `translateX(${-direction * 12}px)` }
        ], { duration: 360 })
      ];
      const animation = state.animations[1];
      if (animation) animation.finished.then(() => state.ghost?.remove(), () => state.ghost?.remove());
      else state.ghost.remove();
    }).catch(() => state.ghost?.remove());
  }

  function tabIndicator(list, kind = 'line') {
    const track = document.createElement('span');
    track.className = 'tab-track tab-track-' + kind;
    track.setAttribute('aria-hidden', 'true');
    track.hidden = true;
    list.append(track);
    function update(moving = true) {
      const tab = list.querySelector('[role=tab][aria-selected=true]');
      if (!tab) return;
      // Layout coordinates stay stable while the entire strip enters or scrolls.
      // Bounding rectangles include entrance transforms and leave a stale offset.
      track.style.transition = moving && !reduced.matches ? '' : 'none';
      track.style.width = tab.offsetWidth + 'px';
      track.style.transform = `translateX(${tab.offsetLeft}px)`;
      if (kind === 'tile') {
        track.style.top = tab.offsetTop + 'px';
        track.style.height = tab.offsetHeight + 'px';
      }
      track.hidden = false;
      list.classList.add('track-ready');
    }
    const observer = new ResizeObserver(() => update(false));
    observer.observe(list);
    list.querySelectorAll('[role=tab]').forEach(tab => observer.observe(tab));
    document.fonts?.ready.then(() => update(false));
    update(false);
    addEventListener('pagehide', () => observer.disconnect(), { once: true });
    return update;
  }

  function swipe(element, select) {
    let start = null, suppressUntil = 0;
    element.classList.add('swipe-surface');
    element.addEventListener('pointerdown', event => {
      if (event.pointerType === 'mouse' || !event.isPrimary) return;
      start = { x: event.clientX, y: event.clientY, time: performance.now(), id: event.pointerId };
    }, { passive: true });
    element.addEventListener('pointerup', event => {
      if (!start || event.pointerId !== start.id) return;
      const dx = event.clientX - start.x, dy = event.clientY - start.y;
      const valid = Math.abs(dx) >= 38 && Math.abs(dx) > Math.abs(dy) * 1.35 && performance.now() - start.time < 1000;
      start = null;
      if (!valid) return;
      suppressUntil = performance.now() + 450;
      if (event.cancelable) event.preventDefault();
      select(dx < 0 ? 1 : -1);
    });
    element.addEventListener('pointercancel', () => { start = null; }, { passive: true });
    element.addEventListener('click', event => {
      if (performance.now() < suppressUntil) { event.preventDefault(); event.stopImmediatePropagation(); }
    }, true);
    element.addEventListener('keydown', event => {
      if (!['ArrowLeft', 'ArrowRight'].includes(event.key)) return;
      event.preventDefault();
      select(event.key === 'ArrowRight' ? 1 : -1);
    });
  }

  window.KotUI = Object.freeze({ animate, enterText, swapImage, tabIndicator, swipe, reduced });
  body.classList.add('enhanced');
  Promise.race([document.fonts?.ready || Promise.resolve(), new Promise(resolve => setTimeout(resolve, 500))])
    .then(() => requestAnimationFrame(() => body.classList.add('entered')));

  const revealObserver = 'IntersectionObserver' in window ? new IntersectionObserver(entries => {
    entries.forEach(entry => {
      if (entry.isIntersecting) { entry.target.classList.add('is-visible'); revealObserver.unobserve(entry.target); }
    });
  }, { threshold: .08 }) : null;
  if (!reduced.matches && revealObserver) {
    body.classList.add('motion-ready');
    document.querySelectorAll('.reveal').forEach(element => revealObserver.observe(element));
  }
  reduced.addEventListener('change', () => {
    if (reduced.matches) {
      activeAnimations.forEach(animation => animation.cancel());
      body.classList.remove('motion-ready');
      revealObserver?.disconnect();
      document.querySelectorAll('.transition-snapshot').forEach(element => element.remove());
    }
    dispatchEvent(new CustomEvent('kot:motion', { detail: { reduced: reduced.matches } }));
  });

  const modalStates = new WeakMap();
  function syncLock() { body.classList.toggle('modal-open', !!document.querySelector('dialog:modal')); }
  function openModal(dialog, trigger) {
    document.querySelectorAll('dialog:modal').forEach(other => {
      if (other !== dialog) { modalStates.get(other)?.animation?.cancel(); other.close(); }
    });
    modalStates.get(dialog)?.animation?.cancel();
    const state = { trigger, closing: false };
    modalStates.set(dialog, state);
    if (!dialog.open) dialog.showModal();
    syncLock();
    state.animation = animate(dialog, [
      { opacity: .25, transform: 'translateY(10px) scale(.985)' },
      { opacity: 1, transform: 'translateY(0) scale(1)' }
    ], { duration: 280 });
    dialog.querySelector('[data-close-demo],[data-close-image]')?.focus({ preventScroll: true });
    dispatchEvent(new CustomEvent('kot:modal', { detail: { open: true } }));
  }
  function closeModal(dialog) {
    if (!dialog?.open) return;
    const state = modalStates.get(dialog) || {};
    if (state.closing) return;
    state.closing = true;
    state.animation?.cancel();
    modalStates.set(dialog, state);
    const finish = () => {
      if (modalStates.get(dialog) !== state) return;
      dialog.close(); syncLock();
      state.trigger?.focus({ preventScroll: true });
      dispatchEvent(new CustomEvent('kot:modal', { detail: { open: false } }));
    };
    state.animation = animate(dialog, [
      { opacity: 1, transform: 'translateY(0) scale(1)' },
      { opacity: 0, transform: 'translateY(5px) scale(.99)' }
    ], { duration: 160, easing: 'ease-in' });
    if (state.animation) state.animation.finished.then(finish, finish);
    else finish();
  }
  document.querySelectorAll('dialog').forEach(dialog => {
    dialog.addEventListener('cancel', event => { event.preventDefault(); closeModal(dialog); });
    dialog.addEventListener('click', event => {
      if (event.target !== dialog) return;
      const r = dialog.getBoundingClientRect();
      if (event.clientX < r.left || event.clientX > r.right || event.clientY < r.top || event.clientY > r.bottom) closeModal(dialog);
    });
  });

  const dialog = document.querySelector('#app-demo');
  const frame = document.querySelector('#client-demo');
  const loading = document.querySelector('[data-demo-loading]');
  const message = document.querySelector('[data-demo-message]');
  const retry = document.querySelector('[data-retry-demo]');
  const payload = document.querySelector('#kot-demo-payload');
  let loaded = false, ready = false, pendingPage = 'home', loadTimer;
  const control = (action, page) => frame.contentWindow?.postMessage({ kind: 'kot-demo-control', action, page }, '*');
  function loadDemo() {
    loaded = true; ready = false;
    loading.hidden = false; loading.classList.remove('has-error');
    retry.hidden = true; window.KotPunctuation.setText(message, 'Открываем демо…');
    clearTimeout(loadTimer);
    loadTimer = setTimeout(() => {
      if (ready) return;
      loading.classList.add('has-error');
      window.KotPunctuation.setText(message, 'Не удалось открыть демо. Можно попробовать ещё раз.');
      retry.hidden = false;
    }, 12000);
    if (payload) {
      const bytes = Uint8Array.from(atob(payload.textContent.trim()), c => c.charCodeAt(0));
      frame.srcdoc = new TextDecoder().decode(bytes);
    } else frame.src = frame.dataset.src;
  }
  document.querySelectorAll('[data-open-demo]').forEach(button => button.addEventListener('click', () => {
    pendingPage = button.dataset.demoPage || 'home';
    if (!loaded) loadDemo();
    if (ready) { control('navigate', pendingPage); pendingPage = null; }
    openModal(dialog, button);
  }));
  document.querySelectorAll('[data-close-demo]').forEach(button => button.addEventListener('click', () => closeModal(dialog)));
  document.querySelector('[data-reset-demo]')?.addEventListener('click', () => { pendingPage = 'home'; loadDemo(); });
  retry?.addEventListener('click', loadDemo);
  addEventListener('message', event => {
    if (event.source !== frame?.contentWindow || event.data?.kind !== 'kot-demo') return;
    if (event.data.ready) {
      ready = true; loading.hidden = true; clearTimeout(loadTimer);
      if (pendingPage) { control('navigate', pendingPage); pendingPage = null; }
    }
    if (event.data.close) closeModal(dialog);
  });

  const zoom = document.querySelector('#image-window');
  const zoomImage = zoom.querySelector('.image-canvas img');
  const imageSize = zoom.querySelector('[data-image-size]');
  const canvas = zoom.querySelector('.image-canvas');
  document.querySelectorAll('[data-zoom]').forEach(button => button.addEventListener('click', () => {
    const original = button.querySelector('img:not([aria-hidden=true])');
    zoomImage.src = original.src; zoomImage.alt = original.alt;
    window.KotPunctuation.setText(zoom.querySelector('[data-image-title]'), original.alt);
    const download = zoom.querySelector('[data-image-download]');
    download.href = original.src;
    download.download = 'kot-' + (original.dataset.screenKey || 'home') + '.png';
    zoom.classList.remove('is-actual'); imageSize.setAttribute('aria-pressed', 'false'); imageSize.textContent = '1:1';
    canvas.scrollTo(0, 0);
    openModal(zoom, button);
  }));
  imageSize.addEventListener('click', () => {
    const actual = zoom.classList.toggle('is-actual');
    imageSize.setAttribute('aria-pressed', String(actual));
    imageSize.textContent = actual ? 'Вписать' : '1:1';
    canvas.scrollTo(0, 0);
  });
  zoom.querySelector('[data-close-image]').addEventListener('click', () => closeModal(zoom));
  addEventListener('pagehide', () => { clearTimeout(loadTimer); revealObserver?.disconnect(); activeAnimations.forEach(animation => animation.cancel()); }, { once: true });
})();
