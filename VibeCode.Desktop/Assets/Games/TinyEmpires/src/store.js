// store.js — the shared mutable state: the tile arrays, the game object and the
// camera. Deliberately a leaf module (imports only core) so every other module
// can read state without creating import cycles.

import { TILES, W, H, DEFAULT_ZOOM, ZOOMS } from './core.js';

/** One entry per tile. Flat typed arrays keep 24 800 tiles cheap to scan every frame. */
export const world = {
  terr:     new Uint8Array(TILES),   // terrain id
  elev:     new Float32Array(TILES), // 0..1 height
  res:      new Uint8Array(TILES),   // resource id (0 = none)
  land:     new Int16Array(TILES),   // landmass component id, -1 for water
  owner:    new Int8Array(TILES),    // empire index owning the tile, -1 = unclaimed
  bld:      new Int32Array(TILES),   // building index, -1 = none
  city:     new Int32Array(TILES),   // city index when this tile is a city centre
  river:    new Uint8Array(TILES),   // 1 when a river runs through
  coast:    new Uint8Array(TILES),   // land tile touching water
  explored: new Uint8Array(TILES),   // the player has seen it at some point
  vis:      new Uint8Array(TILES),   // the player can see it right now
  seed: 0,
};

/** Land components, biggest first — used to seat empires on real continents. */
export const landmasses = [];

export const game = {
  mode: 'menu',              // menu | playing | pause | over | win
  empires: [], cities: [], units: [], buildings: [], villagers: [], fx: [],
  player: 0,
  difficulty: 1,
  // `clock` is elapsed game seconds and is the real timebase; `turn` survives
  // only as a derived age counter that AI pacing reads. There are no turns.
  clock: 0, econT: 0, villagerT: 0,
  turn: 0, year: -4000,
  speed: 1, time: 0,
  sel: { kind: null, idx: -1, units: [] },   // kind: 'city' | 'unit' | 'tile' | null
  place: null,               // id of the building currently being placed
  winKind: null,
  stats: { kills: 0, losses: 0, founded: 0, captured: 0, lost: 0 },
};

export const camera = { x: W / 2, y: H / 2, zi: DEFAULT_ZOOM };
export const zoom = () => ZOOMS[camera.zi];

/** Cache invalidation flags for the layered renderer. */
export const dirty = { terrain: true, territory: true, fog: true, minimap: true };
export const markDirty = (...keys) => { for (const k of keys) dirty[k] = true; };

/* ── tiny shared helpers over state ───────────────────────────────────────── */

export const alive = o => !!o && !o.dead;
export const player = () => game.empires[game.player];
export const empireOf = i => game.empires[i];
export const cityById = i => (i >= 0 ? game.cities[i] : null);
export const unitById = i => (i >= 0 ? game.units[i] : null);

/** The city occupying this tile, or null. */
export function cityAt(x, y) {
  if (x < 0 || y < 0 || x >= W || y >= H) return null;
  const ci = world.city[y * W + x];
  return ci >= 0 && !game.cities[ci].dead ? game.cities[ci] : null;
}

/** The building on this tile, or null. */
export function bldAt(x, y) {
  if (x < 0 || y < 0 || x >= W || y >= H) return null;
  const bi = world.bld[y * W + x];
  return bi >= 0 && !game.buildings[bi].dead ? game.buildings[bi] : null;
}

export const empireCities = e => game.cities.filter(c => !c.dead && c.owner === e.i);
export const empireUnits  = e => game.units.filter(u => !u.dead && u.owner === e.i);
export const livingEmpires = () => game.empires.filter(e => !e.dead);
