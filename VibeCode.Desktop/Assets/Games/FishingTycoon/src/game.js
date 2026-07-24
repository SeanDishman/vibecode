// game.js — the idle simulation. Nobody steers and nobody hauls: the boat sits
// anchored on its mark and the deckhands cast, wait, hook and wind in on their
// own, forever. The only thing the player ever does is buy upgrades, so every
// number here is a pure read off `levels` — see the stat curves in core.js.

import {
  FISH, UPGRADES, zoneAt, rand, lineNeededFor,
  depthFor, biteDelay, reelFor, sinkFor, strengthFor, linesFor, traderFor,
  netEvery, netDepthFor, fightFor, gripFor,
} from './core.js';
import { sfxCatch, sfxSell, sfxBuy } from './audio.js';
import { resetWeather, weather } from './weather.js';

/** Where each rod pokes over the rail, as an x offset from the boat's centre.
 *  Ordered so the first deckhand gets the best spot on the stern. */
export const ROD_SLOTS = [58, -34, 88, -66];

/** How long the cast takes. Long enough to actually watch the hook leave the
 *  rod, fly out and land — at the old 0.42s it was over before you saw it. */
export const CAST_TIME = 0.85;

export const game = {
  mode: 'menu',
  t: 0,
  cash: 0, caught: 0, earned: 0,
  best: null,                 // heaviest payday so far: { name, value }
  levels: { net: 0, rod: 0, bait: 0, reel: 0, line: 0, crew: 0, trader: 0, power: 0 },
  boat: { bob: 0, tilt: 0 },
  lines: [],                  // one per deckhand — the whole game loop lives here
  netT: 0,                    // countdown to the net's next catch
  pops: [],                   // floating catch / snap labels
  recent: [],                 // [t, amount] used for the $/min readout
  rate: 0,
  hintKey: '',
};

/* ── derived stats ────────────────────────────────────────────────────────── */
export const rodDepth = () => depthFor(game.levels.rod);
export const reelSpeed = () => reelFor(game.levels.reel);
export const sinkSpeed = () => sinkFor(game.levels.reel);
export const lineStrength = () => strengthFor(game.levels.line);
export const lineCount = () => linesFor(game.levels.crew);
export const valueMult = () => traderFor(game.levels.trader);
export const biteEvery = () => biteDelay(game.levels.bait);
export const fightTime = () => fightFor(game.levels.power);
export const gripMult = () => gripFor(game.levels.power);
export const stormLevel = () => weather.storm;

/** Deepest water the view needs to show: the hook plus a little seabed below. */
export const viewDepth = () => Math.max(200, rodDepth() * 1.14);

/** What the sky is doing, as far as the fish are concerned. Thunderstorms bring
 *  the storm species up; a honey sun-break brings the sunfish out. */
export function skyNow() {
  if (weather.storm > 0.66) return 'storm';
  if (weather.sun > 0.4) return 'sun';
  return 'fair';
}

/** Species that can be on the hook right now — depth first, then whatever the
 *  current sky allows. Weather species are absent unless their sky is up. */
export function reachable(d = rodDepth(), sky = skyNow()) {
  return FISH.filter(f => d >= f.min && d <= f.max && (!f.sky || f.sky === sky));
}

export function reset() {
  game.t = 0; game.cash = 0; game.caught = 0; game.earned = 0;
  game.best = null;
  game.levels = { net: 0, rod: 0, bait: 0, reel: 0, line: 0, crew: 0, trader: 0, power: 0 };
  game.boat = { bob: 0, tilt: 0 };
  game.lines = [];
  game.netT = 0;
  game.pops = []; game.recent = []; game.rate = 0;
  game.hintKey = '';
  resetWeather();
  syncLines();
}

/* ── the deckhands ────────────────────────────────────────────────────────── */

function makeLine(i) {
  // Stagger the opening cast so four rods never move in lockstep.
  // `splash` counts up from 0 when the hook hits the water, -1 when there is
  // nothing in; the renderer draws the rings and the float off it.
  return { i, state: 'rest', t: -i * 0.55, y: 0, sp: null, wait: 0, splash: -1 };
}

/** Add or drop rods to match the Deckhands upgrade. */
function syncLines() {
  const want = lineCount();
  while (game.lines.length < want) game.lines.push(makeLine(game.lines.length));
  if (game.lines.length > want) game.lines.length = want;
}

/** Weighted pick among everything living at the hook's depth. */
function speciesAt(d) {
  const ok = reachable(d);
  if (!ok.length) return FISH[0];
  const total = ok.reduce((s, f) => s + f.w, 0);
  let r = Math.random() * total;
  for (const f of ok) { r -= f.w; if (r <= 0) return f; }
  return ok[ok.length - 1];
}

function updateLine(L, dt) {
  const depth = rodDepth();
  L.t += dt;
  if (L.splash >= 0) L.splash += dt;

  switch (L.state) {
    case 'rest':                                  // fish unhooked, rod re-baited
      if (L.t > 0.5) { L.state = 'cast'; L.t = 0; L.y = 0; L.sp = null; L.splash = -1; }
      break;

    case 'cast':                                  // rod swings, hook arcs out
      if (L.t > CAST_TIME) { L.state = 'sink'; L.t = 0; L.splash = 0; }
      break;

    case 'sink':                                  // weighted hook drops to depth
      L.y += sinkSpeed() * dt;
      if (L.y >= depth) { L.y = depth; L.state = 'wait'; L.t = 0; L.wait = biteEvery() * rand(1.35, 0.7); }
      break;

    case 'wait':                                  // float bobbing, nothing yet
      L.y = depth + Math.sin(game.t * 1.7 + L.i * 2.1) * 7;
      if (L.t >= L.wait) { L.sp = speciesAt(depth); L.state = 'fight'; L.t = 0; }
      break;

    case 'fight':                                 // something's on — rod bends
      L.y = depth - Math.sin(L.t * 15) * 20;
      if (L.t > fightTime()) {
        // Too heavy for the line and it parts. That is the whole point of the
        // Line track: depth alone gets you bites you cannot actually land.
        // Reel power's grip lets the line hold a little past its rating.
        if (L.sp.len > lineStrength() * gripMult()) snap(L);
        else { L.state = 'reel'; L.t = 0; }
      }
      break;

    case 'reel':                                  // winding it up to the rail
      L.y -= reelSpeed() * dt;
      if (L.y <= 0) { L.y = 0; land(L); }
      break;

    case 'snap':                                  // line parted, fish gone
      if (L.t > 0.95) { L.state = 'rest'; L.t = 0; L.sp = null; }
      break;
  }
}

function land(L) {
  const sp = L.sp;
  const value = Math.round(sp.value * valueMult());
  game.cash += value;
  game.earned += value;
  game.caught++;
  game.recent.push([game.t, value]);
  // A personal best is the only moment worth more than a splash.
  const record = !game.best || value > game.best.value;
  if (record) game.best = { name: sp.name, value };
  game.pops.push({ slot: L.i, text: `${sp.name}  +$${value.toLocaleString('en-US')}`, col: sp.col, t: 0, bad: false });
  try { sfxCatch(); if (record && game.caught > 1) sfxSell(); } catch { /* audio optional */ }
  game.onCatch?.(sp, value);
  // Nothing below the surface is drawn, so the fish breaking it is the only
  // sign anything came up: restart the splash so the rings replay at the mark.
  L.state = 'rest'; L.t = 0; L.sp = null; L.splash = 0;
}

function snap(L) {
  game.pops.push({ slot: L.i, text: `Line snapped — ${L.sp.name}`, col: '#ff8f7a', t: 0, bad: true });
  game.hintKey = 'snap';
  game.onSnap?.(L.sp);
  L.state = 'snap'; L.t = 0;
}

/* ── the net ────────────────────────────────────────────────────────────────
   Passive fishing: whatever swims into it from the bands it reaches, hauled
   aboard on its own clock. Never weather fish — those only take a hook. */
function netSpecies() {
  const d = netDepthFor(levelOf('net'));
  const ok = FISH.filter(f => !f.sky && f.min <= d);
  const total = ok.reduce((s, f) => s + f.w, 0);
  let r = Math.random() * total;
  for (const f of ok) { r -= f.w; if (r <= 0) return f; }
  return ok[ok.length - 1];
}

function updateNet(dt) {
  // levelOf, not game.levels.net: a missing key reads back undefined, and
  // `undefined <= 0` is false, so a bare `game.levels.net` walks straight past
  // this guard and lands in netSpecies() with no depth and no species to pick.
  const lvl = levelOf('net');
  if (lvl <= 0) return;
  game.netT += dt;
  if (game.netT < netEvery(lvl)) return;
  game.netT = 0;
  const sp = netSpecies();
  const value = Math.round(sp.value * valueMult());
  game.cash += value;
  game.earned += value;
  game.caught++;
  game.recent.push([game.t, value]);
  // slot -1: the pop floats up off the stern, where the net comes aboard.
  game.pops.push({ slot: -1, text: `Net — ${sp.name}  +$${value.toLocaleString('en-US')}`, col: sp.col, t: 0, bad: false });
  game.onCatch?.(sp, value);
}

function updatePops(dt) {
  for (const p of game.pops) p.t += dt;
  if (game.pops.length) game.pops = game.pops.filter(p => p.t < 2.2);
}

/** Rolling 30-second earn rate, shown as $/min. */
function updateRate() {
  const cut = game.t - 30;
  while (game.recent.length && game.recent[0][0] < cut) game.recent.shift();
  let sum = 0;
  for (const [, v] of game.recent) sum += v;
  const span = Math.min(30, Math.max(4, game.t));
  game.rate = sum * (60 / span);
}

/* ── update ───────────────────────────────────────────────────────────────── */

export function update(dt) {
  game.t += dt;
  syncLines();
  game.boat.bob += dt * (1.15 + weather.storm * 1.4);
  game.boat.tilt = Math.sin(game.boat.bob * 0.62) * (0.014 + weather.storm * 0.055);
  for (const L of game.lines) updateLine(L, dt);
  updateNet(dt);
  updatePops(dt);
  updateRate();
}

/* ── economy ──────────────────────────────────────────────────────────────── */

export const levelOf = id => game.levels[id] ?? 0;

export function costOf(id) {
  const u = UPGRADES.find(u => u.id === id);
  if (!u) return Infinity;
  const l = levelOf(id);
  return l >= u.max ? Infinity : u.cost(l);
}

/** A rod bought too far ahead of the line drops the hook into water where every
 *  remaining species is over the breaking strain — nothing lands, nothing earns,
 *  and there is no way back out of it. That one sale is held until the line can
 *  hold something. Returns the Line level required, or 0 when the sale is fine. */
export function blockedBy(id, l = levelOf(id)) {
  if (id !== 'rod') return 0;
  const need = lineNeededFor(l + 1);
  return game.levels.line < need ? need : 0;
}

/** The tree gate: which track — and at what level — must be owned before this
 *  one sells its next level. Returns the unmet { id, lvl } requirement, or
 *  null when the node is unlocked. The net has no parent: it is the root. */
export function needsOf(id) {
  const u = UPGRADES.find(u => u.id === id);
  if (!u || !u.needs) return null;
  return levelOf(u.needs.id) >= u.needs.lvl ? null : u.needs;
}

export function buy(id) {
  const u = UPGRADES.find(u => u.id === id);
  if (!u) return false;
  const l = levelOf(id);
  if (l >= u.max) return false;
  if (blockedBy(id, l) || needsOf(id)) return false;
  const price = u.cost(l);
  if (game.cash < price) return false;
  game.cash -= price;
  game.levels[id] = l + 1;
  if (id === 'line') game.hintKey = '';
  try { sfxBuy(); } catch { /* audio optional */ }
  game.onBought?.(u, l + 1);
  return true;
}

/** The cheapest thing worth buying — drives the "you can afford X" nudge.
 *  Skips anything the player cannot actually click, so the hint never points at
 *  a gated rod. */
export function cheapestAffordable() {
  let best = null;
  for (const u of UPGRADES) {
    const c = costOf(u.id);
    if (c === Infinity || game.cash < c || blockedBy(u.id) || needsOf(u.id)) continue;
    if (!best || c < best.cost) best = { u, cost: c };
  }
  return best;
}

export const currentZone = () => zoneAt(rodDepth());
