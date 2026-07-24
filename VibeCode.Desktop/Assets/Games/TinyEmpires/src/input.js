// input.js — mouse and keyboard. Left drag selects, right click orders, middle
// drag or WASD pans, wheel zooms. Actions that belong to the app shell (pause,
// restart) are injected so this module never imports main.js back.

import { W, H, clamp, idx, inBounds, cheb } from './core.js';
import { world, game, camera, markDirty, cityAt } from './store.js';
import { BLD } from './data.js';
import { canPlace } from './economy.js';
import { addBuilding } from './entities.js';
import { orderMove, orderAttack, orderFound, orderFortify, canSettle } from './military.js';
import { screenToWorld, zoomAt, clampCamera, hover, dragBox } from './render.js?v=unit30';
import {
  refreshSelection, refreshRails, setPlace, toggleTech, techOpen, closeTech,
  togglePanel, closePanels,
} from './ui.js';
import { logEvent } from './log.js';

const keys = new Set();
let panning = false, panLast = null;
let dragging = false, dragStart = null, dragMoved = false;
let actions = {};
let canvas = null;

const DRAG_THRESHOLD = 5;   // css pixels before a click becomes a box-select

export function initInput(gameCanvas, minimap, shellActions) {
  canvas = gameCanvas;
  actions = shellActions || {};

  canvas.addEventListener('mousedown', onMouseDown);
  canvas.addEventListener('mousemove', onMouseMove);
  window.addEventListener('mouseup', onMouseUp);
  canvas.addEventListener('wheel', onWheel, { passive: false });
  canvas.addEventListener('contextmenu', e => e.preventDefault());
  canvas.addEventListener('mouseleave', () => { hover.x = -1; hover.y = -1; });

  minimap.addEventListener('mousedown', onMinimap);
  minimap.addEventListener('mousemove', e => { if (e.buttons & 1) onMinimap(e); });
  minimap.addEventListener('contextmenu', e => e.preventDefault());

  window.addEventListener('keydown', onKeyDown);
  window.addEventListener('keyup', e => keys.delete(e.key.toLowerCase()));
  window.addEventListener('blur', () => keys.clear());
}

const playing = () => game.mode === 'playing';

/* ── pointer ──────────────────────────────────────────────────────────────── */

function tileUnder(e) {
  const r = canvas.getBoundingClientRect();
  const w = screenToWorld(e.clientX - r.left, e.clientY - r.top);
  return { x: Math.floor(w.x), y: Math.floor(w.y), fx: w.x, fy: w.y };
}

function onMouseDown(e) {
  if (!playing()) return;
  canvas.focus();
  const t = tileUnder(e);

  if (e.button === 1) {                      // middle drag pans
    panning = true;
    panLast = { x: e.clientX, y: e.clientY };
    document.getElementById('stage').classList.add('panning');
    e.preventDefault();
    return;
  }

  if (e.button === 2) { onRightClick(t); return; }

  if (e.button === 0) {
    if (game.place) { tryPlace(t); return; }
    dragging = true; dragMoved = false;
    const r = canvas.getBoundingClientRect();
    dragStart = { x: e.clientX - r.left, y: e.clientY - r.top, tile: t };
    dragBox.x0 = dragBox.x1 = dragStart.x;
    dragBox.y0 = dragBox.y1 = dragStart.y;
  }
}

function onMouseMove(e) {
  const r = canvas.getBoundingClientRect();
  const t = tileUnder(e);

  if (inBounds(t.x, t.y)) {
    hover.x = t.x; hover.y = t.y;
    if (game.place) {
      const chk = canPlace(game.empires[game.player], game.place, t.x, t.y);
      hover.ok = chk.ok; hover.why = chk.why || '';
    }
  } else { hover.x = -1; hover.y = -1; }

  if (panning && panLast) {
    camera.x -= (e.clientX - panLast.x) / pxPerTile();
    camera.y -= (e.clientY - panLast.y) / pxPerTile();
    panLast = { x: e.clientX, y: e.clientY };
    clampCamera();
    return;
  }

  if (dragging && dragStart) {
    dragBox.x1 = e.clientX - r.left;
    dragBox.y1 = e.clientY - r.top;
    if (!dragMoved &&
        (Math.abs(dragBox.x1 - dragBox.x0) > DRAG_THRESHOLD ||
         Math.abs(dragBox.y1 - dragBox.y0) > DRAG_THRESHOLD)) {
      dragMoved = true;
      dragBox.on = true;
    }
  }
}

/** CSS pixels per tile, for turning mouse deltas into camera movement. */
function pxPerTile() {
  const r = canvas.getBoundingClientRect();
  const w = screenToWorld(0, 0), w2 = screenToWorld(100, 0);
  const per = 100 / Math.max(1e-6, w2.x - w.x);
  return per;
}

function onMouseUp(e) {
  if (panning && e.button === 1) {
    panning = false; panLast = null;
    document.getElementById('stage').classList.remove('panning');
    return;
  }
  if (!dragging || e.button !== 0) return;
  dragging = false;
  dragBox.on = false;

  if (!playing()) return;
  if (dragMoved) { boxSelect(); return; }
  clickSelect(dragStart.tile);
}

function onWheel(e) {
  e.preventDefault();
  if (!playing()) return;
  const r = canvas.getBoundingClientRect();
  zoomAt(e.deltaY < 0 ? 1 : -1, e.clientX - r.left, e.clientY - r.top);
}

function onMinimap(e) {
  if (!playing()) return;
  const r = e.currentTarget.getBoundingClientRect();
  camera.x = clamp(((e.clientX - r.left) / r.width) * W, 0, W);
  camera.y = clamp(((e.clientY - r.top) / r.height) * H, 0, H);
  clampCamera();
}

/* ── selection ────────────────────────────────────────────────────────────── */

function clickSelect(t) {
  if (!inBounds(t.x, t.y)) return;
  const me = game.player;

  // A unit of yours standing here wins the click.
  const own = game.units.filter(u => !u.dead && u.owner === me &&
    cheb(Math.floor(u.x), Math.floor(u.y), t.x, t.y) === 0);
  if (own.length) {
    game.sel = { kind: 'unit', idx: own[0].i, units: own.map(u => u.i) };
    refreshSelection(); refreshRails();
    return;
  }

  const c = cityAt(t.x, t.y);
  if (c && world.explored[idx(t.x, t.y)]) {
    game.sel = { kind: 'city', idx: c.i, units: [] };
    refreshSelection(); refreshRails();
    return;
  }

  // Anything else: show the tile, or an enemy unit standing on it.
  const foe = game.units.find(u => !u.dead && world.vis[idx(t.x, t.y)] &&
    cheb(Math.floor(u.x), Math.floor(u.y), t.x, t.y) === 0);
  if (foe) { game.sel = { kind: 'unit', idx: foe.i, units: [] }; refreshSelection(); refreshRails(); return; }

  game.sel = { kind: 'tile', idx: idx(t.x, t.y), units: [] };
  refreshSelection(); refreshRails();
}

function boxSelect() {
  const r = canvas.getBoundingClientRect();
  const a = screenToWorld(Math.min(dragBox.x0, dragBox.x1), Math.min(dragBox.y0, dragBox.y1));
  const b = screenToWorld(Math.max(dragBox.x0, dragBox.x1), Math.max(dragBox.y0, dragBox.y1));
  const picked = [];
  for (const u of game.units) {
    if (u.dead || u.owner !== game.player) continue;
    if (u.x >= a.x && u.x <= b.x && u.y >= a.y && u.y <= b.y) picked.push(u.i);
  }
  game.sel = picked.length
    ? { kind: 'unit', idx: picked[0], units: picked }
    : { kind: null, idx: -1, units: [] };
  refreshSelection(); refreshRails();
}

/* ── orders ───────────────────────────────────────────────────────────────── */

function onRightClick(t) {
  if (game.place) { setPlace(null); return; }
  if (!inBounds(t.x, t.y)) return;

  const units = game.sel.units
    .map(i => game.units[i])
    .filter(u => u && !u.dead && u.owner === game.player);
  if (!units.length) return;

  const ti = idx(t.x, t.y);
  const seen = world.vis[ti] === 1;

  // Enemy unit or city under the cursor becomes an attack order.
  const foe = seen ? game.units.find(u => !u.dead && u.owner !== game.player &&
    cheb(Math.floor(u.x), Math.floor(u.y), t.x, t.y) === 0) : null;
  const foeCity = cityAt(t.x, t.y);

  let ordered = 0;
  for (const u of units) {
    if (foe && u.def.atk > 0) { orderAttack(u, 'unit', foe.i); ordered++; continue; }
    if (foeCity && foeCity.owner !== game.player && u.def.atk > 0) { orderAttack(u, 'city', foeCity.i); ordered++; continue; }
    if (u.def.role === 'settler' && canSettle(t.x, t.y).ok) { orderFound(u, t.x, t.y); ordered++; continue; }
    if (orderMove(u, t.x, t.y)) ordered++;
  }
  if (!ordered) logEvent('No route there.', 'info');
  refreshSelection();
}

function tryPlace(t) {
  const e = game.empires[game.player];
  const chk = canPlace(e, game.place, t.x, t.y);
  if (!chk.ok) { logEvent(chk.why, 'info'); return; }
  const d = BLD[game.place];
  e.gold -= d.cost;
  addBuilding(e, game.place, t.x, t.y, chk.city);
  logEvent(`${d.name} ordered near ${chk.city.name} — workers are assembling it.`, 'good');
  // Shift keeps the tool active so a row of farms is one selection, not five.
  if (!keys.has('shift')) setPlace(null);
  else refreshRails();
  refreshSelection();
}

/* ── keyboard ─────────────────────────────────────────────────────────────── */

function onKeyDown(e) {
  const k = e.key.toLowerCase();
  keys.add(k);

  if (k === 'escape') {
    if (game.place) { setPlace(null); return; }
    if (techOpen()) { closeTech(); return; }
    closePanels();
    if (actions.togglePause) actions.togglePause();
    return;
  }
  if (k === 'p') { if (actions.togglePause) actions.togglePause(); return; }
  if (k === 't') { if (playing() || game.mode === 'pause') { closePanels(); toggleTech(); } return; }

  if (!playing()) return;
  if (k === 'b') { togglePanel('buildPanel'); return; }
  if (k === 'c') { togglePanel('cityPanel'); return; }
  if (k === '1') { game.speed = 1; actions.speedChanged?.(); }
  if (k === '2') { game.speed = 2; actions.speedChanged?.(); }
  if (k === '3') { game.speed = 3; actions.speedChanged?.(); }
  if (k === 'f') {
    for (const i of game.sel.units) { const u = game.units[i]; if (u && !u.dead) orderFortify(u); }
    refreshSelection();
  }
  if (k === ' ') {
    // Space cycles through units that have nothing to do.
    e.preventDefault();
    const idle = game.units.filter(u => !u.dead && u.owner === game.player && u.state === 'idle');
    if (idle.length) {
      const u = idle[0];
      camera.x = u.x; camera.y = u.y;
      game.sel = { kind: 'unit', idx: u.i, units: [u.i] };
      refreshSelection(); refreshRails();
    }
  }
}

/** Smooth WASD / arrow panning, called every frame from the game loop. */
export function updateCameraKeys(dt) {
  if (!playing()) return;
  const speedTiles = 26 * dt;
  let dx = 0, dy = 0;
  if (keys.has('a') || keys.has('arrowleft')) dx -= 1;
  if (keys.has('d') || keys.has('arrowright')) dx += 1;
  if (keys.has('w') || keys.has('arrowup')) dy -= 1;
  if (keys.has('s') || keys.has('arrowdown')) dy += 1;
  if (!dx && !dy) return;
  const n = Math.hypot(dx, dy) || 1;
  camera.x += (dx / n) * speedTiles;
  camera.y += (dy / n) * speedTiles;
  clampCamera();
}
