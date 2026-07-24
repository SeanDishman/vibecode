// turn.js — the heartbeat. There are no turns: the economy, growth and research
// all accumulate continuously off elapsed time, and the year ticks up with them.
//
// Balance is still authored in "per cycle" units (a cycle is CYCLE seconds) so
// every yield number in data.js keeps its meaning; the tick just pays out the
// fraction of a cycle that actually elapsed. `game.turn` survives only as an
// internal age counter for AI pacing — it is never shown to the player.

import { clamp, formatYear } from './core.js';
import { world, game, empireCities, markDirty, livingEmpires } from './store.js';
import { TECH, BLD, UNI, BUILDINGS, OIL_TECH, VICTORY_TECH } from './data.js';
import {
  recomputeCity, recomputeEmpire, recomputeAll, foodSurplus, growthCost, eraOf,
  availableTechs, startResearch,
} from './economy.js';
import { claimTerritory, syncVillagers, addFx, killEmpire, cityRoles } from './entities.js';
import { runAI } from './ai.js';
import { revealAll } from './vision.js';
import { logEvent } from './log.js';

/** Seconds of game time that one "unit" of the authored per-cycle yields covers. */
export const CYCLE = 3.0;
/** How often the economy actually settles up. Fine enough to look continuous,
    coarse enough that we aren't re-costing every empire sixty times a second. */
const ECON_STEP = 0.25;
const AI_INTERVAL = 2.6;      // seconds between one rival's decisions

/** Years fly by early on and slow down as the eras advance, like the real thing. */
const yearsPerSecond = () => Math.max(2, 22 - game.clock / 30) / CYCLE;

/** Culture banked before the borders push out another ring. */
const CULTURE_STEPS = [30, 100, 240, 999999];

/** Advance the whole world by `dt` seconds of game time. */
export function tickWorld(dt) {
  game.clock += dt;
  game.year += yearsPerSecond() * dt;
  // Kept only so AI pacing (first-war timing, expansion appetite) still has a
  // sense of how old the game is. Nothing player-facing reads this.
  game.turn = Math.floor(game.clock / CYCLE);

  game.econT += dt;
  while (game.econT >= ECON_STEP) {
    game.econT -= ECON_STEP;
    economyStep(ECON_STEP);
  }

  // Rivals think on their own staggered clocks rather than all at once.
  for (const e of game.empires) {
    if (e.dead || e.isPlayer) continue;
    e.aiT = (e.aiT || 0) - dt;
    if (e.aiT <= 0) {
      e.aiT = AI_INTERVAL * (0.85 + Math.random() * 0.3);
      runAI(e);
    }
  }
}

function economyStep(step) {
  const f = step / CYCLE;                 // fraction of a cycle's worth of yield

  for (const e of game.empires) {
    if (e.dead) continue;

    recomputeEmpire(e);
    e.gold += e.incGold * f;
    e.oil = Math.max(0, (e.oil || 0) + e.incOil * f);
    if (e.gold < 0) bankruptcy(e);

    advanceResearch(e, f);
    for (const c of empireCities(e)) growCity(c, e, f);
  }

  game.villagerT = (game.villagerT || 0) + step;
  if (game.villagerT >= 1.5) { game.villagerT = 0; syncVillagers(); }

  checkVictory();
}

/* ── research ─────────────────────────────────────────────────────────────── */

function advanceResearch(e, f) {
  e.sci += e.incSci * f;
  if (!e.researching) return;

  e.sciInto += e.incSci * f;
  const cost = TECH[e.researching].cost;
  if (e.sciInto < cost) return;

  const id = e.researching;
  e.sciInto -= cost;
  e.techs.add(id);
  e.researching = null;
  e.era = eraOf(e);

  for (const c of empireCities(e)) recomputeCity(c);
  recomputeEmpire(e);

  if (e.isPlayer) {
    const t = TECH[id];
    logEvent(`${t.name} discovered — ${t.eff.toLowerCase()}.`, 'tech');
    // Roll straight onto the next cheapest tech. Idle science is invisible and
    // feels like a bug to anyone who hasn't opened the tree yet.
    const open = availableTechs(e).sort((a, b) => a.cost - b.cost);
    if (open.length) {
      startResearch(e, open[0].id);
      logEvent(`Now researching ${open[0].name} — press T to change.`, 'tech');
    }
  }
  applyHousingUpgrades(e);

  // Discovering how to burn oil literally redraws the map, so force a repaint.
  if (id === OIL_TECH && e.isPlayer) {
    markDirty('terrain', 'minimap');
    logEvent('Oil fields are now visible across the world.', 'tech');
  }
  if (id === VICTORY_TECH) winGame(e, 'space');
}

/**
 * A better house doesn't mean bulldozing the old one. When a housing tech lands,
 * every existing home of the superseded tier is retargeted at the new tier and
 * knocked back to a part-built state — which is exactly the condition
 * construction.js hands to idle villagers, so they walk out to each house in turn
 * and physically rebuild it. Yields step up only as each one is finished.
 */
export function applyHousingUpgrades(e) {
  const upgrades = BUILDINGS.filter(d => d.upgradeFrom && e.techs.has(d.tech));
  if (!upgrades.length) return;

  const next = new Map(upgrades.map(d => [d.upgradeFrom, d]));
  let count = 0, top = null;

  for (const b of game.buildings) {
    if (!b || b.dead || b.owner !== e.i || b.phase === 'strip') continue;
    // Climb the whole ladder at once: a hut jumps straight to the best tier the
    // empire knows rather than crawling up one tech at a time.
    let target = null, guard = 0;
    let cur = b.id;
    while (next.has(cur) && guard++ < 6) { cur = next.get(cur).id; target = cur; }
    if (!target) continue;

    b.id = target;
    b.progress = Math.min(b.progress, 0.35);
    b.phase = 'build';
    count++;
    top = target;
  }

  if (!count) return;
  for (const c of empireCities(e)) recomputeCity(c);
  recomputeEmpire(e);
  if (e.isPlayer) {
    logEvent(`Builders are upgrading ${count} home${count > 1 ? 's' : ''} to ${BLD[top].name}.`, 'good');
  }
}

/** Seconds of game time left on the current research, for the HUD. */
export function researchEta(e) {
  if (!e.researching) return null;
  const left = TECH[e.researching].cost - e.sciInto;
  if (e.incSci <= 0) return Infinity;
  return Math.max(1, Math.ceil(left / (e.incSci / CYCLE)));
}

/** Per-second rate from an authored per-cycle yield — what the HUD should show. */
export const perSecond = v => v / CYCLE;

/* ── city growth ──────────────────────────────────────────────────────────── */

function growCity(c, e, f) {
  const surplus = foodSurplus(c) * (1 + (c.growPct || 0)) * f;
  const need = growthCost(c.pop);

  if (surplus < 0) {
    // Starvation: eat into the store first, then lose a citizen.
    c.food += surplus;
    if (c.food < 0) {
      c.food = 0;
      if (c.pop > 1) {
        c.pop--;
        recomputeCity(c);
        if (e.isPlayer) logEvent(`${c.name} is starving — a citizen was lost.`, 'war');
      }
    }
  } else if (c.pop >= c.housing) {
    // Out of houses: the surplus piles up but nobody new moves in.
    c.food = Math.min(c.food + surplus, need * 0.95);
  } else {
    c.food += surplus;
    if (c.food >= need) {
      c.food -= need;
      c.pop++;
      recomputeCity(c);
      addFx('text', c.x + 0.5, c.y - 0.2, '#7ee787', '+1');
      if (e.isPlayer && c.pop % 5 === 0) logEvent(`${c.name} has grown to ${c.pop} citizens.`, 'good');
    }
  }

  // Borders creep outwards as culture accumulates.
  c.culture += c.yields.culture;
  const ring = Math.round(c.radius) - 3;
  if (ring < CULTURE_STEPS.length && c.culture >= CULTURE_STEPS[ring] && c.radius < 6) {
    c.radius += 1;
    c.culture = 0;
    claimTerritory(c);
    recomputeCity(c);
    if (e.isPlayer) logEvent(`${c.name}'s borders have expanded.`, 'good');
  }
}

/** Broke empires disband their priciest unit rather than going into the red.
    Rate-limited: the economy settles four times a second, and without a cooldown
    a deficit would dissolve an entire army inside a couple of seconds. */
function bankruptcy(e) {
  if (game.clock < (e.bankruptT || 0)) { e.gold = Math.max(0, e.gold); return; }
  e.bankruptT = game.clock + 5;

  let worst = null;
  for (const u of game.units) {
    if (u.dead || u.owner !== e.i) continue;
    if (!worst || u.def.cost > worst.def.cost) worst = u;
  }
  if (worst) {
    worst.dead = true;
    if (e.isPlayer) logEvent(`Treasury empty — the ${worst.def.name.toLowerCase()} disbanded.`, 'war');
  }
  e.gold = Math.max(0, e.gold);
}

/* ── win and loss ─────────────────────────────────────────────────────────── */

export function checkVictory() {
  const me = game.empires[game.player];

  // Losing every city ends the run. This must not be conditional on `me.dead`
  // being unset: capturing the last city already flips that flag via
  // killEmpire(), and an early return there would swallow the defeat screen.
  if (empireCities(me).length === 0) {
    me.dead = true;
    if (game.mode === 'playing') endGame('over');
    return;
  }

  const rivals = game.empires.filter(e => !e.dead && e.i !== game.player);
  if (!me.dead && rivals.length === 0) winGame(me, 'conquest');
}

function winGame(e, kind) {
  if (!e.isPlayer || game.mode === 'win') return;
  game.winKind = kind;
  endGame('win');
}

function endGame(mode) {
  game.mode = mode;
  revealAll();
  markDirty('fog');
}

/* ── readouts for the HUD ─────────────────────────────────────────────────── */

export function empireSummary(e) {
  const cities = empireCities(e);
  let pop = 0, fighters = 0, settlers = 0;
  for (const c of cities) {
    const r = cityRoles(c);
    pop += r.total; fighters += r.fighters; settlers += r.settlers;
  }
  return { cities: cities.length, pop, fighters, settlers };
}

export const yearLabel = () => formatYear(game.year);
