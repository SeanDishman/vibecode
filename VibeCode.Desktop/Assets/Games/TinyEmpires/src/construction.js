// construction.js — how buildings actually go up (and come down). Every order
// becomes a site that villagers walk to and assemble piece by piece; when a
// building is stripped, the same progress bar runs backwards before it dies.
// Border walls are just a tile-building with extra placement rules plus a
// "fortify this whole frontier" helper that queues a line of them.

import { W, H, idx, inBounds, clamp, dist2, cheb, isWater, DX4, DY4 } from './core.js';
import { world, game, markDirty } from './store.js';
import { BLD, TECH } from './data.js';
import { hasTech, cityCoveringTile, recomputeCity, recomputeEmpire } from './economy.js';
import { addBuilding, addFx } from './entities.js';
import { logEvent } from './log.js';

/** Seconds a 22-gold building needs with one worker. Scales with cost. */
const BUILD_COST_REF = 22;
const BUILD_BASE_SEC = 3.2;
/** Tiny trickle so a site with no free hands still finishes eventually. */
const IDLE_WORK_RATE = 0.10;
/** Extra rate per villager standing on (or next to) the site. */
const WORKER_RATE = 0.38;
/** City-centre buildings pull workers from the population, not the map. */
const CITY_WORKER_CAP = 4;
/** How close a villager has to be (tiles) to count as working. */
const WORK_RADIUS = 0.85;

export const isComplete = b => !!b && !b.dead && b.progress >= 1 && b.phase === 'build';
export const isBuilding = b => !!b && !b.dead && b.phase === 'build' && b.progress < 1;
export const isStripping = b => !!b && !b.dead && b.phase === 'strip';

/** Seconds of work a finished building represents at one worker. */
export function buildDuration(defId) {
  const d = BLD[defId];
  if (!d) return BUILD_BASE_SEC;
  return Math.max(2.0, BUILD_BASE_SEC * (d.cost / BUILD_COST_REF));
}

/* ── border detection ─────────────────────────────────────────────────────── */

/** True when this owned tile sits next to a living rival's land. */
export function isEnemyBorderTile(empIdx, x, y) {
  if (!inBounds(x, y)) return false;
  const ti = idx(x, y);
  if (world.owner[ti] !== empIdx) return false;
  if (isWater(world.terr[ti])) return false;
  for (let k = 0; k < 4; k++) {
    const nx = x + DX4[k], ny = y + DY4[k];
    if (!inBounds(nx, ny)) continue;
    const oi = world.owner[idx(nx, ny)];
    if (oi < 0 || oi === empIdx) continue;
    const foe = game.empires[oi];
    if (foe && !foe.dead) return true;
  }
  return false;
}

/** Living rival empires that share at least one edge with this empire. */
export function borderingFoes(emp) {
  const found = new Map();
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const ti = idx(x, y);
      if (world.owner[ti] !== emp.i) continue;
      for (let k = 0; k < 4; k++) {
        const nx = x + DX4[k], ny = y + DY4[k];
        if (!inBounds(nx, ny)) continue;
        const oi = world.owner[idx(nx, ny)];
        if (oi < 0 || oi === emp.i || found.has(oi)) continue;
        const foe = game.empires[oi];
        if (foe && !foe.dead) found.set(oi, foe);
      }
    }
  }
  return [...found.values()];
}

/**
 * Own tiles that touch a specific rival (or any rival if foeIdx is null).
 * Skips tiles that already have a live building.
 */
export function borderTilesVs(emp, foeIdx = null) {
  const out = [];
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const ti = idx(x, y);
      if (world.owner[ti] !== emp.i) continue;
      if (world.city[ti] >= 0) continue;
      if (world.bld[ti] >= 0) {
        const b = game.buildings[world.bld[ti]];
        if (b && !b.dead) continue;
      }
      if (isWater(world.terr[ti])) continue;

      let touches = false;
      for (let k = 0; k < 4; k++) {
        const nx = x + DX4[k], ny = y + DY4[k];
        if (!inBounds(nx, ny)) continue;
        const oi = world.owner[idx(nx, ny)];
        if (oi < 0 || oi === emp.i) continue;
        if (foeIdx != null && oi !== foeIdx) continue;
        const foe = game.empires[oi];
        if (foe && !foe.dead) { touches = true; break; }
      }
      if (touches) out.push({ x, y, ti });
    }
  }
  return out;
}

/** Which rival (if any) this tile faces — first neighbour found. */
export function foeFacingTile(empIdx, x, y) {
  for (let k = 0; k < 4; k++) {
    const nx = x + DX4[k], ny = y + DY4[k];
    if (!inBounds(nx, ny)) continue;
    const oi = world.owner[idx(nx, ny)];
    if (oi < 0 || oi === empIdx) continue;
    const foe = game.empires[oi];
    if (foe && !foe.dead) return foe;
  }
  return null;
}

/**
 * Queue Border Walls along the shared frontier with one rival.
 * Spends gold up front per segment; villagers assemble them over time.
 * @returns {{ok:boolean, why?:string, count?:number, cost?:number}}
 */
export function fortifyBorder(emp, foeIdx, maxCount = Infinity) {
  if (!hasTech(emp, 'masonry')) {
    return { ok: false, why: 'Needs ' + TECH.masonry.name };
  }
  const def = BLD.borderwall;
  const tiles = borderTilesVs(emp, foeIdx).filter(t => {
    // canPlace-ish: terrain must be allowed and a city must cover it.
    if (def.on && !def.on.includes(world.terr[t.ti])) return false;
    return !!cityCoveringTile(emp, t.x, t.y);
  });
  if (!tiles.length) return { ok: false, why: 'No open frontier tiles against them' };

  const affordable = Math.min(tiles.length, Math.floor(emp.gold / def.cost), maxCount);
  if (affordable <= 0) return { ok: false, why: `Needs ${def.cost} gold per segment` };

  // Prefer tiles closest to our cities so workers reach them first.
  tiles.sort((a, b) => {
    const ca = cityCoveringTile(emp, a.x, a.y);
    const cb = cityCoveringTile(emp, b.x, b.y);
    const da = ca ? cheb(ca.x, ca.y, a.x, a.y) : 99;
    const db = cb ? cheb(cb.x, cb.y, b.x, b.y) : 99;
    return da - db;
  });

  let n = 0, spent = 0;
  for (let i = 0; i < affordable; i++) {
    const t = tiles[i];
    const city = cityCoveringTile(emp, t.x, t.y);
    if (!city) continue;
    // Race: something may have claimed the tile since the scan.
    if (world.bld[t.ti] >= 0) {
      const existing = game.buildings[world.bld[t.ti]];
      if (existing && !existing.dead) continue;
    }
    emp.gold -= def.cost;
    spent += def.cost;
    addBuilding(emp, 'borderwall', t.x, t.y, city);
    n++;
  }
  if (!n) return { ok: false, why: 'No tiles could be walled' };

  const foe = game.empires[foeIdx];
  if (emp.isPlayer) {
    logEvent(
      n === 1
        ? `Border wall ordered against ${foe.name} (${spent}g) — workers are on the way.`
        : `${n} border walls ordered against ${foe.name} (${spent}g) — workers are assembling the line.`,
      'good');
  }
  return { ok: true, count: n, cost: spent };
}

/* ── per-frame construction ───────────────────────────────────────────────── */

export function updateConstruction(dt) {
  assignBuilders();
  for (const b of game.buildings) {
    if (!b || b.dead) continue;
    if (b.phase === 'strip') {
      stepStrip(b, dt);
      continue;
    }
    if (b.progress >= 1) continue;
    stepBuild(b, dt);
  }
}

function stepBuild(b, dt) {
  const d = BLD[b.id];
  if (!d) { b.progress = 1; finishBuild(b); return; }

  const dur = buildDuration(b.id);
  const workers = workerCount(b);
  const rate = (IDLE_WORK_RATE + workers * WORKER_RATE) / dur;
  b.progress = Math.min(1, b.progress + rate * dt);
  b.workers = workers;

  // Dust while hands are on the job.
  if (workers > 0 && Math.random() < dt * 1.6) {
    addFx('puff', b.x + 0.5 + (Math.random() - 0.5) * 0.5,
      b.y + 0.55 + (Math.random() - 0.5) * 0.3, game.empires[b.owner].col);
  }

  if (b.progress >= 1) finishBuild(b);
}

function finishBuild(b) {
  b.progress = 1;
  b.phase = 'build';
  b.workers = 0;
  clearJobsFor(b.i);
  const c = game.cities[b.city];
  const emp = game.empires[b.owner];
  if (c && !c.dead) recomputeCity(c);
  if (emp && !emp.dead) recomputeEmpire(emp);
  addFx('ring', b.x + 0.5, b.y + 0.5, emp ? emp.col : '#fff');
  if (emp && emp.isPlayer) {
    const d = BLD[b.id];
    const where = c && !c.dead ? c.name : 'the frontier';
    logEvent(`${d.name} finished near ${where}.`, 'good');
  }
}

function stepStrip(b, dt) {
  const dur = buildDuration(b.id) * 0.55;   // tear-down is quicker than raising
  const workers = Math.max(1, workerCount(b));
  const rate = (0.25 + workers * WORKER_RATE) / dur;
  b.progress = Math.max(0, b.progress - rate * dt);
  if (Math.random() < dt * 2.2) {
    addFx('puff', b.x + 0.5 + (Math.random() - 0.5) * 0.4,
      b.y + 0.5, '#9aa3ad');
  }
  if (b.progress <= 0) finalizeDead(b);
}

/** Kick off a reverse-build so the structure comes down piece by piece. */
export function beginStrip(b) {
  if (!b || b.dead) return;
  if (b.phase === 'strip') return;
  // Already a foundation: just vanish.
  if (b.progress <= 0.05) { finalizeDead(b); return; }
  b.phase = 'strip';
  // Incomplete sites still strip from whatever height they reached.
  if (b.progress < 0.15) b.progress = 0.15;
  // Yields drop immediately while the scaffolding comes down.
  const c = game.cities[b.city];
  if (c && !c.dead) recomputeCity(c);
  const emp = game.empires[b.owner];
  if (emp && !emp.dead) recomputeEmpire(emp);
}

function finalizeDead(b) {
  b.dead = true;
  b.progress = 0;
  clearJobsFor(b.i);
  if (world.bld[b.ti] === b.i) world.bld[b.ti] = -1;
  const c = game.cities[b.city];
  if (c && !c.dead) {
    c.blds = c.blds.filter(i => i !== b.i);
    recomputeCity(c);
    const emp = game.empires[c.owner];
    if (emp && !emp.dead) recomputeEmpire(emp);
  }
  addFx('puff', b.x + 0.5, b.y + 0.5, '#6b7280');
}

/* ── workers ──────────────────────────────────────────────────────────────── */

function workerCount(b) {
  const d = BLD[b.id];
  if (d && d.city) {
    const c = game.cities[b.city];
    if (!c || c.dead) return 1;
    return clamp(1 + Math.floor(c.pop / 4), 1, CITY_WORKER_CAP);
  }
  let n = 0;
  for (const v of game.villagers) {
    if (v.dead || v.jobBi !== b.i) continue;
    if (dist2(v.x, v.y, b.x + 0.5, b.y + 0.5) <= WORK_RADIUS * WORK_RADIUS) n++;
  }
  return n;
}

/** Hand idle townsfolk a build/strip job within their home city's footprint. */
function assignBuilders() {
  const sites = [];
  for (const b of game.buildings) {
    if (!b || b.dead) continue;
    if (b.phase === 'strip' || b.progress < 1) {
      const d = BLD[b.id];
      if (d && d.city) continue;   // city buildings use population, not walkers
      sites.push(b);
    }
  }
  if (!sites.length) {
    for (const v of game.villagers) {
      if (v.jobBi != null) { v.jobBi = null; v.job = null; }
    }
    return;
  }

  // Count current assignments so we don't over-staff one site.
  const assigned = new Map();
  for (const v of game.villagers) {
    if (v.dead || v.jobBi == null) continue;
    assigned.set(v.jobBi, (assigned.get(v.jobBi) || 0) + 1);
  }

  for (const v of game.villagers) {
    if (v.dead) continue;
    const c = game.cities[v.city];
    if (!c || c.dead) { v.jobBi = null; continue; }

    // Keep working a live job.
    if (v.jobBi != null) {
      const b = game.buildings[v.jobBi];
      if (b && !b.dead && (b.progress < 1 || b.phase === 'strip') && b.owner === v.owner) {
        driveVillagerToSite(v, b);
        continue;
      }
      v.jobBi = null; v.job = null;
    }

    // Pick the nearest understaffed site belonging to this city / empire.
    let best = null, bd = Infinity;
    for (const b of sites) {
      if (b.owner !== v.owner) continue;
      // Prefer the home city, but allow help on any of the empire's sites nearby.
      if (b.city !== c.i && cheb(c.x, c.y, b.x, b.y) > Math.round(c.radius) + 1) continue;
      const have = assigned.get(b.i) || 0;
      if (have >= 3) continue;
      const d = dist2(v.x, v.y, b.x + 0.5, b.y + 0.5);
      if (d < bd) { bd = d; best = b; }
    }
    if (best) {
      v.jobBi = best.i;
      v.job = 'build';
      assigned.set(best.i, (assigned.get(best.i) || 0) + 1);
      driveVillagerToSite(v, best);
    }
  }
}

function driveVillagerToSite(v, b) {
  const here = dist2(v.x, v.y, b.x + 0.5, b.y + 0.5);
  if (here <= WORK_RADIUS * WORK_RADIUS) return;          // already on site
  // Already walking toward it — don't re-roll the target every frame.
  if (dist2(v.tx, v.ty, b.x + 0.5, b.y + 0.5) <= 1.2) return;
  v.tx = b.x + 0.5 + (Math.random() - 0.5) * 0.3;
  v.ty = b.y + 0.55 + (Math.random() - 0.5) * 0.2;
  v.wait = 0;
}

function clearJobsFor(bi) {
  for (const v of game.villagers) {
    if (v.jobBi === bi) { v.jobBi = null; v.job = null; }
  }
}

/** Completed border wall on this tile, if any. */
export function wallAt(x, y) {
  if (!inBounds(x, y)) return null;
  const bi = world.bld[idx(x, y)];
  if (bi < 0) return null;
  const b = game.buildings[bi];
  if (!b || b.dead || b.id !== 'borderwall') return null;
  if (b.progress < 1 || b.phase === 'strip') return null;
  return b;
}
