// economy.js — everything that turns land and people into numbers: which tiles a
// city works, what it yields, how it grows, and what the research tree allows.
// Pure computation over state; it never creates or destroys entities.

import { W, H, T, idx, inBounds, isWater, clamp, cheb, DX4, DY4 } from './core.js';
import { world, game, empireCities } from './store.js';
import { TERRAIN, RESOURCES, BLD, UNI, TECH, TECHS, BUILDINGS, ERAS, OIL_TECH } from './data.js';

/** Oil is worthless — and invisible — until an empire works out how to burn it. */
export const seesOil = emp => !!emp && emp.techs.has(OIL_TECH);

/* ── research helpers ─────────────────────────────────────────────────────── */

export const hasTech = (emp, id) => !id || emp.techs.has(id);

/** A tech is available once every prerequisite is in. */
export function techAvailable(emp, id) {
  const t = TECH[id];
  if (!t || emp.techs.has(id)) return false;
  return t.req.every(r => emp.techs.has(r));
}

export function availableTechs(emp) {
  return TECHS.filter(t => techAvailable(emp, t.id));
}

/** Era = the highest era the empire has actually finished a tech in, +1 once deep in. */
export function eraOf(emp) {
  const last = ERAS.length - 1;
  let era = 0;
  for (const id of emp.techs) era = Math.max(era, TECH[id].era);
  const inEra = [...emp.techs].filter(id => TECH[id].era === era).length;
  // Advance the label once most of an era is done, so the HUD tracks progress.
  if (inEra >= 4 && era < last) era += 1;
  return clamp(era, 0, last);
}

export function startResearch(emp, id) {
  if (!techAvailable(emp, id)) return false;
  emp.researching = id;
  return true;
}

/** Science needed for the current pick, after global multipliers. */
export const techCost = id => TECH[id].cost;

/* ── tile yields ──────────────────────────────────────────────────────────── */

/** What one citizen pulls out of a single tile, including any building on it. */
export function tileYield(ti, emp) {
  const t = world.terr[ti];
  const td = TERRAIN[t];
  let food = td.food, gold = td.gold, sci = 0;

  const rid = world.res[ti];
  if (rid) {
    const r = RESOURCES[rid];
    if (!r.strategic || seesOil(emp)) { food += r.food; gold += r.gold; sci += r.sci; }
  }
  if (world.river[ti] && !isWater(t)) food += 1;

  const bi = world.bld[ti];
  if (bi >= 0) {
    const b = game.buildings[bi];
    // Incomplete / stripping sites pay nothing until the workers finish.
    if (b && !b.dead && b.owner === emp.i && (b.progress ?? 1) >= 1 && b.phase !== 'strip') {
      const d = BLD[b.id];
      food += d.food || 0;
      gold += d.gold || 0;
      sci += d.sci || 0;
      if (d.riverBonus && world.river[ti]) food += d.riverBonus;
    }
  }
  return { food, gold, sci };
}

const yieldScore = y => y.food * 1.7 + y.gold * 1.2 + y.sci * 1.6;

/* ── city recomputation ───────────────────────────────────────────────────── */

/** Recalculate a city's worked tiles, per-turn yields and all its modifiers. */
export function recomputeCity(c) {
  if (c.dead) return;
  const emp = game.empires[c.owner];

  // 1 · modifiers contributed by buildings sitting in the city centre
  let flatFood = 0, flatGold = 0, flatSci = 0, culture = 1, oil = 0;
  let housing = 6, def = 0, vet = 0, unitCap = 0;
  let foodPct = 0, goldPct = 0, sciPct = 0, growPct = 0;
  let ships = false, navy = false, air = false;

  for (const bi of c.blds) {
    const b = game.buildings[bi];
    if (!b || b.dead) continue;
    // Scaffolding and half-built walls don't feed, house or defend yet.
    if ((b.progress ?? 1) < 1 || b.phase === 'strip') continue;
    const d = BLD[b.id];
    if (d.city) {                       // tile buildings pay out through worked tiles instead
      flatFood += d.food || 0;
      flatGold += d.gold || 0;
      flatSci += d.sci || 0;
    }
    // Oil is extracted, not worked: a well pays out whether or not a citizen
    // happens to be standing on that tile this turn.
    oil += d.oil || 0;
    culture += d.culture || 0;
    housing += d.housing || 0;
    def += d.def || 0;
    vet += d.vet || 0;
    unitCap += d.unitCap || 0;
    foodPct += d.foodPct || 0;
    goldPct += d.goldPct || 0;
    sciPct += d.sciPct || 0;
    growPct += d.growPct || 0;
    if (d.ships) ships = true;
    if (d.navy) navy = true;
    if (d.air) air = true;
  }

  // 2 · pick the best tiles in range — one per citizen
  const r = Math.round(c.radius);
  const cand = [];
  for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) {
    const x = c.x + dx, y = c.y + dy;
    if (!inBounds(x, y) || (dx === 0 && dy === 0)) continue;
    if (dx * dx + dy * dy > r * r + r) continue;
    const ti = idx(x, y);
    if (world.owner[ti] !== c.owner) continue;
    if (world.city[ti] >= 0) continue;               // another city's centre
    const y2 = tileYield(ti, emp);
    cand.push({ ti, y: y2, s: yieldScore(y2) });
  }
  cand.sort((a, b) => b.s - a.s);

  const workers = Math.min(c.pop, cand.length);
  let food = 2, gold = 2, sci = 1;                    // the city centre itself
  c.worked.length = 0;
  for (let i = 0; i < workers; i++) {
    const t = cand[i];
    food += t.y.food; gold += t.y.gold; sci += t.y.sci;
    c.worked.push(t.ti);
  }

  // 3 · flat additions, then percentages
  food += flatFood; gold += flatGold; sci += flatSci;
  if (emp.techs.has('printing')) sci *= 1.35;
  if (emp.techs.has('enlighten')) { sci *= 1.20; gold *= 1.20; }
  food *= 1 + foodPct;
  gold *= 1 + goldPct;
  sci *= 1 + sciPct;

  c.yields.food = food;
  c.yields.gold = gold;
  c.yields.sci = sci;
  c.yields.oil = oil;
  c.yields.culture = culture;
  c.canAir = air;
  c.housing = housing;
  c.defBonus = def;
  c.vetBonus = vet;
  c.capUnits = unitCap;
  c.growPct = growPct;
  c.canShips = ships;
  c.canNavy = navy;
  c.stage = cityStage(c);
  c.maxHp = 100 + (c.capital ? 40 : 0) + Math.round(c.pop * 4) + Math.round(def * 60);
  c.hp = Math.min(c.hp, c.maxHp);
}

export function cityStage(c) {
  const pop = c.pop;
  return pop >= 22 ? 3 : pop >= 13 ? 2 : pop >= 6 ? 1 : 0;
}

/** Food banked before the next citizen is born. */
export const growthCost = pop => Math.round(14 + pop * pop * 1.15 + pop * 5);

/** Surplus after the citizens have eaten. */
export const foodSurplus = c => c.yields.food - c.pop * 0.85;

export function recomputeEmpire(e) {
  const cities = empireCities(e);
  let gold = 0, sci = 0, food = 0, oil = 0, cap = 4;
  for (const c of cities) {
    gold += c.yields.gold;
    sci += c.yields.sci;
    oil += c.yields.oil || 0;
    food += foodSurplus(c);
    cap += 2 + c.capUnits;
  }
  // Armies cost money; the first few are free so early play isn't punished.
  let units = 0, oilUp = 0;
  for (const u of game.units) {
    if (u.dead || u.owner !== e.i) continue;
    units++;
    oilUp += u.def.oilUp || 0;
  }
  const upkeep = Math.max(0, units - 4) * 1.5;

  // National character shows up here: a merchant realm banks far more than a
  // fading one from the same land, which is what makes some rivals soft.
  const m = e.mult || { gold: 1, sci: 1 };
  e.incGold = gold * m.gold - upkeep;
  e.incSci = sci * m.sci;
  e.incFood = food;
  e.incOil = oil - oilUp;
  e.oilUp = oilUp;
  e.upkeep = upkeep;
  e.unitCap = cap;
  e.era = eraOf(e);
  e.techCount = e.techs.size;
  // An empire that has run its tanks and aircraft dry fights badly until the
  // wells catch up. Cheaper and more legible than per-unit fuel tanks.
  e.dry = seesOil(e) && e.oil <= 0 && e.incOil < 0;
}

export function recomputeAll() {
  for (const c of game.cities) if (!c.dead) recomputeCity(c);
  for (const e of game.empires) if (!e.dead) recomputeEmpire(e);
}

/* ── build legality ───────────────────────────────────────────────────────── */

export function cityHas(c, id) {
  return c.blds.some(i => {
    const b = game.buildings[i];
    // Count sites still going up so you can't queue a second Barracks.
    return b && !b.dead && b.phase !== 'strip' && b.id === id;
  });
}

/** Which of the player's cities owns this tile for building purposes. */
export function cityCoveringTile(emp, x, y) {
  let best = null, bd = Infinity;
  for (const c of game.cities) {
    if (c.dead || c.owner !== emp.i) continue;
    const d = cheb(c.x, c.y, x, y);
    if (d <= Math.round(c.radius) && d < bd) { bd = d; best = c; }
  }
  return best;
}

/**
 * Can this empire drop a tile-building here?
 * @returns {{ok:boolean, why?:string, city?:object}}
 */
export function canPlace(emp, defId, x, y) {
  const d = BLD[defId];
  if (!d) return { ok: false, why: 'Unknown building' };
  if (d.city) return { ok: false, why: 'Built from the city panel' };
  if (!inBounds(x, y)) return { ok: false, why: 'Off the map' };
  if (!hasTech(emp, d.tech)) return { ok: false, why: 'Needs ' + TECH[d.tech].name };

  const ti = idx(x, y);
  if (world.owner[ti] !== emp.i) return { ok: false, why: 'Outside your borders' };
  if (world.city[ti] >= 0) return { ok: false, why: 'That is a city centre' };
  if (world.bld[ti] >= 0) {
    const existing = game.buildings[world.bld[ti]];
    if (existing && !existing.dead && existing.phase !== 'strip') {
      return { ok: false, why: 'Something is already built here' };
    }
  }
  if (d.on && !d.on.includes(world.terr[ti])) return { ok: false, why: `Not on ${TERRAIN[world.terr[ti]].name.toLowerCase()}` };
  if (d.coast && !world.coast[ti]) return { ok: false, why: 'Must touch the sea' };
  if (d.res) {
    const r = RESOURCES[world.res[ti]];
    if (!r || r.key !== d.res) return { ok: false, why: `Must sit on ${d.res.toLowerCase()}` };
  }
  // Border walls only go up on tiles that face a living rival.
  if (d.wall) {
    let facesFoe = false;
    for (let k = 0; k < 4; k++) {
      const nx = x + DX4[k], ny = y + DY4[k];
      if (!inBounds(nx, ny)) continue;
      const oi = world.owner[idx(nx, ny)];
      if (oi < 0 || oi === emp.i) continue;
      const foe = game.empires[oi];
      if (foe && !foe.dead) { facesFoe = true; break; }
    }
    if (!facesFoe) return { ok: false, why: 'Must touch an enemy border' };
  }

  const city = cityCoveringTile(emp, x, y);
  if (!city) return { ok: false, why: 'Too far from any city' };
  if (emp.gold < d.cost) return { ok: false, why: `Needs ${d.cost} gold` };
  return { ok: true, city };
}

/** Can this city add a city-centre building? */
export function canBuildInCity(emp, c, defId) {
  const d = BLD[defId];
  if (!d || !d.city) return { ok: false, why: 'Placed on a tile instead' };
  if (!hasTech(emp, d.tech)) return { ok: false, why: 'Needs ' + TECH[d.tech].name };
  if (cityHas(c, defId)) return { ok: false, why: 'Already built here' };
  if (d.needs && !cityHas(c, d.needs)) return { ok: false, why: 'Needs a ' + BLD[d.needs].name };
  if (d.coast && !world.coast[c.ti]) return { ok: false, why: 'This city is inland' };
  if (emp.gold < d.cost) return { ok: false, why: `Needs ${d.cost} gold` };
  return { ok: true };
}

/** Can this city train that unit right now? */
export function canTrain(emp, c, unitId) {
  const d = UNI[unitId];
  if (!d) return { ok: false, why: 'Unknown unit' };
  if (!hasTech(emp, d.tech)) return { ok: false, why: 'Needs ' + TECH[d.tech].name };
  if (d.sea && !c.canShips) return { ok: false, why: 'Needs a Harbour' };
  if (d.air && !c.canAir) return { ok: false, why: 'Needs an Airfield' };
  if (d.needBld && !cityHas(c, d.needBld)) return { ok: false, why: 'Needs a ' + BLD[d.needBld].name };
  if (emp.gold < d.cost) return { ok: false, why: `Needs ${d.cost} gold` };
  if (d.oil && (emp.oil || 0) < d.oil) return { ok: false, why: `Needs ${d.oil} oil` };
  if (c.pop <= d.pop) return { ok: false, why: 'Not enough citizens' };

  let units = 0;
  for (const u of game.units) if (!u.dead && u.owner === emp.i) units++;
  if (units >= emp.unitCap) return { ok: false, why: `Army is full (${units}/${emp.unitCap})` };
  return { ok: true };
}

/** Buildings this empire could ever place on tiles, in tech order — drives the build rail. */
/** Tile buildings this empire should see. Once a housing tier is researched the
    tier below it drops out of the palette — you never want to place the obsolete
    version, and existing ones upgrade themselves anyway. */
export function tileBuildings(emp) {
  // Sorted by era so the palette's era headings run in historical order — the
  // declaration order in data.js groups related buildings, not chronology.
  const byEra = (a, b) => (a.era - b.era) || (a.cost - b.cost);
  const all = BUILDINGS.filter(b => !b.city).sort(byEra);
  if (!emp) return all;
  const superseded = new Set(
    all.filter(d => d.upgradeFrom && hasTech(emp, d.tech)).map(d => d.upgradeFrom));
  return all.filter(d => !superseded.has(d.id));
}
export function cityBuildings() { return BUILDINGS.filter(b => b.city); }

/* ── movement cost ────────────────────────────────────────────────────────── */

/** Terrain cost for a unit, or Infinity where it simply cannot go. */
export function moveCost(u, x, y) {
  if (!inBounds(x, y)) return Infinity;
  // Aircraft ignore the map entirely — mountains, ocean and enemy borders alike.
  if (u.def.air) return 0.75;

  const ti = idx(x, y);
  const t = world.terr[ti];
  const emp = game.empires[u.owner];
  const water = isWater(t);

  if (u.def.sea) {
    if (!water) return Infinity;                                  // ships stay wet
    if (t === T.DEEP && !emp.techs.has('navigation')) return Infinity;
    return TERRAIN[t].move;
  }
  if (water) {
    // Land units need Sailing to touch water at all, and open sea beyond that.
    if (!emp.techs.has('sailing')) return Infinity;
    if (t !== T.SHALLOW && !emp.techs.has('shipbuilding')) return Infinity;
    if (t === T.DEEP && !emp.techs.has('navigation')) return Infinity;
    return TERRAIN[t].move * 1.15;
  }
  let cost = TERRAIN[t].move;
  // Completed enemy border walls are a slog — pathing prefers the long way round.
  const bi = world.bld[idx(x, y)];
  if (bi >= 0) {
    const b = game.buildings[bi];
    if (b && !b.dead && b.id === 'borderwall' && b.owner !== u.owner
        && (b.progress ?? 1) >= 1 && b.phase !== 'strip') {
      cost *= 4.2;
    }
  }
  return cost;
}
