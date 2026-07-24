// setup.js — starting a new game: generate a world, score it for decent starting
// positions, and seat every empire with a capital, an escort and a settler.

import { W, H, T, idx, inBounds, clamp, dist, isWater, randInt, rand, pick } from './core.js';
import { world, landmasses, game, camera, markDirty } from './store.js';
import { EMPIRE_NAMES, DIFFS, TERRAIN, PERSONALITIES, POWERS } from './data.js';
import { generateWorld } from './worldgen.js';
import { recomputeAll, recomputeCity, recomputeEmpire, startResearch } from './economy.js';
import { foundCity, addUnit, resetNames } from './entities.js';
import { clearSpriteCache } from './sprites.js';
import { updateVision } from './vision.js';
import { logEvent, clearLog } from './log.js';

/** Weighted pick from POWERS, so minor realms are common and great powers rare. */
function rollPower() {
  const total = POWERS.reduce((s, p) => s + p.weight, 0);
  let r = rand() * total;
  for (const p of POWERS) { r -= p.weight; if (r <= 0) return p; }
  return POWERS[1];
}

function makeEmpire(i, def, isPlayer) {
  // The player has no personality modifiers — those exist to make the rivals
  // differ from each other, not to hand the human a hidden handicap.
  const trait = isPlayer ? null : pick(PERSONALITIES);
  const power = isPlayer ? null : rollPower();
  return {
    i, name: def.name, adj: def.adj, col: def.col, isPlayer,
    trait, power,
    // Combined multipliers the AI and economy read.
    mult: trait && power
      ? { gold: trait.gold * power.mult, sci: trait.sci * power.mult,
          army: trait.army * power.mult, expand: trait.expand, aggr: trait.aggr }
      : { gold: 1, sci: 1, army: 1, expand: 1, aggr: 1 },
    dead: false,
    gold: 60, sci: 0, sciInto: 0, oil: 0,
    techs: new Set(), researching: null, techCount: 0,
    era: 0,
    incGold: 0, incSci: 0, incFood: 0, incOil: 0, oilUp: 0, dry: false, upkeep: 0,
    unitCap: 6, kills: 0,
  };
}

export function newGame(difficulty, rivals) {
  const diff = DIFFS[clamp(difficulty, 0, DIFFS.length - 1)];
  game.difficulty = difficulty;
  // The menu picks the crowd size; fall back to the difficulty's own default.
  const wanted = clamp(rivals || diff.rivals, 1, EMPIRE_NAMES.length - 1);
  game.rivals = wanted;

  // Keep rolling worlds until one has room for everybody. A packed world may
  // simply not fit twelve well-separated capitals, so accept the best we get
  // rather than looping forever.
  let starts = [], attempts = 0, best = [];
  do {
    generateWorld((Math.random() * 2 ** 31) | 0);
    starts = chooseStarts(wanted + 1);
    if (starts.length > best.length) best = starts;
    attempts++;
  } while (starts.length < wanted + 1 && attempts < 14);
  if (starts.length < best.length) starts = best;

  game.empires = []; game.cities = []; game.units = []; game.buildings = [];
  game.villagers = []; game.fx = [];
  game.turn = 0; game.year = -4000; game.time = 0;
  game.clock = 0; game.econT = 0; game.villagerT = 0;
  game.speed = 1;
  game.sel = { kind: null, idx: -1, units: [] };
  game.place = null;
  game.winKind = null;
  game.stats = { kills: 0, losses: 0, founded: 0, captured: 0, lost: 0 };
  resetNames();
  clearSpriteCache();
  clearLog();

  // The player always flies the cyan banner; rivals draw from what's left.
  const pool = EMPIRE_NAMES.slice();
  const playerDef = pool.shift();
  for (let i = 0; i < starts.length; i++) {
    const isPlayer = i === 0;
    const def = isPlayer ? playerDef : pool.splice(randInt(pool.length), 1)[0];
    const e = makeEmpire(i, def, isPlayer);
    if (!isPlayer) e.gold = Math.round(60 * diff.aiGold * e.power.mult);
    game.empires.push(e);
  }
  game.player = 0;

  starts.forEach((s, i) => {
    const e = game.empires[i];
    const c = foundCity(e, s.x, s.y, null, true);
    // A great power opens with a bigger capital; a minor realm with a smaller one.
    if (!e.isPlayer) c.pop = clamp(Math.round(c.pop * e.power.mult), 2, 8);
    recomputeCity(c);
    c.hp = c.maxHp;

    // Escort size tracks the power tier: minor realms are genuinely soft.
    const escort = clamp(2 + (e.isPlayer ? 0 : e.power.units), 1, 4);
    for (let k = 0; k < escort; k++) {
      addUnit(e, 'warrior', clamp(s.x + k, 0, W - 1), s.y, c);
    }
    addUnit(e, 'settler', s.x, clamp(s.y + 1, 0, H - 1), c);
  });

  recomputeAll();
  updateVision(true);
  centerOn(starts[0].x, starts[0].y);
  game.mode = 'playing';
  markDirty('terrain', 'territory', 'fog', 'minimap');

  // Start on something rather than idling: wasted early science is a rough
  // opening for anyone who hasn't found the research tree yet.
  const me = game.empires[0];
  startResearch(me, 'agriculture');

  logEvent(`${me.name} is founded, 4000 BC.`, 'good');
  const rivalsSeated = game.empires.length - 1;
  logEvent(`${rivalsSeated} rival nation${rivalsSeated === 1 ? '' : 's'} share this world.`, 'info');
  // Name the softest neighbour outright — the whole point of varied power tiers
  // is that the player can go looking for an easy first conquest.
  const soft = game.empires
    .filter(e => !e.isPlayer && e.power)
    .sort((a, b) => a.power.mult * a.mult.army - b.power.mult * b.mult.army)[0];
  if (soft) logEvent(`${soft.name} looks weak — ${soft.trait.blurb.toLowerCase()}.`, 'good');
  logEvent('Researching Agriculture — press T to change it.', 'tech');
  return game;
}

export function centerOn(x, y) {
  camera.x = clamp(x, 0, W);
  camera.y = clamp(y, 0, H);
}

/** Score every land tile, then greedily take the best well-separated spots. */
export function chooseStarts(count) {
  const big = landmasses.filter(m => m.size >= 140);
  if (!big.length) return [];

  const cand = [];
  for (const m of big.slice(0, 4)) {
    for (const ti of m.tiles) {
      const x = ti % W, y = (ti / W) | 0;
      if (x < 4 || y < 4 || x > W - 5 || y > H - 5) continue;
      const t = world.terr[ti];
      if (t === T.MOUNTAIN || t === T.PEAK || t === T.SNOW || t === T.DESERT) continue;

      let yieldSum = 0, land = 0, water = 0;
      for (let dy = -3; dy <= 3; dy++) for (let dx = -3; dx <= 3; dx++) {
        const nx = x + dx, ny = y + dy;
        if (!inBounds(nx, ny)) continue;
        const ni = idx(nx, ny);
        const td = TERRAIN[world.terr[ni]];
        // Average the yield over LAND only. Summing raw meant every ocean tile
        // in the window dragged a coastal site down, and measurement showed the
        // result: not one capital in twenty starts was on the coast, which quietly
        // made harbours — and the entire navy — unreachable from your first city.
        if (!isWater(world.terr[ni])) { yieldSum += td.food * 1.5 + td.gold; land++; }
        else water++;
        if (world.res[ni]) yieldSum += 3;
        if (world.river[ni]) yieldSum += 2;
      }
      if (land < 18) continue;                     // don't strand anyone on a rock
      let score = (yieldSum / land) * 13;
      if (world.coast[ti]) score += 16;            // coastal capitals can build harbours
      if (water >= 4 && water <= 28) score += 7;   // a real shoreline, not an islet
      if (world.river[ti]) score += 6;
      score += m.size * 0.004;
      cand.push({ x, y, score });
    }
  }
  cand.sort((a, b) => b.score - a.score);

  // Spacing has to shrink as the world fills up: a fixed 32-tile gap simply
  // cannot seat thirteen capitals, and the relax loop below would do all the
  // work badly. Roughly "share the land out evenly, then leave some elbow room".
  const minSep = clamp(Math.sqrt((W * H * 0.30) / Math.max(1, count)) * 0.95, 9, 34);
  const picked = [];
  const tryFill = sep => {
    for (const c of cand) {
      if (picked.length >= count) return;
      if (picked.every(p => dist(p.x, p.y, c.x, c.y) >= sep)) picked.push(c);
    }
  };
  tryFill(minSep);
  // Relax the spacing rather than fail outright on a cramped world.
  for (let f = 0.75; picked.length < count && f > 0.3; f -= 0.15) tryFill(minSep * f);
  return picked.slice(0, count);
}
