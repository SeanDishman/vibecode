// military.js — orders, movement, combat and conquest. Units move in continuous
// tile coordinates along an A* path; fighting is cooldown-driven rather than
// turn-based so a battle plays out visibly while the economy ticks around it.

import {
  W, H, idx, inBounds, clamp, cheb, dist, rand, randRange, isWater, T,
} from './core.js';
import { world, game, markDirty, cityAt } from './store.js';
import { TERRAIN, TERRAIN_DEF, UNI } from './data.js';
import { moveCost, recomputeCity, recomputeEmpire } from './economy.js';
import {
  addFx, killUnit, captureCity, foundCity, claimTerritory, cityRoles, nearestCity,
} from './entities.js';
import { findPath } from './pathing.js';
import { logEvent } from './log.js';

const BASE_SPEED = 1.15;        // tiles per second at 1× before terrain
const STRIKE_INTERVAL = 0.85;   // seconds between blows
const MIN_CITY_GAP = 4;         // tiles between city centres

/* ── orders ───────────────────────────────────────────────────────────────── */

export function orderMove(u, x, y) {
  if (u.dead) return false;
  x = clamp(Math.floor(x), 0, W - 1);
  y = clamp(Math.floor(y), 0, H - 1);
  const path = findPath(u, Math.floor(u.x), Math.floor(u.y), x, y);
  if (!path) { u.stuck = 1; return false; }
  u.order = { kind: 'move', x, y };
  u.path = path; u.pathI = 0; u.state = 'move'; u.fortified = 0;
  return true;
}

export function orderAttack(u, targetKind, targetIdx) {
  if (u.dead || !canFight(u)) return false;
  u.order = { kind: 'attack', targetKind, target: targetIdx };
  u.state = 'move'; u.path = null; u.repath = 0; u.fortified = 0;
  return true;
}

export function orderFound(u, x, y) {
  if (u.dead || u.def.role !== 'settler') return false;
  if (!orderMove(u, x, y)) return false;
  u.order = { kind: 'found', x: Math.floor(x), y: Math.floor(y) };
  return true;
}

export function orderFortify(u) {
  if (u.dead) return;
  u.order = null; u.path = null; u.state = 'fortify'; u.fortified = 0.0001;
}

export const canFight = u => u.def.atk > 0 && !u.embarked;

/** Engines with no oil behind them fight at just over half strength. */
const fuelFactor = u => (u.def.oilUp && game.empires[u.owner].dry ? 0.55 : 1);

/* ── main update ──────────────────────────────────────────────────────────── */

export function updateUnits(dt) {
  for (const u of game.units) {
    if (u.dead) continue;
    if (u.cd > 0) u.cd -= dt;
    if (u.flash > 0) u.flash = Math.max(0, u.flash - dt * 3);
    if (u.state === 'fortify') u.fortified = Math.min(1, u.fortified + dt * 0.35);

    // Riding the waves? Land units on water are embarked and cannot fight back.
    // Aircraft are simply over it and never count as embarked.
    const ti = idx(clamp(Math.floor(u.x), 0, W - 1), clamp(Math.floor(u.y), 0, H - 1));
    u.embarked = !u.def.sea && !u.def.air && isWater(world.terr[ti]);

    stepUnit(u, dt);
    healUnit(u, dt);
  }
  // Compact the arrays occasionally so dead entries don't accumulate forever.
  if (game.units.length > 500) compactUnits();
}

function stepUnit(u, dt) {
  const order = u.order;

  if (order && order.kind === 'attack') {
    const target = resolveTarget(order);
    if (!target) { u.order = null; u.state = 'idle'; u.path = null; return; }
    const inRange = targetInRange(u, target);
    if (inRange) {
      u.path = null;
      u.state = 'attack';
      tryStrike(u, target);
      return;
    }
    // Close the distance, re-pathing now and then because the target moves.
    u.repath -= dt;
    if (!u.path || u.pathI >= u.path.length || u.repath <= 0) {
      const tp = targetTile(target);
      const p = findPath(u, Math.floor(u.x), Math.floor(u.y), tp.x, tp.y, 4500);
      u.path = p; u.pathI = 0; u.repath = 1.2 + rand() * 0.6;
    }
    u.state = 'move';
    followPath(u, dt);
    return;
  }

  if (u.path && u.pathI < u.path.length) {
    followPath(u, dt);
    return;
  }

  // Arrived.
  if (order && order.kind === 'found') {
    if (tryFoundCity(u)) return;
    u.order = null;
  } else if (order && order.kind === 'move') {
    u.order = null;
  }
  if (u.state !== 'fortify') u.state = 'idle';

  // Idle fighters pick their own quarrels with anything that wanders too close.
  if (canFight(u)) {
    const foe = nearestEnemy(u, u.def.range ? u.def.range : 1.6);
    if (foe) { tryStrike(u, foe); u.state = 'attack'; }
  }
}

function followPath(u, dt) {
  if (!u.path || u.pathI >= u.path.length) return;
  const ti = u.path[u.pathI];
  const tx = (ti % W) + 0.5, ty = ((ti / W) | 0) + 0.5;
  const dx = tx - u.x, dy = ty - u.y;
  const d = Math.hypot(dx, dy);

  if (d < 0.06) {
    u.x = tx; u.y = ty; u.pathI++;
    if (u.pathI >= u.path.length) { u.path = null; }
    return;
  }

  const cost = moveCost(u, Math.floor(tx), Math.floor(ty));
  if (!isFinite(cost)) { u.path = null; u.pathI = 0; return; }   // terrain changed under us
  const spd = (BASE_SPEED * u.def.spd / cost) * (u.embarked ? 0.9 : 1) * dt;
  const k = Math.min(1, spd / d);
  u.x += dx * k;
  u.y += dy * k;
}

function healUnit(u, dt) {
  if (u.hp >= u.maxHp) return;
  const x = clamp(Math.floor(u.x), 0, W - 1), y = clamp(Math.floor(u.y), 0, H - 1);
  const owner = world.owner[idx(x, y)];
  if (owner !== u.owner) return;                       // only heal at home
  const inCity = !!cityAt(x, y);
  u.hp = Math.min(u.maxHp, u.hp + dt * (inCity ? 4.0 : 1.4));
}

function compactUnits() {
  const live = game.units.filter(u => !u.dead);
  const remap = new Map();
  live.forEach((u, i) => { remap.set(u.i, i); u.i = i; });
  game.units = live;
  game.sel.units = game.sel.units.map(i => remap.get(i)).filter(i => i !== undefined);
  if (game.sel.kind === 'unit') {
    const n = remap.get(game.sel.idx);
    if (n === undefined) game.sel = { kind: null, idx: -1, units: [] };
    else game.sel.idx = n;
  }
}

/* ── targeting ────────────────────────────────────────────────────────────── */

function resolveTarget(order) {
  if (order.targetKind === 'unit') {
    const t = game.units[order.target];
    return t && !t.dead ? t : null;
  }
  const c = game.cities[order.target];
  return c && !c.dead ? c : null;
}

const isCity = t => t && t.blds !== undefined;
const targetTile = t => (isCity(t) ? { x: t.x, y: t.y } : { x: Math.floor(t.x), y: Math.floor(t.y) });

function targetInRange(u, t) {
  const tp = targetTile(t);
  const d = cheb(Math.floor(u.x), Math.floor(u.y), tp.x, tp.y);
  const reach = u.def.range || 1;
  if (d > reach) return false;
  // A city is a valid target for anyone; enemy units must still be hostile.
  if (isCity(t)) return t.owner !== u.owner;
  return t.owner !== u.owner;
}

/** Closest hostile unit or city within `range` tiles. */
export function nearestEnemy(u, range) {
  let best = null, bd = Infinity;
  const ux = Math.floor(u.x), uy = Math.floor(u.y);
  for (const o of game.units) {
    if (o.dead || o.owner === u.owner) continue;
    if (o.embarked && u.def.sea === undefined && false) continue;
    const d = cheb(ux, uy, Math.floor(o.x), Math.floor(o.y));
    if (d <= range && d < bd) { bd = d; best = o; }
  }
  if (!u.def.sea) {
    for (const c of game.cities) {
      if (c.dead || c.owner === u.owner) continue;
      const d = cheb(ux, uy, c.x, c.y);
      if (d <= Math.max(1, range) && d < bd) { bd = d; best = c; }
    }
  }
  return best;
}

/* ── combat ───────────────────────────────────────────────────────────────── */

function defenceMultiplier(u) {
  const x = clamp(Math.floor(u.x), 0, W - 1), y = clamp(Math.floor(u.y), 0, H - 1);
  const t = world.terr[idx(x, y)];
  let m = 1 + (TERRAIN_DEF[t] || 0) + (u.fortified || 0) * 0.4;
  const c = cityAt(x, y);
  if (c && c.owner === u.owner) m += 0.35 + c.defBonus * 0.5;
  // Standing on your own finished border wall is almost as good as a fort.
  const bi = world.bld[idx(x, y)];
  if (bi >= 0) {
    const b = game.buildings[bi];
    if (b && !b.dead && b.id === 'borderwall' && b.owner === u.owner
        && (b.progress ?? 1) >= 1 && b.phase !== 'strip') {
      m += 0.55;
    }
  }
  if (u.embarked) m -= 0.45;                    // caught at sea in an open boat
  return Math.max(0.35, m);
}

function tryStrike(attacker, target) {
  if (attacker.cd > 0 || !canFight(attacker)) return;
  attacker.cd = STRIKE_INTERVAL;
  attacker.flash = 1;

  if (isCity(target)) strikeCity(attacker, target);
  else strikeUnit(attacker, target);
}

function strikeUnit(a, d) {
  let dmg = a.atk * randRange(0.85, 1.15) * fuelFactor(a);
  if (a.def.vsCav && d.def.cav) dmg *= a.def.vsCav;
  if (d.embarked) dmg *= 1.5;
  dmg /= defenceMultiplier(d);
  dmg = Math.max(1, dmg);

  d.hp -= dmg;
  d.flash = 1;
  addFx('spark', d.x, d.y, game.empires[a.owner].col);

  if (d.hp <= 0) {
    killUnit(d, a.owner);
    if (a.owner === game.player) addFx('text', d.x, d.y, '#7ee787', '+kill');
    return;
  }

  // Melee gets hit back; ranged and siege strike from outside reach.
  const melee = !a.def.range || a.def.range <= 1;
  if (melee && canFight(d)) {
    let back = d.atk * randRange(0.6, 0.9) / defenceMultiplier(a);
    a.hp -= Math.max(1, back);
    a.flash = 1;
    if (a.hp <= 0) killUnit(a, d.owner);
  }
}

function strikeCity(a, c) {
  let dmg = a.atk * randRange(0.85, 1.15) * (a.def.vsCity || 1) * fuelFactor(a);
  dmg /= 1 + c.defBonus;
  if (c.protect > 0) dmg *= 0.2;        // just-captured cities dig in for a while
  dmg = Math.max(1, dmg);

  c.hp -= dmg;
  c.siege = 2.5;
  c.flash = 1;
  addFx('spark', c.x + randRange(-0.4, 0.4), c.y + randRange(-0.4, 0.4), game.empires[a.owner].col);

  // The garrison shoots back at anyone standing next to the walls.
  const adjacent = cheb(Math.floor(a.x), Math.floor(a.y), c.x, c.y) <= 1;
  if (adjacent && c.pop > 0) {
    const garrison = (2 + c.pop * 0.32) * (1 + c.defBonus * 0.5);
    a.hp -= Math.max(1, garrison * randRange(0.5, 0.9) / defenceMultiplier(a));
    a.flash = 1;
    if (a.hp <= 0) { killUnit(a, c.owner); return; }
  }

  // Aircraft can flatten a city but never hold one — somebody has to walk in.
  if (c.hp <= 0) {
    if (a.def.air) c.hp = 1;
    else captureCity(c, a.owner);
  }
}

/* ── settling ─────────────────────────────────────────────────────────────── */

/** @returns {{ok:boolean, why?:string}} whether a city may be planted here. */
export function canSettle(x, y) {
  if (!inBounds(x, y)) return { ok: false, why: 'Off the map' };
  const ti = idx(x, y);
  const t = world.terr[ti];
  if (isWater(t)) return { ok: false, why: 'Not in the sea' };
  if (t === T.MOUNTAIN || t === T.PEAK) return { ok: false, why: 'Too steep' };
  if (world.city[ti] >= 0) return { ok: false, why: 'A city already stands here' };
  for (const c of game.cities) {
    if (c.dead) continue;
    if (cheb(c.x, c.y, x, y) < MIN_CITY_GAP) return { ok: false, why: `Too close to ${c.name}` };
  }
  return { ok: true };
}

function tryFoundCity(u) {
  const x = Math.floor(u.x), y = Math.floor(u.y);
  const emp = game.empires[u.owner];
  const check = canSettle(x, y);
  if (!check.ok) {
    if (emp.isPlayer) logEvent(`Settler can't build here — ${check.why.toLowerCase()}.`, 'info');
    u.order = null; u.state = 'idle';
    return false;
  }

  const c = foundCity(emp, x, y, null);
  // The settler's citizens become the new city's first inhabitants.
  c.pop = Math.max(c.pop, u.def.pop);
  recomputeCity(c);
  c.hp = c.maxHp;
  recomputeEmpire(emp);
  killUnitSilently(u);
  addFx('ring', x + 0.5, y + 0.5, emp.col);
  logEvent(emp.isPlayer ? `${c.name} has been founded.` : `${emp.name} founded ${c.name}.`,
    emp.isPlayer ? 'good' : 'info');
  markDirty('territory', 'minimap');
  return true;
}

/** A settler that becomes a city isn't a casualty, so it must not count as one. */
function killUnitSilently(u) {
  u.dead = true;
  if (game.sel.units.length) game.sel.units = game.sel.units.filter(i => i !== u.i);
  if (game.sel.kind === 'unit' && game.sel.idx === u.i) game.sel = { kind: null, idx: -1, units: [] };
}

/* ── city upkeep between fights ───────────────────────────────────────────── */

export function updateCities(dt) {
  for (const c of game.cities) {
    if (c.dead) continue;
    if (c.flash > 0) c.flash = Math.max(0, c.flash - dt * 2);
    if (c.protect > 0) c.protect -= dt;
    if (c.siege > 0) { c.siege -= dt; continue; }
    if (c.hp < c.maxHp) c.hp = Math.min(c.maxHp, c.hp + dt * 3.5);
  }
}
