'use strict';
(() => {
  // Only sentence periods and separators. Decimals, filenames and ellipses stay text.
  const marks = /(?<!\.)\.(?![\p{L}\p{N}_.])|·/gu;
  function setText(element, value) {
    const fragment = document.createDocumentFragment();
    let previous = 0;
    marks.lastIndex = 0;
    for (const match of value.matchAll(marks)) {
      fragment.append(document.createTextNode(value.slice(previous, match.index)));
      const dot = document.createElement('span');
      dot.className = 'text-dot' + (match[0] === '·' ? ' text-dot-middle' : '');
      dot.textContent = match[0];
      fragment.append(dot);
      previous = match.index + match[0].length;
    }
    fragment.append(document.createTextNode(value.slice(previous)));
    element.replaceChildren(fragment);
  }
  window.KotPunctuation = Object.freeze({ setText });
})();
