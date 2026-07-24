// vision.js — fog of war for the human player. `explored` is permanent, `vis`
// is what is lit right now. Recomputed a few times a second, not every frame.

import { W, H, TILES, idx, inBounds } from './core.js';
import { world, game, markDirty } from './store.js';

let accum = 0;
const INTERVAL = 0.25;      // seconds of game time between refreshes

export function updateVision(force = false, dt = 0) {
  accum += dt;
  if (!force && accum < INTERVAL) return;
  accum = 0;

  world.vis.fill(0);
  const me = game.player;

  for (const c of game.cities) {
    if (c.dead || c.owner !== me) continue;
    reveal(c.x, c.y, Math.round(c.radius) + 2);
  }
  for (const u of game.units) {
    if (u.dead || u.owner !== me) continue;
    reveal(Math.floor(u.x), Math.floor(u.y), u.def.sight);
  }
  markDirty('fog');
}

function reveal(cx, cy, r) {
  const r2 = r * r + r;
  const x0 = Math.max(0, cx - r), x1 = Math.min(W - 1, cx + r);
  const y0 = Math.max(0, cy - r), y1 = Math.min(H - 1, cy + r);
  for (let y = y0; y <= y1; y++) {
    const dy = y - cy;
    for (let x = x0; x <= x1; x++) {
      const dx = x - cx;
      if (dx * dx + dy * dy > r2) continue;
      const i = y * W + x;
      world.vis[i] = 1;
      world.explored[i] = 1;
    }
  }
}

/** Reveal everything — used when a game ends so the final map is readable. */
export function revealAll() {
  world.vis.fill(1);
  world.explored.fill(1);
  markDirty('fog');
}

export const tileVisible = (x, y) => inBounds(x, y) && world.vis[idx(x, y)] === 1;
export const tileExplored = (x, y) => inBounds(x, y) && world.explored[idx(x, y)] === 1;
