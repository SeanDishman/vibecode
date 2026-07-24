// entities.js — creating, changing and destroying the things on the map:
// cities, buildings, units, the ambient townsfolk and the little visual effects.

import {
  W, H, idx, inBounds, clamp, rand, randRange, pick, dist2, cheb,
} from './core.js';
import { world, game, markDirty, empireCities } from './store.js';
import { CITY_NAMES, UNI, BLD } from './data.js';
import { recomputeCity, recomputeEmpire, cityStage } from './economy.js';
import { logEvent } from './log.js';

/* ── naming ───────────────────────────────────────────────────────────────── */

const usedNames = new Set();
export function resetNames() { usedNames.clear(); }

export function pickCityName() {
  for (let i = 0; i < 60; i++) {
    const n = pick(CITY_NAMES);
    if (!usedNames.has(n)) { usedNames.add(n); return n; }
  }
  return 'Settlement ' + (game.cities.length + 1);
}

/* ── cities ───────────────────────────────────────────────────────────────── */

export function foundCity(emp, x, y, name, capital = false) {
  const ti = idx(x, y);
  const c = {
    i: game.cities.length, x, y, ti, owner: emp.i,
    name: name || pickCityName(),
    pop: capital ? 4 : 2,
    food: 0, culture: 0, radius: 3,
    hp: 100, maxHp: 100,
    blds: [],
    yields: { food: 0, gold: 0, sci: 0, oil: 0, culture: 0 },
    housing: 6, defBonus: 0, vetBonus: 0, capUnits: 0, growPct: 0,
    canShips: false, canNavy: false, canAir: false,
    worked: [], stage: 0,
    capital, dead: false, siege: 0, flash: 0,
  };
  game.cities.push(c);
  world.city[ti] = c.i;
  claimTerritory(c);
  recomputeCity(c);
  c.hp = c.maxHp;
  spawnVillagers(c, 3);
  if (emp.isPlayer) game.stats.founded++;
  return c;
}

/** Paint every tile inside the culture radius with the owner's colour. */
export function claimTerritory(c) {
  const r = Math.round(c.radius);
  for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
    const x = c.x + dx, y = c.y + dy;
    if (!inBounds(x, y)) continue;
    if (dx * dx + dy * dy > r * r + r) continue;
    const ti = idx(x, y);
    const cur = world.owner[ti];
    if (cur === c.owner) continue;
    if (cur !== -1) {
      // Contested ground belongs to whichever city centre is nearer.
      const rival = nearestCityOf(cur, x, y);
      if (rival && dist2(rival.x, rival.y, x, y) <= dx * dx + dy * dy) continue;
    }
    world.owner[ti] = c.owner;
    markDirty('territory', 'minimap');
  }
}

export function nearestCityOf(empIdx, x, y) {
  let best = null, bd = Infinity;
  for (const c of game.cities) {
    if (c.dead || c.owner !== empIdx) continue;
    const d = dist2(c.x, c.y, x, y);
    if (d < bd) { bd = d; best = c; }
  }
  return best;
}

export function nearestCity(x, y, filter) {
  let best = null, bd = Infinity;
  for (const c of game.cities) {
    if (c.dead || (filter && !filter(c))) continue;
    const d = dist2(c.x, c.y, x, y);
    if (d < bd) { bd = d; best = c; }
  }
  return best;
}

/** Citizens who left this city to become units — exactly what the city panel reports. */
export function cityRoles(c) {
  let fighters = 0, settlers = 0, fieldUnits = 0;
  for (const u of game.units) {
    if (u.dead || u.home !== c.i) continue;
    if (u.def.role === 'settler') settlers += u.def.pop;
    else fighters += u.def.pop;
    fieldUnits++;
  }
  return { civilians: c.pop, fighters, settlers, fieldUnits, total: c.pop + fighters + settlers };
}

/** Flip a city to a new owner: halve the population, wreck some of the buildings. */
export function captureCity(c, byEmpire) {
  const from = game.empires[c.owner];
  const to = game.empires[byEmpire];
  if (!to || c.dead) return;

  // Anything trained here loses its home city.
  for (const u of game.units) if (!u.dead && u.home === c.i) u.home = -1;

  // The sack: a third of the buildings come down.
  for (const bi of c.blds.slice()) {
    const b = game.buildings[bi];
    if (b && !b.dead && rand() < 0.34) removeBuilding(b);
  }

  c.owner = byEmpire;
  c.pop = Math.max(1, Math.floor(c.pop / 2));
  c.capital = false;
  c.hp = Math.round(c.maxHp * 0.6);
  c.siege = 0;
  c.flash = 1;
  // Without a grace period a contested city ping-pongs between two armies every
  // few seconds, which reads as noise rather than as a war.
  c.protect = 14;
  for (const v of game.villagers) if (v.city === c.i) v.owner = byEmpire;

  claimTerritory(c);
  recomputeCity(c);
  recomputeEmpire(to);
  recomputeEmpire(from);
  markDirty('territory', 'minimap');

  const mine = byEmpire === game.player, theirs = from.i === game.player;
  if (mine) game.stats.captured++;
  if (theirs) game.stats.lost++;
  logEvent(
    mine ? `You have taken ${c.name} from ${from.name}!`
         : theirs ? `${to.name} has captured ${c.name}!`
                  : `${to.name} captured ${c.name} from ${from.name}.`,
    mine ? 'good' : 'war');

  // An empire with nothing left is finished.
  if (empireCities(from).length === 0) killEmpire(from);
}

export function killEmpire(e) {
  if (e.dead) return;
  e.dead = true;
  for (const u of game.units) if (!u.dead && u.owner === e.i) u.dead = true;
  logEvent(`${e.name} has fallen from history.`, e.i === game.player ? 'war' : 'good');
}

/* ── buildings ────────────────────────────────────────────────────────────── */

export function addBuilding(emp, defId, x, y, city) {
  const b = {
    i: game.buildings.length, id: defId, owner: emp.i,
    x, y, ti: idx(x, y), city: city.i, dead: false, born: game.time,
    // Every structure starts as a foundation. Villagers (or city labour for
    // centre buildings) raise progress to 1; yields and wall effects wait.
    progress: 0, phase: 'build', workers: 0,
  };
  game.buildings.push(b);
  // City-centre buildings share the city tile — don't overwrite a real
  // tile-building sprite under the settlement.
  const def = BLD[defId];
  if (!def || !def.city) world.bld[b.ti] = b.i;
  city.blds.push(b.i);
  // Don't recompute yields yet — incomplete sites pay nothing until finished.
  return b;
}

export function removeBuilding(b) {
  if (!b || b.dead) return;
  // Strip piece-by-piece when there's something to tear down. construction.js
  // ticks phase === 'strip' each frame; foundations just vanish.
  if ((b.progress ?? 1) > 0.08 && b.phase !== 'strip') {
    b.phase = 'strip';
    if (b.progress < 0.15) b.progress = 0.15;
    const c = game.cities[b.city];
    if (c && !c.dead) recomputeCity(c);
    const emp = game.empires[b.owner];
    if (emp && !emp.dead) recomputeEmpire(emp);
    return;
  }
  b.dead = true;
  b.progress = 0;
  if (world.bld[b.ti] === b.i) world.bld[b.ti] = -1;
  const c = game.cities[b.city];
  if (c && !c.dead) {
    c.blds = c.blds.filter(i => i !== b.i);
    recomputeCity(c);
  }
}

/* ── units ────────────────────────────────────────────────────────────────── */

export function addUnit(emp, typeId, x, y, home) {
  const def = UNI[typeId];
  const vet = home ? home.vetBonus : 0;
  const hp = Math.round(def.hp * (1 + vet * 0.5));
  const u = {
    i: game.units.length, id: typeId, def, owner: emp.i,
    x: x + 0.5, y: y + 0.5,
    home: home ? home.i : -1,
    hp, maxHp: hp,
    atk: def.atk * (1 + vet),
    path: null, pathI: 0, repath: 0, stuck: 0,
    order: null,              // { kind:'move'|'attack'|'found', x, y, targetKind, target }
    cd: 0, state: 'idle', embarked: false, fortified: 0,
    bob: rand() * 6.28, flash: 0, dead: false,
  };
  game.units.push(u);
  return u;
}

export function killUnit(u, byEmpire) {
  if (u.dead) return;
  u.dead = true;
  const emp = game.empires[u.owner];
  addFx('puff', u.x, u.y, emp.col);
  if (byEmpire != null && game.empires[byEmpire] && byEmpire !== u.owner) {
    game.empires[byEmpire].kills++;
    if (byEmpire === game.player) game.stats.kills++;
  }
  if (u.owner === game.player) game.stats.losses++;
  if (game.sel.units.length) game.sel.units = game.sel.units.filter(i => i !== u.i);
  if (game.sel.kind === 'unit' && game.sel.idx === u.i) game.sel = { kind: null, idx: -1, units: [] };
}

export function unitsAt(x, y, radius = 0) {
  const out = [];
  for (const u of game.units) {
    if (u.dead) continue;
    if (cheb(Math.floor(u.x), Math.floor(u.y), x, y) <= radius) out.push(u);
  }
  return out;
}

/* ── ambient townsfolk ────────────────────────────────────────────────────
   Purely decorative specks that potter about near their city. They cost nothing
   and do nothing, but a settlement with people moving in it reads as alive. */

export function spawnVillagers(c, n) {
  for (let i = 0; i < n; i++) {
    game.villagers.push({
      city: c.i, owner: c.owner,
      x: c.x + 0.5 + randRange(-1.5, 1.5),
      y: c.y + 0.5 + randRange(-1.5, 1.5),
      tx: c.x + 0.5, ty: c.y + 0.5,
      wait: rand() * 3, dead: false,
      jobBi: null, job: null,          // set by construction.js while building
    });
  }
}

/** Keep roughly one wanderer per two citizens, capped so big empires stay cheap. */
export function syncVillagers() {
  const counts = new Map();
  for (const v of game.villagers) {
    if (v.dead) continue;
    counts.set(v.city, (counts.get(v.city) || 0) + 1);
  }
  for (const c of game.cities) {
    if (c.dead) continue;
    const want = clamp(2 + Math.floor(c.pop / 2), 2, 10);
    const have = counts.get(c.i) || 0;
    if (have < want) spawnVillagers(c, want - have);
  }
  // Drop wanderers whose city is gone.
  if (game.villagers.length > 400) {
    game.villagers = game.villagers.filter(v => !v.dead && game.cities[v.city] && !game.cities[v.city].dead);
  }
}

/** Wander between random points inside the home city's footprint — unless a
 *  construction job has given them a site to walk to. */
export function updateVillagers(dt) {
  for (const v of game.villagers) {
    if (v.dead) continue;
    const c = game.cities[v.city];
    if (!c || c.dead) { v.dead = true; continue; }

    // Builders walk a little faster so the line of walls actually goes up.
    const onJob = v.jobBi != null;
    if (v.wait > 0 && !onJob) { v.wait -= dt; continue; }
    if (v.wait > 0 && onJob) v.wait = 0;

    const dx = v.tx - v.x, dy = v.ty - v.y;
    const d = Math.hypot(dx, dy);
    if (d < 0.12) {
      if (onJob) {
        // Fidget on the site while the structure rises.
        v.tx = v.x + randRange(-0.2, 0.2);
        v.ty = v.y + randRange(-0.15, 0.15);
        v.wait = randRange(0.15, 0.45);
        continue;
      }
      const r = 1 + Math.min(3, c.radius - 1);
      v.tx = c.x + 0.5 + randRange(-r, r);
      v.ty = c.y + 0.5 + randRange(-r, r);
      v.wait = randRange(0.4, 2.4);
      continue;
    }
    const spd = (onJob ? 0.95 : 0.55) * dt;
    v.x += (dx / d) * spd;
    v.y += (dy / d) * spd;
  }
}

/* ── effects ──────────────────────────────────────────────────────────────── */

export function addFx(kind, x, y, col, text) {
  game.fx.push({
    kind, x, y, col, text, t: 0,
    life: kind === 'text' ? 1.5 : kind === 'puff' ? 0.5 : kind === 'ring' ? 0.6 : 0.3,
  });
  if (game.fx.length > 300) game.fx.splice(0, 80);
}

export function updateFx(dt) {
  for (const f of game.fx) f.t += dt;
  if (game.fx.length) game.fx = game.fx.filter(f => f.t < f.life);
}
