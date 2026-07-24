// ai.js — the rival empires. Runs once per turn, not per frame. Each empire
// spends its turn on one priority: research, then growth, then expansion, then
// an army, and finally a war it thinks it can win.

import { W, H, idx, inBounds, clamp, cheb, dist, rand, randInt, pick, chance, isWater, T } from './core.js';
import { world, game, empireCities, markDirty } from './store.js';
import { BUILDINGS, BLD, UNITS, UNI, TECHS, TECH, DIFFS, TERRAIN } from './data.js';
import {
  hasTech, techAvailable, availableTechs, startResearch,
  canPlace, canBuildInCity, canTrain, recomputeCity, recomputeEmpire, tileYield, cityCoveringTile,
} from './economy.js';
import { addBuilding, addUnit, nearestCity, cityRoles } from './entities.js';
import { orderMove, orderAttack, orderFound, orderFortify, canSettle } from './military.js';
import { fortifyBorder, borderingFoes } from './construction.js';

/** Techs the AI reaches for first, roughly in the order a human would. */
const AI_PRIORITY = [
  'agriculture', 'mining', 'pottery', 'bronze', 'masonry', 'writing', 'woodworking',
  'horseback', 'currency', 'ironworking', 'sailing', 'mathematics', 'philosophy',
  'engineering', 'feudalism', 'machinery', 'education', 'chivalry', 'shipbuilding',
  'banking', 'theology', 'navigation', 'gunpowder', 'astronomy', 'metallurgy',
  // Industrial and modern: head for Combustion reasonably early, because oil
  // gates the entire modern roster and an empire without it falls behind fast.
  'printing', 'enlighten', 'steam', 'industrial', 'rifling', 'electricity',
  'ballistics', 'combustion', 'railroad', 'steel', 'sanitation', 'massprod',
  'armor', 'flight', 'radio', 'computers', 'space',
];

export function runAI(emp) {
  if (emp.dead || emp.isPlayer) return;
  const diff = DIFFS[game.difficulty];
  const cities = empireCities(emp);
  if (!cities.length) return;

  chooseResearch(emp);
  spendGold(emp, cities, diff);
  commandArmy(emp, cities, diff);
}

/* ── research ─────────────────────────────────────────────────────────────── */

function chooseResearch(emp) {
  if (emp.researching && !emp.techs.has(emp.researching)) return;
  const open = availableTechs(emp);
  if (!open.length) { emp.researching = null; return; }

  for (const id of AI_PRIORITY) {
    if (open.some(t => t.id === id)) { startResearch(emp, id); return; }
  }
  // Anything left: take the cheapest.
  open.sort((a, b) => a.cost - b.cost);
  startResearch(emp, open[0].id);
}

/* ── building ─────────────────────────────────────────────────────────────── */

function spendGold(emp, cities, diff) {
  // Never drain the treasury completely — leave a war chest.
  let budget = emp.gold - 15;
  if (budget <= 0) return;

  // 1 · city-centre buildings, best value first
  for (const c of cities) {
    if (budget <= 0) break;
    const wanted = pickCityBuilding(emp, c);
    if (wanted && wanted.cost <= budget) {
      emp.gold -= wanted.cost;
      budget -= wanted.cost;
      addBuilding(emp, wanted.id, c.x, c.y, c);
    }
  }

  // 2 · tile improvements around each city (skip border walls — handled below)
  for (const c of cities) {
    if (budget <= 0) break;
    const spot = pickTileBuilding(emp, c);
    if (spot && spot.def.cost <= budget) {
      emp.gold -= spot.def.cost;
      budget -= spot.def.cost;
      addBuilding(emp, spot.def.id, spot.x, spot.y, c);
    }
  }

  // 3 · when pressed or late enough, wall a short stretch of the nearest frontier.
  // Cap segments so the AI does not dump its entire treasury into masonry.
  if (budget >= 18 && hasTech(emp, 'masonry') &&
      (cities.some(c => c.siege > 0 || c.hp < c.maxHp * 0.9) || game.turn >= diff.warTurn)) {
    const foes = borderingFoes(emp);
    if (foes.length) {
      const maxSeg = Math.min(5, Math.floor(budget / 18));
      const before = emp.gold;
      fortifyBorder(emp, foes[0].i, maxSeg);
      budget -= Math.max(0, before - emp.gold);
    }
  }
}

function pickCityBuilding(emp, c) {
  let best = null, bestScore = -1;
  for (const d of BUILDINGS) {
    if (!d.city) continue;
    if (!canBuildInCity(emp, c, d.id).ok) continue;
    // Value growth and money early, science and defence once established.
    let s = (d.food || 0) * 3 + (d.gold || 0) * 2.4 + (d.sci || 0) * 2.6
          + (d.foodPct || 0) * 22 + (d.goldPct || 0) * 20 + (d.sciPct || 0) * 20
          + (d.housing || 0) * 1.6 + (d.culture || 0) * 1.2
          + (d.unitCap || 0) * 2.2 + (d.def || 0) * 6 + (d.vet || 0) * 12
          // Oil wells, airfields and shipyards gate entire unit classes, so they
          // have to be valued for what they unlock rather than what they yield —
          // on raw yield a shipyard scores negative and never gets built at all.
          + (d.oil || 0) * 8 + (d.air ? 10 : 0) + (d.navy ? 12 : 0);
    s -= d.cost * 0.045;
    if (s > bestScore) { bestScore = s; best = d; }
  }
  return bestScore > 0 ? best : null;
}

function pickTileBuilding(emp, c) {
  const r = Math.round(c.radius);
  let best = null, bestScore = 0;
  for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
    const x = c.x + dx, y = c.y + dy;
    if (!inBounds(x, y) || (dx === 0 && dy === 0)) continue;
    const ti = idx(x, y);
    if (world.owner[ti] !== emp.i || world.bld[ti] >= 0 || world.city[ti] >= 0) continue;

    for (const d of BUILDINGS) {
      if (d.city || d.wall) continue;   // walls are queued as a frontier line
      if (!canPlace(emp, d.id, x, y).ok) continue;
      // Oil is weighted heavily: without wells the whole modern roster is dead
      // weight, and a well costs far more than the gold it returns.
      let s = (d.food || 0) * 3.2 + (d.gold || 0) * 2.4 + (d.housing || 0) * 1.4
            + (d.oil || 0) * 14 - d.cost * 0.05;
      if (d.riverBonus && world.river[ti]) s += 2;
      if (world.res[ti]) s += 1.5;
      if (s > bestScore) { bestScore = s; best = { def: d, x, y }; }
    }
  }
  return best;
}

/* ── army and war ─────────────────────────────────────────────────────────── */

function commandArmy(emp, cities, diff) {
  const units = game.units.filter(u => !u.dead && u.owner === emp.i);
  const soldiers = units.filter(u => u.def.role !== 'settler');
  const settlers = units.filter(u => u.def.role === 'settler');

  // Difficulty sets the baseline; the nation's own character scales it, so a
  // warlike great power and a fading minor realm behave nothing alike.
  const m = emp.mult || { expand: 1, army: 1, aggr: 1 };
  const aggression = diff.aggression * m.aggr;
  const wantCities = Math.round((2 + game.turn / 22 + (diff.aggression > 1 ? 1 : 0)) * m.expand);
  const wantArmy = clamp(Math.round((2 + cities.length * 1.6) * aggression * m.army), 1, emp.unitCap);

  // 1 · expand while there is room
  if (cities.length < wantCities && settlers.length === 0) {
    trainBest(emp, cities, 'settler');
  }
  // 2 · keep an army
  if (soldiers.length < wantArmy) {
    trainBest(emp, cities, null);
  }

  // 2b · and a small navy. trainBest() deliberately ignores sea units so a
  // battleship's huge attack can't crowd out the land army, which meant rivals
  // happily built shipyards and then never put a single hull in the water.
  const fleet = units.filter(u => u.def.sea);
  const wantFleet = clamp(Math.round(cities.length * 0.6), 1, 5);
  if (fleet.length < wantFleet) {
    const port = cities.find(c => c.canNavy) || cities.find(c => c.canShips);
    if (port) {
      let best = null, bestScore = -1;
      for (const d of UNITS) {
        if (!d.sea) continue;
        if (!canTrain(emp, port, d.id).ok) continue;
        const s = d.atk * 1.6 + d.hp * 0.25 - d.cost * 0.09;
        if (s > bestScore) { bestScore = s; best = d; }
      }
      if (best) doTrain(emp, port, best.id);
    }
  }

  // 3 · give every idle unit something to do
  for (const u of units) {
    if (u.order || u.state === 'move' || u.state === 'attack') continue;

    if (u.def.role === 'settler') {
      const spot = findSettleSpot(emp, u);
      if (spot) orderFound(u, spot.x, spot.y);
      continue;
    }
    if (u.def.sea) { patrolShip(emp, u); continue; }

    // Defend a threatened city first.
    const threatened = cities.find(c => c.siege > 0 || c.hp < c.maxHp * 0.85);
    if (threatened && cheb(Math.floor(u.x), Math.floor(u.y), threatened.x, threatened.y) > 2) {
      orderMove(u, threatened.x, threatened.y);
      continue;
    }
    // Otherwise join the war effort, once the army is big enough and the
    // early-game peace has run out.
    if (game.turn >= diff.warTurn && soldiers.length >= wantArmy * 0.75 && chance(0.6 * aggression)) {
      const prey = pickWarTarget(emp, u);
      if (prey) { orderAttack(u, 'city', prey.i); continue; }
    }
    // Nothing to do: garrison the nearest city.
    const home = nearestCity(u.x, u.y, c => c.owner === emp.i);
    if (home && cheb(Math.floor(u.x), Math.floor(u.y), home.x, home.y) > 2) orderMove(u, home.x, home.y);
    else orderFortify(u);
  }
}

function trainBest(emp, cities, forceId) {
  // Train in the city that can actually afford the best thing right now.
  for (const c of cities) {
    if (forceId) {
      if (canTrain(emp, c, forceId).ok) { doTrain(emp, c, forceId); return true; }
      continue;
    }
    let best = null, bestScore = -1;
    for (const d of UNITS) {
      if (d.role === 'settler' || d.sea) continue;
      if (!canTrain(emp, c, d.id).ok) continue;
      const s = d.atk * 1.6 + d.hp * 0.25 + (d.range || 0) * 3 - d.cost * 0.09;
      if (s > bestScore) { bestScore = s; best = d; }
    }
    if (best) { doTrain(emp, c, best.id); return true; }
  }
  return false;
}

function doTrain(emp, c, unitId) {
  const d = UNI[unitId];
  emp.gold -= d.cost;
  if (d.oil) emp.oil = Math.max(0, (emp.oil || 0) - d.oil);
  c.pop -= d.pop;
  addUnit(emp, unitId, c.x, c.y, c);
  recomputeCity(c);
  recomputeEmpire(emp);
}

/** A decent patch of unclaimed land within reach of the empire. */
function findSettleSpot(emp, settler) {
  const from = nearestCity(settler.x, settler.y, c => c.owner === emp.i);
  const ox = from ? from.x : Math.floor(settler.x);
  const oy = from ? from.y : Math.floor(settler.y);
  const myLand = world.land[idx(clamp(Math.floor(settler.x), 0, W - 1), clamp(Math.floor(settler.y), 0, H - 1))];

  let best = null, bestScore = 0;
  for (let tries = 0; tries < 260; tries++) {
    const a = rand() * Math.PI * 2;
    const r = 5 + rand() * 16;
    const x = clamp(Math.round(ox + Math.cos(a) * r), 1, W - 2);
    const y = clamp(Math.round(oy + Math.sin(a) * r), 1, H - 2);
    const ti = idx(x, y);
    if (isWater(world.terr[ti])) continue;
    if (world.land[ti] !== myLand) continue;             // don't strand settlers overseas
    if (world.owner[ti] !== -1 && world.owner[ti] !== emp.i) continue;
    if (!canSettle(x, y).ok) continue;

    let s = 0;
    for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) {
      const nx = x + dx, ny = y + dy;
      if (!inBounds(nx, ny)) continue;
      const nti = idx(nx, ny);
      const td = TERRAIN[world.terr[nti]];
      s += td.food * 1.4 + td.gold;
      if (world.res[nti]) s += 2.5;
      if (world.river[nti]) s += 1.5;
    }
    if (world.coast[ti]) s += 4;
    s -= dist(ox, oy, x, y) * 0.35;
    if (s > bestScore) { bestScore = s; best = { x, y }; }
  }
  return best;
}

/** Nearest enemy city that this unit can plausibly reach. */
function pickWarTarget(emp, u) {
  const ux = Math.floor(u.x), uy = Math.floor(u.y);
  const myLand = world.land[idx(clamp(ux, 0, W - 1), clamp(uy, 0, H - 1))];
  const canCross = hasTech(emp, 'sailing');

  let best = null, bd = Infinity;
  for (const c of game.cities) {
    if (c.dead || c.owner === emp.i) continue;
    if (!canCross && world.land[c.ti] !== myLand) continue;   // no boats yet, no overseas war
    const d = dist(ux, uy, c.x, c.y);
    // Prefer weak, close cities.
    const score = d + (c.defBonus * 14) - (c.hp < c.maxHp * 0.6 ? 12 : 0);
    if (score < bd) { bd = score; best = c; }
  }
  return best;
}

function patrolShip(emp, u) {
  // Warships hunt enemy coastal cities, or drift around home waters.
  const prey = pickWarTarget(emp, u);
  if (prey && world.coast[prey.ti] && chance(0.5)) { orderAttack(u, 'city', prey.i); return; }
  for (let i = 0; i < 40; i++) {
    const x = clamp(Math.floor(u.x) + randInt(21) - 10, 0, W - 1);
    const y = clamp(Math.floor(u.y) + randInt(21) - 10, 0, H - 1);
    if (isWater(world.terr[idx(x, y)])) { orderMove(u, x, y); return; }
  }
}
