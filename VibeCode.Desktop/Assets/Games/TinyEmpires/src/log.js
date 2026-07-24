// log.js — the little event feed in the top-right corner. Deliberately knows
// nothing about game state so any module can shout without an import cycle.

let host = null;
const MAX_LINES = 6;
const LIFETIME = 7000;

function el() {
  if (!host) host = document.getElementById('log');
  return host;
}

/**
 * Push one line into the feed.
 * @param {string} text
 * @param {'info'|'good'|'war'|'tech'} kind  tints the left edge
 */
export function logEvent(text, kind = 'info') {
  const box = el();
  if (!box) return;

  const line = document.createElement('div');
  line.className = 'logline' + (kind !== 'info' ? ' ' + kind : '');
  line.textContent = text;
  box.appendChild(line);

  while (box.children.length > MAX_LINES) box.removeChild(box.firstChild);

  setTimeout(() => {
    line.classList.add('fade');
    setTimeout(() => line.remove(), 600);
  }, LIFETIME);
}

export function clearLog() {
  const box = el();
  if (box) box.innerHTML = '';
}
