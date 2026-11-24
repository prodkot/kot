'use strict';
(() => {
  const section = document.querySelector('#install');
  const scene = section?.querySelector('.vw-kot-art');
  const pet = scene?.querySelector('.kot-pet');
  const svg = scene?.querySelector('.kot-portrait');
  const ball = scene?.querySelector('.kot-ball-position');
  const spin = scene?.querySelector('.kot-ball-spin');
  const thread = scene?.querySelector('.kot-thread');
  const original = section?.querySelector('.install-title-default');
  const secret = section?.querySelector('.install-title-secret');
  const status = section?.querySelector('[data-cat-status]');
  if (!pet || !ball || !spin || !thread || !original || !secret || !status) return;

  const reduced = window.KotUI?.reduced || matchMedia('(prefers-reduced-motion: reduce)');
  const home = { x: 84, y: 310 };
  const position = { ...home };
  let target = { ...home };
  let angle = 0, frame = 0, lastFrame = 0, restoreTimer = 0;
  let visible = false, modalOpen = false;
  let touch = null, ignoreSceneClickUntil = 0;
  const clamp = (value, low, high) => Math.max(low, Math.min(high, value));
  const active = () => visible && !document.hidden && !modalOpen;

  function draw() {
    ball.setAttribute('transform', `translate(${position.x.toFixed(2)} ${position.y.toFixed(2)})`);
    spin.setAttribute('transform', `rotate(${angle.toFixed(2)})`);
    const side = position.x < 154 ? -1 : 1;
    thread.setAttribute('d', `M154 284C${154 + side * 26} 326 ${
      (position.x - side * 48).toFixed(2)} ${(position.y + 8).toFixed(2)} ${
      (position.x - side * 18).toFixed(2)} ${position.y.toFixed(2)}`);
  }

  function stop() {
    cancelAnimationFrame(frame);
    frame = 0;
    lastFrame = 0;
  }

  function tick(now) {
    frame = 0;
    if (!active() || reduced.matches) { lastFrame = 0; return; }
    const elapsed = lastFrame ? clamp(now - lastFrame, 1, 40) : 16;
    lastFrame = now;
    const ease = 1 - Math.exp(-elapsed / 115);
    const dx = (target.x - position.x) * ease;
    position.x += dx;
    position.y += (target.y - position.y) * ease;
    angle = (angle + dx / 18 * 180 / Math.PI) % 360;
    draw();
    if (Math.abs(target.x - position.x) + Math.abs(target.y - position.y) > .12) {
      frame = requestAnimationFrame(tick);
    } else {
      position.x = target.x;
      position.y = target.y;
      lastFrame = 0;
      draw();
    }
  }

  function moveBall(x, y = 310, immediate = false) {
    target = { x: clamp(x, 38, 562), y: clamp(y, 302, 318) };
    if (immediate || reduced.matches || !active()) {
      stop();
      angle = (angle + (target.x - position.x) / 18 * 180 / Math.PI) % 360;
      Object.assign(position, target);
      draw();
    } else if (!frame) {
      frame = requestAnimationFrame(tick);
    }
  }

  function point(event) {
    const bounds = svg.getBoundingClientRect();
    return { x: (event.clientX - bounds.left) / bounds.width * 600,
      y: (event.clientY - bounds.top) / bounds.height * 360 };
  }

  function restore() {
    clearTimeout(restoreTimer);
    restoreTimer = 0;
    section.classList.remove('is-purring');
    original.removeAttribute('aria-hidden');
    secret.setAttribute('aria-hidden', 'true');
    status.textContent = '';
  }

  function purr() {
    if (!active()) return;
    const wasPurring = section.classList.contains('is-purring');
    section.classList.add('is-purring');
    original.setAttribute('aria-hidden', 'true');
    secret.removeAttribute('aria-hidden');
    if (!wasPurring) status.textContent = 'Мяу, и интернет у тебя в руках.';
    clearTimeout(restoreTimer);
    restoreTimer = setTimeout(restore, 5000);
  }

  pet.addEventListener('click', () => {
    purr();
    moveBall(position.x < 300 ? 115 : 500);
  });

  scene.addEventListener('pointermove', event => {
    if (!active() || reduced.matches || !event.isPrimary || event.pointerType === 'touch') return;
    const local = point(event);
    moveBall(local.x, 310 + (local.y - 180) * .035);
  }, { passive: true });
  scene.addEventListener('pointerleave', event => {
    if (event.pointerType !== 'touch') moveBall(home.x, home.y);
  }, { passive: true });

  // Only a deliberate tap moves the yarn. A vertical scroll is never captured.
  scene.addEventListener('pointerdown', event => {
    if (!event.isPrimary || event.pointerType === 'mouse') return;
    touch = { id: event.pointerId, x: event.clientX, y: event.clientY };
  }, { passive: true });
  scene.addEventListener('pointercancel', () => { touch = null; }, { passive: true });
  scene.addEventListener('pointerup', event => {
    if (!touch || touch.id !== event.pointerId) return;
    const moved = Math.hypot(event.clientX - touch.x, event.clientY - touch.y);
    touch = null;
    if (moved > 12 || pet.contains(event.target)) return;
    ignoreSceneClickUntil = performance.now() + 700;
    rollTo(event);
  }, { passive: true });
  scene.addEventListener('click', event => {
    if (pet.contains(event.target) || event.detail === 0 || performance.now() < ignoreSceneClickUntil) return;
    // Mouse and pen clicks on the yarn give it a small extra nudge.
    rollTo(event);
  });

  function rollTo(event) {
    if (!active()) return;
    const local = point(event);
    const x = Math.abs(local.x - position.x) < 35
      ? position.x + (position.x < 300 ? 155 : -155) : local.x;
    moveBall(x, 310);
  }

  function syncActivity() {
    scene.classList.toggle('kot-scene-paused', !active());
    if (!active()) {
      stop();
      touch = null;
      restore();
      moveBall(home.x, home.y, true);
    }
  }

  scene.classList.add('kot-interactive');
  if ('IntersectionObserver' in window) {
    const observer = new IntersectionObserver(entries => {
      visible = entries[0].isIntersecting;
      syncActivity();
    }, { threshold: 0 });
    observer.observe(scene);
  } else {
    visible = true;
  }
  document.addEventListener('visibilitychange', syncActivity);
  addEventListener('kot:modal', event => { modalOpen = event.detail.open; syncActivity(); });
  reduced.addEventListener('change', () => { stop(); moveBall(home.x, home.y, true); });
  addEventListener('pagehide', () => { stop(); restore(); });
  addEventListener('pageshow', syncActivity);
  syncActivity();
  draw();
})();
