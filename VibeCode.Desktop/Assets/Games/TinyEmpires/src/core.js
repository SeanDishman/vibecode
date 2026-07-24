// core.js — world constants, deterministic randomness, noise and small maths helpers.
// Leaf module: imports nothing, so everything else is free to depend on it.

export const W = 200, H = 124;          // world size in tiles — one tile is one map "pixel"
export const TILES = W * H;
// Screen pixels per tile. The top of the range is deliberately far in — close
// enough to see individual villagers walking between houses — and the renderer
// layers on extra terrain detail as you climb it.
export const ZOOMS = [2, 3, 4, 5, 6, 8, 11, 15, 20, 27, 36, 48, 64];
// Start close enough that the little people and buildings read as pixel art;
// the whole world is still a few scroll clicks away.
export const DEFAULT_ZOOM = 6;          // index into ZOOMS

/** Detail tiers the renderer switches on as you zoom in. */
export const LOD_DETAIL = 13;   // scenery: trees, rocks, tufts, waves
export const LOD_FINE = 22;     // denser scenery, shadows, shoreline foam

/** Terrain ids. Order matters: anything <= SHALLOW counts as water. */
export const T = {
  DEEP: 0, OCEAN: 1, SHALLOW: 2,
  BEACH: 3, GRASS: 4, PLAINS: 5, SAVANNA: 6, FOREST: 7, JUNGLE: 8,
  MARSH: 9, HILLS: 10, MOUNTAIN: 11, PEAK: 12, DESERT: 13, TUNDRA: 14, SNOW: 15,
};
export const WATER_MAX = T.SHALLOW;
export const isWater = t => t <= WATER_MAX;
export const isDeep = t => t <= T.OCEAN;

/** Map special resources. OIL is strategic: it sits on the map from turn one but
    stays invisible and worthless until an empire researches Combustion. */
export const R = { NONE: 0, FISH: 1, WHEAT: 2, GAME: 3, ORE: 4, GEMS: 5, STONE: 6, HORSES: 7, OIL: 8 };

// Neighbour offsets, used all over the place.
export const DX8 = [1, 1, 0, -1, -1, -1, 0, 1], DY8 = [0, 1, 1, 1, 0, -1, -1, -1];
export const DX4 = [1, 0, -1, 0], DY4 = [0, 1, 0, -1];

/* ── deterministic randomness ─────────────────────────────────────────────── */

let rngState = 1;
export function srand(seed) { rngState = (seed >>> 0) || 1; }
export function rand() {                    // xorshift32 — same seed, same world
  rngState ^= rngState << 13; rngState >>>= 0;
  rngState ^= rngState >>> 17;
  rngState ^= rngState << 5;  rngState >>>= 0;
  return rngState / 4294967296;
}
export const randInt = n => (rand() * n) | 0;
export const randRange = (a, b) => a + rand() * (b - a);
export const pick = arr => arr[(rand() * arr.length) | 0];
export const chance = p => rand() < p;

/* ── maths ────────────────────────────────────────────────────────────────── */

export const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
export const lerp = (a, b, t) => a + (b - a) * t;
export const smooth = t => t * t * (3 - 2 * t);
export const idx = (x, y) => y * W + x;
export const tx = i => i % W;
export const ty = i => (i / W) | 0;
export const inBounds = (x, y) => x >= 0 && y >= 0 && x < W && y < H;
export const dist2 = (ax, ay, bx, by) => { const dx = ax - bx, dy = ay - by; return dx * dx + dy * dy; };
export const dist = (ax, ay, bx, by) => Math.sqrt(dist2(ax, ay, bx, by));
/** Chebyshev distance — the "tiles away" players actually perceive on a square grid. */
export const cheb = (ax, ay, bx, by) => Math.max(Math.abs(ax - bx), Math.abs(ay - by));

/* ── value noise ──────────────────────────────────────────────────────────── */

export function hash2(x, y, seed) {
  let h = Math.imul(x | 0, 374761393) ^ Math.imul(y | 0, 668265263) ^ Math.imul(seed | 0, 1442695041);
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
}

export function vnoise(x, y, seed) {
  const xi = Math.floor(x), yi = Math.floor(y);
  const u = smooth(x - xi), v = smooth(y - yi);
  const a = hash2(xi, yi, seed),     b = hash2(xi + 1, yi, seed);
  const c = hash2(xi, yi + 1, seed), d = hash2(xi + 1, yi + 1, seed);
  return lerp(lerp(a, b, u), lerp(c, d, u), v);
}

export function fbm(x, y, seed, octaves = 5, gain = 0.5, lac = 2.0) {
  let amp = 1, freq = 1, sum = 0, norm = 0;
  for (let o = 0; o < octaves; o++) {
    sum += amp * vnoise(x * freq, y * freq, seed + o * 1013);
    norm += amp; amp *= gain; freq *= lac;
  }
  return sum / norm;
}

/* ── formatting ───────────────────────────────────────────────────────────── */

/** 4000 BC … AD 1712 — the year readout in the top bar. */
export function formatYear(y) {
  return y < 0 ? `${Math.abs(Math.round(y))} BC` : `AD ${Math.max(1, Math.round(y))}`;
}

export function shortNum(n) {
  const a = Math.abs(n);
  if (a >= 10000) return (n / 1000).toFixed(a >= 100000 ? 0 : 1) + 'k';
  return String(Math.round(n));
}

export const signed = n => (n > 0 ? '+' : '') + shortNum(n);
