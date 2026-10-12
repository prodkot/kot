// The native host supplies one theme and one 1-in-100 draw for the entire launch.
// Apply before styles and body parsing so light startup never paints a dark frame.
(() => {
  'use strict';
  const root = document.documentElement;
  root.classList.toggle('theme-light', window.kotStartup?.theme === 'light');
  root.classList.toggle('startup-spin', window.kotStartup?.spin === true);
})();
