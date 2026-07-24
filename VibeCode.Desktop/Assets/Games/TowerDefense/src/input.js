// Mouse only for play. The host still synthesises a "p" keydown when the game
// window is minimised, so pause stays on the keyboard for that contract alone.
import { cellIndex } from './path.js';
import { S } from './state.js';
import { view, toWorld } from './view.js';

const DRAG_PX = 6;

export function bindInput(h) {
  const canvas = view.canvas;
  let rightDown = null;   // { x, y, i, dragged }

  const cellAt = ev => {
    const rect = canvas.getBoundingClientRect();
    const p = toWorld(ev.clientX - rect.left, ev.clientY - rect.top);
    return cellIndex(p.x, p.y);
  };

  canvas.addEventListener('mousemove', ev => {
    S.hoverCell = cellAt(ev);
    if (rightDown && !rightDown.dragged) {
      const dx = ev.clientX - rightDown.x, dy = ev.clientY - rightDown.y;
      if (dx * dx + dy * dy > DRAG_PX * DRAG_PX) rightDown.dragged = true;
    }
  });
  canvas.addEventListener('mouseleave', () => { S.hoverCell = -1; });
  canvas.addEventListener('contextmenu', ev => ev.preventDefault());

  canvas.addEventListener('mousedown', ev => {
    if (!h.isLive()) return;
    const i = cellAt(ev);
    S.hoverCell = i;

    // Right button: wait for mouseup so a drag is not treated as "inspect".
    if (ev.button === 2) {
      rightDown = { x: ev.clientX, y: ev.clientY, i, dragged: false };
      return;
    }

    if (ev.button === 0 && i >= 0) h.onCell(i);
  });

  window.addEventListener('mouseup', ev => {
    if (ev.button !== 2 || !rightDown) return;
    const press = rightDown;
    rightDown = null;
    if (!h.isLive()) return;
    // Dragged right-click = cancel / do nothing (user was probably panning intent).
    if (press.dragged) return;
    h.onRightClick?.(press.i, ev.clientX, ev.clientY);
  });

  // Host pause contract only — no other gameplay hotkeys.
  window.addEventListener('keydown', ev => {
    if (ev.key.toLowerCase() === 'p') {
      ev.preventDefault();
      h.onPause();
    }
  });
}
