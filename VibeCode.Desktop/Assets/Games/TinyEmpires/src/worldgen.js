// worldgen.js — procedural world: continent blobs shaped by domain-warped fbm,
// then latitude/moisture biomes, rivers carved downhill, and scattered resources.

import {
  W, H, TILES, T, R, isWater, idx, inBounds, clamp, lerp, smooth,
  srand, rand, randInt, randRange, chance, fbm, DX8, DY8,
} from './core.js';
import { world, landmasses, markDirty } from './store.js';

const SEA = 0.42;   // normalised height at which land begins

export function generateWorld(seed) {
  srand(seed);
  world.seed = seed;
  world.terr.fill(0); world.res.fill(0); world.river.fill(0); world.coast.fill(0);
  world.owner.fill(-1); world.bld.fill(-1); world.city.fill(-1);
  world.explored.fill(0); world.vis.fill(0);

  const eSeed = randInt(1e6), mSeed = randInt(1e6), wSeed = randInt(1e6);
  buildElevation(eSeed, wSeed);
  paintBiomes(mSeed);
  carveBeaches();
  carveRivers(18 + randInt(12));
  markCoast();
  labelLandmasses();
  scatterResources();
  markDirty('terrain', 'territory', 'fog', 'minimap');
}

/** Continent blobs + warped noise, normalised so sea level always means something. */
function buildElevation(eSeed, wSeed) {
  const blobs = [];
  const blobCount = 3 + randInt(3);
  for (let i = 0; i < blobCount; i++) {
    blobs.push({
      x: randRange(W * 0.16, W * 0.84),
      y: randRange(H * 0.20, H * 0.80),
      r: randRange(Math.min(W, H) * 0.24, Math.min(W, H) * 0.46),
      w: randRange(0.75, 1.25),
    });
  }

  const e = world.elev;
  let lo = Infinity, hi = -Infinity;
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      // Domain warp is what gives coastlines their crinkly, believable shape.
      const wx = x + (fbm(x * 0.035, y * 0.035, wSeed, 3) - 0.5) * 26;
      const wy = y + (fbm(x * 0.035 + 41, y * 0.035 - 17, wSeed + 7, 3) - 0.5) * 26;
      let v = fbm(wx * 0.030, wy * 0.030, eSeed, 6, 0.52);

      let mask = 0;
      for (const b of blobs) {
        const dx = (x - b.x) * b.w, dy = (y - b.y) / b.w;
        const d = Math.sqrt(dx * dx + dy * dy) / b.r;
        if (d < 1) mask = Math.max(mask, Math.pow(1 - d, 1.35));
      }
      // Sink the map edges so continents never clip the border.
      const ex = Math.min(x, W - 1 - x) / (W * 0.13);
      const ey = Math.min(y, H - 1 - y) / (H * 0.13);
      const edge = clamp(Math.min(ex, ey), 0, 1);

      v = (v * 0.52 + mask * 0.68) * lerp(0.30, 1, smooth(edge));
      e[idx(x, y)] = v;
      if (v < lo) lo = v;
      if (v > hi) hi = v;
    }
  }
  const span = Math.max(1e-6, hi - lo);
  for (let i = 0; i < TILES; i++) e[i] = (e[i] - lo) / span;
}

function paintBiomes(mSeed) {
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const i = idx(x, y), e = world.elev[i];
      if (e < SEA) {
        world.terr[i] = e < SEA - 0.16 ? T.DEEP : e < SEA - 0.05 ? T.OCEAN : T.SHALLOW;
        continue;
      }
      const h = (e - SEA) / (1 - SEA);                       // 0..1 above sea level
      const lat = Math.abs(y - (H - 1) / 2) / ((H - 1) / 2);  // 0 equator … 1 pole
      const moist = fbm(x * 0.042, y * 0.042, mSeed, 4);
      const temp = clamp(1.06 - lat * 1.32 - h * 0.55 + (moist - 0.5) * 0.10, 0, 1);

      let t;
      if (h > 0.72) t = T.PEAK;
      else if (h > 0.55) t = T.MOUNTAIN;
      else if (h > 0.38) t = T.HILLS;
      else if (temp < 0.14) t = T.SNOW;
      else if (temp < 0.26) t = T.TUNDRA;
      else if (moist < 0.34 && temp > 0.56) t = T.DESERT;
      else if (moist < 0.40) t = temp > 0.62 ? T.SAVANNA : T.PLAINS;
      else if (moist > 0.70) t = h < 0.10 ? T.MARSH : (temp > 0.72 ? T.JUNGLE : T.FOREST);
      else if (moist > 0.55) t = T.FOREST;
      else t = T.GRASS;

      // Warm mid-height peaks read better as bare mountain than as snow caps.
      if (t === T.PEAK && temp > 0.45 && h < 0.80) t = T.MOUNTAIN;
      world.terr[i] = t;
    }
  }
}

/** Low land touching the sea becomes sand — the biggest "this is a map" cue there is. */
function carveBeaches() {
  const out = [];
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const i = idx(x, y), t = world.terr[i];
    if (isWater(t) || t === T.MOUNTAIN || t === T.PEAK || t === T.SNOW) continue;
    if (world.elev[i] > 0.50) continue;
    let touches = false;
    for (let d = 0; d < 8 && !touches; d++) {
      const nx = x + DX8[d], ny = y + DY8[d];
      if (inBounds(nx, ny) && isWater(world.terr[idx(nx, ny)])) touches = true;
    }
    if (touches && chance(0.82)) out.push(i);
  }
  for (const i of out) world.terr[i] = T.BEACH;
}

/** Walk downhill from high ground to the sea. Rivers are decoration plus a farm bonus. */
function carveRivers(count) {
  for (let n = 0; n < count; n++) {
    let best = -1, bestE = 0;
    for (let tries = 0; tries < 60; tries++) {
      const i = randInt(TILES);
      if (isWater(world.terr[i]) || world.river[i]) continue;
      if (world.elev[i] > bestE) { bestE = world.elev[i]; best = i; }
    }
    if (best < 0 || bestE < 0.62) continue;

    let x = best % W, y = (best / W) | 0, steps = 0;
    while (steps++ < 220) {
      const i = idx(x, y);
      if (isWater(world.terr[i])) break;
      world.river[i] = 1;
      let nx = -1, ny = -1, low = world.elev[i] + 0.004;
      for (let d = 0; d < 8; d++) {
        const cx = x + DX8[d], cy = y + DY8[d];
        if (!inBounds(cx, cy)) continue;
        // A little wobble on the comparison makes rivers meander instead of running straight.
        const e = world.elev[idx(cx, cy)] + (rand() - 0.5) * 0.012;
        if (e < low) { low = e; nx = cx; ny = cy; }
      }
      if (nx < 0) break;                 // ran into a basin
      x = nx; y = ny;
    }
  }
}

function markCoast() {
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const i = idx(x, y);
    if (isWater(world.terr[i])) { world.coast[i] = 0; continue; }
    let c = 0;
    for (let d = 0; d < 8; d++) {
      const nx = x + DX8[d], ny = y + DY8[d];
      if (inBounds(nx, ny) && isWater(world.terr[idx(nx, ny)])) { c = 1; break; }
    }
    world.coast[i] = c;
  }
}

/** Flood-fill land into components so empires can be seated on real continents. */
function labelLandmasses() {
  world.land.fill(-1);
  landmasses.length = 0;
  const stack = new Int32Array(TILES);

  for (let start = 0; start < TILES; start++) {
    if (world.land[start] !== -1 || isWater(world.terr[start])) continue;
    const id = landmasses.length;
    let sp = 0;
    const tiles = [];
    stack[sp++] = start; world.land[start] = id;
    while (sp > 0) {
      const i = stack[--sp];
      tiles.push(i);
      const x = i % W, y = (i / W) | 0;
      for (let d = 0; d < 8; d++) {
        const nx = x + DX8[d], ny = y + DY8[d];
        if (!inBounds(nx, ny)) continue;
        const j = idx(nx, ny);
        if (world.land[j] !== -1 || isWater(world.terr[j])) continue;
        world.land[j] = id; stack[sp++] = j;
      }
    }
    landmasses.push({ id, size: tiles.length, tiles });
  }

  landmasses.sort((a, b) => b.size - a.size);
  // Sorting shuffles the ids, so relabel the tiles to match.
  landmasses.forEach((m, newId) => { for (const i of m.tiles) world.land[i] = newId; m.id = newId; });
}

function scatterResources() {
  for (let i = 0; i < TILES; i++) {
    const t = world.terr[i];
    let r = R.NONE;
    if (t === T.SHALLOW) { if (chance(0.055)) r = R.FISH; }
    else if (t === T.OCEAN) { if (chance(0.012)) r = R.FISH; }
    else if (t === T.GRASS) { if (chance(0.055)) r = R.WHEAT; else if (chance(0.018)) r = R.HORSES; }
    else if (t === T.PLAINS || t === T.SAVANNA) { if (chance(0.035)) r = R.HORSES; else if (chance(0.030)) r = R.WHEAT; }
    else if (t === T.FOREST || t === T.JUNGLE) { if (chance(0.050)) r = R.GAME; }
    else if (t === T.HILLS) { if (chance(0.070)) r = R.ORE; else if (chance(0.035)) r = R.STONE; }
    else if (t === T.MOUNTAIN || t === T.PEAK) { if (chance(0.045)) r = R.ORE; else if (chance(0.014)) r = R.GEMS; }
    else if (t === T.DESERT) { if (chance(0.014)) r = R.GEMS; else if (chance(0.020)) r = R.STONE; }
    else if (t === T.TUNDRA) { if (chance(0.022)) r = R.GAME; }

    // Oil sits under the map from the very first turn but stays hidden until
    // somebody researches Combustion — so the industrial age redraws the world.
    // Density matters more than it looks: a well has to fall inside some city's
    // work radius to ever be built, so a scattering of ~25 fields across the
    // whole map left almost every empire with no oil and no modern army at all.
    // A well only ever gets built if an oil field lands inside some city's work
    // radius. Putting oil solely in deserts and tundra looked right but meant
    // almost nobody could reach it — cities are founded on good land. So there
    // is now a thinner seam through the terrain people actually settle on.
    if (r === R.NONE) {
      const oilChance = t === T.DESERT ? 0.060 : t === T.MARSH ? 0.055 :
                        t === T.TUNDRA ? 0.045 : t === T.SNOW ? 0.040 :
                        t === T.HILLS ? 0.028 : t === T.PLAINS ? 0.026 :
                        t === T.SAVANNA ? 0.024 : t === T.GRASS ? 0.014 : 0;
      if (oilChance && chance(oilChance)) r = R.OIL;
    }
    world.res[i] = r;
  }
}
