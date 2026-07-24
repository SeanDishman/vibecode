// main.js — boots the page, owns the loop, wires the HUD, and honours the
// VibeCode host's pause contract (#stage.playing / #pause.hidden / #resumeBtn / "p").
//
// There is deliberately no movement input. E opens the shop, P pauses and M
// mutes — none of them steer anything. Buying is the entire game.

import {
  UPGRADES, LADDERS, rungOf, clamp, money,
  NODE_W, NODE_H, treeBounds, biggestUnder, speciesAtRod, strengthFor,
} from './core.js';
import {
  game, reset, update, buy, costOf, levelOf, blockedBy, needsOf,
  rodDepth, lineCount, lineStrength, biteEvery, fightTime, stormLevel, skyNow,
  currentZone, cheapestAffordable, reachable, CAST_TIME,
} from './game.js';
import { initRender, resize, draw, view } from './render.js';
import {
  preloadAudio, unlockAudio, setAmbience, setMuted, isMuted,
} from './audio.js';
import { updateWeather, weather } from './weather.js';

const $ = id => document.getElementById(id);
const stage = $('stage');
const screens = { menu: $('menu'), pause: $('pause') };
let last = 0, hudT = 0, ambT = 0, shopOpen = false, toldDone = false;
// Counts completed frames. draw() throwing used to leave a black screen with a
// live HUD in front of it, which is invisible to anything but a real frame count.
let frames = 0;

/* ── log ──────────────────────────────────────────────────────────────────── */
function log(text, kind = 'info') {
  const box = $('log');
  if (!box) return;
  const line = document.createElement('div');
  line.className = 'logline' + (kind !== 'info' ? ' ' + kind : '');
  line.textContent = text;
  box.appendChild(line);
  while (box.children.length > 5) box.removeChild(box.firstChild);
  setTimeout(() => { line.classList.add('fade'); setTimeout(() => line.remove(), 600); }, 5200);
}

/* ── screens ──────────────────────────────────────────────────────────────── */
function showScreen(name) {
  for (const k in screens) screens[k].classList.toggle('hidden', k !== name);
  stage.classList.toggle('playing', name === null);
  $('hud').style.display = name === null || name === 'pause' ? '' : 'none';
}

function startGame() {
  unlockAudio();
  reset();
  toldDone = false;
  game.mode = 'playing';
  showScreen(null);
  refreshHud(); refreshShop();
  setAmbience(true, 0);
  log('Sal is fishing. Spend what he earns.', 'good');
  focusGame();
}

export function togglePause() {
  if (game.mode === 'playing') {
    game.mode = 'pause';
    showScreen('pause');
    setAmbience(false, 0);
  } else if (game.mode === 'pause') {
    game.mode = 'playing';
    showScreen(null);
    setAmbience(true, stormLevel());
    focusGame();
  }
}

function focusGame() {
  try { window.focus(); $('game').focus(); } catch { /* convenience only */ }
}

/* ── HUD ──────────────────────────────────────────────────────────────────── */

/** What the lead rod is doing, as a label plus 0..1 progress for the bar. */
function rodStatus() {
  const L = game.lines[0];
  if (!L) return { label: 'Baiting up', k: 0 };
  const depth = rodDepth() || 1;
  switch (L.state) {
    case 'cast':  return { label: 'Casting', k: clamp(L.t / CAST_TIME, 0, 1) };
    case 'sink':  return { label: 'Sinking', k: clamp(L.y / depth, 0, 1) };
    case 'wait':  return { label: 'Waiting for a bite', k: clamp(L.t / Math.max(0.01, L.wait), 0, 1) };
    case 'fight': return { label: 'Fish on!', k: clamp(L.t / fightTime(), 0, 1) };
    case 'reel':  return { label: 'Reeling in', k: clamp(1 - L.y / depth, 0, 1) };
    case 'snap':  return { label: 'Line snapped', k: 1 };
    default:      return { label: 'Re-baiting', k: 0 };
  }
}

function refreshHud() {
  $('rCash').textContent = money(game.cash).replace('$', '');
  $('rRate').textContent = game.rate >= 1000
    ? money(game.rate).replace('$', '') + '/min'
    : Math.round(game.rate) + '/min';

  const st = rodStatus();
  $('rState').textContent = st.label;
  $('stateFill').style.width = (st.k * 100) + '%';
  $('stateFill').parentElement.classList.toggle('hot', st.label === 'Fish on!');

  $('rZone').textContent = stormLevel() > 0.8 ? 'Thunderstorm'
    : stormLevel() > 0.55 ? 'Squall'
      : weather.sun > 0.4 ? 'Sun-break' : currentZone().name;
  $('rDepth').textContent = rodDepth().toLocaleString('en-US') + ' m';
  $('rCaught').textContent = game.caught;
  $('rRods').textContent = lineCount();

  // Best payday so far — hidden until there is one to brag about.
  $('bestWrap').hidden = !game.best;
  if (game.best) $('rBest').textContent = `${game.best.name} ${money(game.best.value)}`;

  const hint = $('hint');
  const afford = cheapestAffordable();
  const done = UPGRADES.every(u => levelOf(u.id) >= u.max);

  // Landing the last upgrade deserves saying once, not every frame.
  if (done && !toldDone) {
    toldDone = true;
    log('Every rod, reel and line the chandlery sells. The Abyss is yours.', 'gold');
  }

  let text = 'Sal fishes on his own — spend the money, that is the whole job', urgent = false;
  if (game.hintKey === 'snap') { text = 'Line keeps parting — upgrade Line to hold the big ones'; urgent = true; }
  else if (skyNow() === 'storm') { text = 'Thunderstorm — storm fish are running, and they pay'; urgent = true; }
  else if (skyNow() === 'sun') text = 'The sun is out — sunfish are up, and they are worth a fortune';
  else if (done) text = 'Fully kitted out — nothing left to buy but sea room';
  else if (afford) text = `Press E — you can afford ${afford.u.name}`;
  hint.textContent = text;
  hint.classList.toggle('urgent', urgent);
}

/* ── research tree ──────────────────────────────────────────────────────────
   The shop is not a list and no longer a small popover: it is a tree laid out
   on its own canvas at fixed tree coordinates (u.x/u.y), which the player drags
   and zooms over. That buys two things a grid could not — the layout can sprawl
   wider than any window, and a node can be looked at closely.

   The DOM is built exactly once, in buildTree(). syncTree() then only rewrites
   text and classes, because the game loop calls it ten times a second and
   rebuilding would throw away the pan, the zoom and the selection every tick. */

const SVGNS = 'http://www.w3.org/2000/svg';
const RUNGS = UPGRADES.reduce((n, u) => n + u.max, 0);   // every buyable level in the tree

const cam = { x: 0, y: 0, k: 1 };
const nodes = {};          // id → { el, lvl, rung, stat, price, pips[] }
const linkPaths = {};      // id → the <path> from this node up to its parent
let selected = 'net';
let railKey = '';          // rebuild the rung list only when it would actually change
let built = false;

/* ── the canvas: pan and zoom ─────────────────────────────────────────────── */

function applyCam() {
  $('treePan').style.transform = `translate(${cam.x.toFixed(1)}px, ${cam.y.toFixed(1)}px) scale(${cam.k.toFixed(3)})`;
  $('zPct').textContent = Math.round(cam.k * 100) + '%';
}

/** Zoom about a point in wrapper space, so whatever is under the cursor stays
 *  under the cursor — the thing that makes a map feel like a map. */
function zoomAt(k2, px, py) {
  k2 = clamp(k2, 0.35, 2.2);
  cam.x = px - (px - cam.x) * (k2 / cam.k);
  cam.y = py - (py - cam.y) * (k2 / cam.k);
  cam.k = k2;
  applyCam();
}

function zoomBy(f) {
  const r = $('treeWrap').getBoundingClientRect();
  zoomAt(cam.k * f, r.width / 2, r.height / 2);
}

/** Frame the whole tree in the viewport, which is also the opening shot. */
function fitTree() {
  const wrap = $('treeWrap');
  const r = wrap.getBoundingClientRect();
  if (!r.width) return;
  const b = treeBounds();
  const w = b.x1 - b.x0, h = b.y1 - b.y0, pad = 46;
  cam.k = clamp(Math.min((r.width - pad * 2) / w, (r.height - pad * 2) / h), 0.35, 1.15);
  cam.x = (r.width - w * cam.k) / 2 - b.x0 * cam.k;
  cam.y = (r.height - h * cam.k) / 2 - b.y0 * cam.k;
  applyCam();
}

/** Pan by dragging anywhere on the canvas. A drag that actually moved swallows
 *  the click it ends on, so dragging across a Buy button never buys. */
function wireCanvas() {
  const wrap = $('treeWrap');
  let drag = null, swallow = false;

  wrap.addEventListener('pointerdown', e => {
    if (e.button !== 0) return;
    drag = { px: e.clientX, py: e.clientY, cx: cam.x, cy: cam.y, moved: 0 };
    wrap.classList.add('grabbing');
  });
  window.addEventListener('pointermove', e => {
    if (!drag) return;
    const dx = e.clientX - drag.px, dy = e.clientY - drag.py;
    drag.moved = Math.max(drag.moved, Math.abs(dx) + Math.abs(dy));
    cam.x = drag.cx + dx; cam.y = drag.cy + dy;
    applyCam();
  });
  window.addEventListener('pointerup', () => {
    if (!drag) return;
    swallow = drag.moved > 5;
    drag = null;
    wrap.classList.remove('grabbing');
  });
  wrap.addEventListener('click', e => {
    if (!swallow) return;
    swallow = false;
    e.stopPropagation(); e.preventDefault();
  }, true);

  wrap.addEventListener('wheel', e => {
    e.preventDefault();
    const r = wrap.getBoundingClientRect();
    zoomAt(cam.k * Math.exp(-e.deltaY * 0.0016), e.clientX - r.left, e.clientY - r.top);
  }, { passive: false });

  $('zIn').addEventListener('click', () => zoomBy(1.25));
  $('zOut').addEventListener('click', () => zoomBy(1 / 1.25));
  $('zFit').addEventListener('click', fitTree);
}

/* ── the nodes ────────────────────────────────────────────────────────────── */

function buildTree() {
  const pan = $('treePan');
  const b = treeBounds();
  pan.style.width = b.x1 + 'px';
  pan.style.height = b.y1 + 'px';

  for (const u of UPGRADES) {
    const el = document.createElement('div');
    el.className = 'tnode';
    el.style.left = u.x + 'px';
    el.style.top = u.y + 'px';
    el.innerHTML = `
      <div class="thead">
        <div class="tico">${u.icon}</div>
        <div class="tnm">${u.name}</div>
        <div class="tlv">L0</div>
      </div>
      <div class="trung">—</div>
      <div class="tstat">—</div>
      <div class="tpips"></div>
      <button class="tbuy" type="button">—</button>`;

    const pips = [];
    const pipBox = el.querySelector('.tpips');
    for (let i = 1; i <= u.max; i++) {
      const pip = document.createElement('i');
      if (rungOf(u.id, i).big) pip.classList.add('big');
      pipBox.appendChild(pip);
      pips.push(pip);
    }

    const buyBtn = el.querySelector('.tbuy');
    buyBtn.addEventListener('click', e => {
      e.stopPropagation();
      select(u.id);
      if (buy(u.id)) { syncTree(); refreshHud(); }
    });
    el.addEventListener('click', () => select(u.id));

    nodes[u.id] = {
      u, el, buyBtn, pips,
      lv: el.querySelector('.tlv'),
      rung: el.querySelector('.trung'),
      stat: el.querySelector('.tstat'),
      ico: el.querySelector('.tico'),
    };
    pan.appendChild(el);
  }

  // Links are computed from u.x/u.y alone — no layout read, so the curves are
  // exact at any zoom. The tree flows left to right, so a child sitting to the
  // right of its parent gets a horizontal S-curve off the parent's right edge;
  // anything stacked below still gets the vertical one.
  const svg = $('treeLinks');
  svg.setAttribute('width', b.x1);
  svg.setAttribute('height', b.y1);
  for (const u of UPGRADES) {
    if (!u.needs) continue;
    const a = UPGRADES.find(x => x.id === u.needs.id);
    const across = u.x > a.x;
    const x1 = across ? a.x + NODE_W : a.x + NODE_W / 2;
    const y1 = across ? a.y + NODE_H / 2 : a.y + NODE_H;
    const x2 = across ? u.x : u.x + NODE_W / 2;
    const y2 = across ? u.y + NODE_H / 2 : u.y;
    const mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
    const path = document.createElementNS(SVGNS, 'path');
    path.setAttribute('d', across
      ? `M ${x1} ${y1} C ${mx} ${y1}, ${mx} ${y2}, ${x2} ${y2}`
      : `M ${x1} ${y1} C ${x1} ${my}, ${x2} ${my}, ${x2} ${y2}`);
    svg.appendChild(path);
    linkPaths[u.id] = path;

    // The gate itself, printed on the wire: "NET L2".
    const tag = document.createElementNS(SVGNS, 'text');
    tag.setAttribute('x', mx);
    tag.setAttribute('y', my - 7);
    tag.setAttribute('class', 'gate');
    tag.textContent = `${a.name.toUpperCase()} L${u.needs.lvl}`;
    svg.appendChild(tag);
    linkPaths[u.id + ':tag'] = tag;
  }

  built = true;
}

/** Everything a node's look depends on, in one place — the shop and the rail
 *  both branch on exactly these four states. */
function stateOf(id) {
  const lvl = levelOf(id);
  const u = UPGRADES.find(x => x.id === id);
  const maxed = lvl >= u.max;
  const need = needsOf(id);
  const needLine = maxed ? 0 : blockedBy(id, lvl);
  const price = costOf(id);
  return {
    u, lvl, maxed, need, needLine, price,
    locked: !!(need || needLine),
    afford: !maxed && !need && !needLine && game.cash >= price,
  };
}

function syncTree() {
  if (!built) return;
  $('shopCash').textContent = money(game.cash);
  const owned = UPGRADES.reduce((n, u) => n + levelOf(u.id), 0);
  $('shopProgress').textContent = `${owned} / ${RUNGS} rungs`;

  for (const u of UPGRADES) {
    const n = nodes[u.id];
    const s = stateOf(u.id);

    n.el.className = 'tnode'
      + (s.afford ? ' afford' : '') + (s.maxed ? ' maxed' : '')
      + (s.locked ? ' locked' : '') + (u.id === selected ? ' sel' : '');
    n.ico.textContent = s.locked ? '🔒' : u.icon;
    n.lv.textContent = `L${s.lvl}`;
    n.rung.textContent = rungOf(u.id, s.lvl).n;
    n.stat.textContent = u.stat(s.lvl);

    for (let i = 0; i < n.pips.length; i++) {
      n.pips[i].classList.toggle('on', i < s.lvl);
      n.pips[i].classList.toggle('next', i === s.lvl && !s.maxed);
    }

    n.buyBtn.disabled = !s.afford;
    n.buyBtn.textContent = s.maxed ? 'MAXED'
      : s.need ? `🔒 ${UPGRADES.find(x => x.id === s.need.id).name} L${s.need.lvl}`
        : s.needLine ? `🔒 Line L${s.needLine}`
          : money(s.price);

    const p = linkPaths[u.id];
    if (p) {
      const cls = s.lvl > 0 ? 'owned' : s.need ? '' : 'open';
      p.setAttribute('class', cls);
      linkPaths[u.id + ':tag'].setAttribute('class', 'gate ' + cls);
    }
  }

  syncRail();
}

function select(id) {
  if (selected === id) return;
  selected = id;
  syncTree();
}

/* ── the rail: one track's whole future, rung by rung ─────────────────────── */

/** The one extra line a rung is worth spelling out — which fish it puts in
 *  reach, or which fish it stops losing. Everything else is on the rung itself. */
function rungAside(id, i) {
  if (id === 'rod') {
    const fish = speciesAtRod(i);
    return fish.length ? fish.join(', ') : '';
  }
  if (id === 'line') return `holds up to a ${biggestUnder(strengthFor(i))}`;
  return '';
}

function syncRail() {
  const rail = $('rail');
  const s = stateOf(selected);
  const u = s.u;
  const key = `${selected}|${s.lvl}`;

  if (railKey !== key) {
    railKey = key;
    const ladder = LADDERS[selected];
    rail.innerHTML = `
      <div class="rhead">
        <div class="tico big">${u.icon}</div>
        <div>
          <div class="rnm">${u.name}</div>
          <div class="rlv">L${s.lvl} of ${u.max} · ${u.stat(s.lvl)}</div>
        </div>
      </div>
      <p class="rblurb">${u.blurb}</p>
      <div class="rungs"></div>
      <button class="rbuy" type="button"></button>`;

    const box = rail.querySelector('.rungs');
    for (let i = 1; i < ladder.length; i++) {
      const r = ladder[i];
      const row = document.createElement('div');
      row.className = 'rung' + (i <= s.lvl ? ' done' : i === s.lvl + 1 ? ' next' : ' far')
        + (r.big ? ' big' : '');
      const aside = rungAside(selected, i);
      row.innerHTML = `
        <span class="ri">${r.big ? '★' : i <= s.lvl ? '✓' : '·'}</span>
        <span class="rl">L${i}</span>
        <span class="rn">${r.n}</span>
        <span class="rv">${u.stat(i)}</span>
        <span class="rp">${r.p}</span>
        ${aside ? `<span class="ra">${aside}</span>` : ''}
        <span class="rc">${i <= s.lvl ? 'owned' : money(u.cost(i - 1))}</span>`;
      box.appendChild(row);
    }

    const btn = rail.querySelector('.rbuy');
    btn.addEventListener('click', () => { if (buy(selected)) { syncTree(); refreshHud(); } });
    // Bring the rung you are about to buy into view, not the top of the list.
    box.querySelector('.rung.next')?.scrollIntoView({ block: 'center' });
  }

  const btn = rail.querySelector('.rbuy');
  if (!btn) return;
  btn.disabled = !s.afford;
  btn.className = 'rbuy' + (s.afford ? ' afford' : '') + (s.locked ? ' locked' : '');
  btn.textContent = s.maxed ? 'Fully researched'
    : s.need ? `Needs ${UPGRADES.find(x => x.id === s.need.id).name} L${s.need.lvl}`
      : s.needLine ? `Line L${s.needLine} first — that water would snap what you have`
        : `Research L${s.lvl + 1} · ${rungOf(selected, s.lvl + 1).n} — ${money(s.price)}`;
}

/** Kept as the name the rest of the file calls: build once, then only sync. */
function refreshShop() {
  if (!$('treePan')) return;
  if (!built) { buildTree(); wireCanvas(); }
  syncTree();
}

function toggleMute() {
  setMuted(!isMuted());
  const muted = isMuted();
  $('muteBtn').textContent = muted ? '🔇' : '🔊';
  $('muteBtn').classList.toggle('off', muted);
  // Coming back from mute has to restart the loops setMuted() tore down.
  if (!muted && game.mode === 'playing') setAmbience(true, stormLevel());
}

let everFitted = false;

function toggleShop(force) {
  shopOpen = force ?? !shopOpen;
  $('shop').classList.toggle('hidden', !shopOpen);
  $('btnShop').classList.toggle('on', shopOpen);
  if (!shopOpen) return;
  refreshShop();
  // The canvas has no size until it is on screen, so the opening framing has to
  // wait until after .hidden comes off.
  if (!everFitted) { everFitted = true; fitTree(); }
}

/* ── input ────────────────────────────────────────────────────────────────── */
function wireInput() {
  const cv = $('game');
  cv.addEventListener('mousedown', () => cv.focus());
  cv.addEventListener('contextmenu', e => e.preventDefault());

  window.addEventListener('keydown', e => {
    const k = e.key.toLowerCase();
    if (k === 'escape') { if (shopOpen) { toggleShop(false); return; } togglePause(); return; }
    if (k === 'p') { togglePause(); return; }
    if (k === 'm') { toggleMute(); return; }
    if (game.mode !== 'playing') return;
    if (k === 'e') { toggleShop(); return; }
    // Map keys, only while the map is up.
    if (!shopOpen) return;
    if (k === '+' || k === '=') zoomBy(1.25);
    else if (k === '-' || k === '_') zoomBy(1 / 1.25);
    else if (k === '0') fitTree();
  });
}

/* ── loop ─────────────────────────────────────────────────────────────────── */
function frame(now) {
  requestAnimationFrame(frame);
  const raw = last ? (now - last) / 1000 : 0;
  last = now;
  const dt = clamp(raw, 0, 0.1);

  // The sea also runs behind the title card, so the menu shows the actual game
  // rather than a still. Anything earned there is wiped by reset() on Cast off,
  // and audio stays locked until the player commits, so it costs nothing.
  if (game.mode === 'playing' || game.mode === 'menu') {
    update(dt);
    updateWeather(dt, view());
  }

  if (game.mode === 'playing') {
    // A gain ramp does not need 60 updates a second.
    ambT += dt;
    if (ambT > 0.25) { ambT = 0; setAmbience(true, stormLevel()); }
    hudT += dt;
    if (hudT > 0.1) { hudT = 0; refreshHud(); if (shopOpen) refreshShop(); }
  }

  draw(dt);
  frames++;
}

/* ── boot ─────────────────────────────────────────────────────────────────── */
function boot() {
  initRender($('game'));
  wireInput();
  preloadAudio();

  $('startBtn').addEventListener('click', startGame);
  $('resumeBtn').addEventListener('click', e => { e.preventDefault(); if (game.mode === 'pause') togglePause(); });
  $('pauseBtn').addEventListener('click', togglePause);
  $('btnShop').addEventListener('click', () => toggleShop());
  $('shopClose').addEventListener('click', () => toggleShop(false));
  $('muteBtn').addEventListener('click', toggleMute);

  window.addEventListener('resize', resize);
  document.addEventListener('visibilitychange', () => {
    if (document.hidden && game.mode === 'playing') togglePause();
    else if (!document.hidden && game.mode === 'pause') focusGame();
  });

  game.onBought = (u, lvl) => log(`${u.name} → L${lvl}`, 'good');
  game.onSnap = sp => log(`${sp.name} snapped the line`, 'bad');
  // Weather fish are the whole reward for fishing through the weather, so they
  // get called out by name; ordinary catches stay quiet in the log.
  game.onCatch = (sp, value) => {
    if (sp.sky) log(`${sp.name} — ${money(value)}${sp.sky === 'storm' ? ' out of the storm' : ' in the sun'}`, 'gold');
  };

  // Debug surface for headless smoke tests.
  window.__FT = {
    game, weather, start: startGame, togglePause,
    simulate(seconds, step = 1 / 30) {
      const errs = [];
      for (let t = 0; t < seconds; t += step) {
        try { update(step); updateWeather(step, view()); }
        catch (e) { errs.push(String(e && e.stack || e)); if (errs.length > 2) break; }
      }
      return errs;
    },
    snapshot: () => ({
      mode: game.mode, cash: Math.round(game.cash), caught: game.caught,
      earned: Math.round(game.earned), rate: Math.round(game.rate),
      depth: rodDepth(), zone: currentZone().name,
      sky: skyNow(), storm: +weather.storm.toFixed(2), sun: +weather.sun.toFixed(2),
      rods: lineCount(), strength: +lineStrength().toFixed(1),
      bite: +biteEvery().toFixed(2),
      species: reachable().map(f => f.id),
      states: game.lines.map(L => L.state),
      levels: { ...game.levels }, frames, errors: window.__FT_ERRORS,
    }),
    get frames() { return frames; },
    buy, costOf, setMuted, blockedBy, needsOf,
    forceStorm(v = 1) {
      weather.target = v; weather.storm = v; weather.nextChange = 60;
      weather.sun = 0; weather.sunTarget = 0; weather.sunFor = 0;
    },
    forceSun(v = 1) {
      weather.target = 0; weather.storm = 0; weather.nextChange = 120;
      weather.sun = v; weather.sunTarget = v;
      // v = 0 has to clear the break timer too, or updateWeather sees a live
      // countdown and ramps the sun straight back up.
      weather.sunFor = v > 0 ? 120 : 0;
      weather.wasWet = false;
    },
  };
  window.__FT_BOOTED = true;

  // Seed a world up front so the title card has a real ocean behind it.
  reset();
  showScreen('menu');
  requestAnimationFrame(frame);
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();
