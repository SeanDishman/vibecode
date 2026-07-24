// pathing.js — A* over the tile grid. Scratch buffers are allocated once and
// re-used via a generation stamp, so a path search never clears 24 800 entries.

import { W, H, TILES, idx, inBounds, DX8, DY8 } from './core.js';
import { moveCost } from './economy.js';

const gScore = new Float32Array(TILES);
const cameFrom = new Int32Array(TILES);
const stamp = new Uint32Array(TILES);
const closed = new Uint8Array(TILES);
let generation = 0;

/* A binary min-heap of tile indices keyed by f-score. */
const heapTile = new Int32Array(TILES + 1);
const heapF = new Float32Array(TILES + 1);
let heapSize = 0;

function heapClear() { heapSize = 0; }
function heapPush(tile, f) {
  let i = ++heapSize;
  heapTile[i] = tile; heapF[i] = f;
  while (i > 1) {
    const p = i >> 1;
    if (heapF[p] <= heapF[i]) break;
    const t = heapTile[p], ff = heapF[p];
    heapTile[p] = heapTile[i]; heapF[p] = heapF[i];
    heapTile[i] = t; heapF[i] = ff;
    i = p;
  }
}
function heapPop() {
  const top = heapTile[1];
  heapTile[1] = heapTile[heapSize]; heapF[1] = heapF[heapSize];
  heapSize--;
  let i = 1;
  for (;;) {
    const l = i << 1, r = l + 1;
    let m = i;
    if (l <= heapSize && heapF[l] < heapF[m]) m = l;
    if (r <= heapSize && heapF[r] < heapF[m]) m = r;
    if (m === i) break;
    const t = heapTile[m], ff = heapF[m];
    heapTile[m] = heapTile[i]; heapF[m] = heapF[i];
    heapTile[i] = t; heapF[i] = ff;
    i = m;
  }
  return top;
}

const SQRT2 = Math.SQRT2;
const octile = (ax, ay, bx, by) => {
  const dx = Math.abs(ax - bx), dy = Math.abs(ay - by);
  return (dx + dy) + (SQRT2 - 2) * Math.min(dx, dy);
};

/**
 * Find a walkable route for `u` from (sx,sy) to (gx,gy).
 * @returns {number[]|null} tile indices after the start tile, or null if unreachable.
 */
export function findPath(u, sx, sy, gx, gy, maxNodes = 9000) {
  if (!inBounds(sx, sy) || !inBounds(gx, gy)) return null;
  const start = idx(sx, sy), goal = idx(gx, gy);
  if (start === goal) return [];

  // If the goal itself is impassable, aim for the closest reachable neighbour instead.
  let target = goal;
  if (!isFinite(moveCost(u, gx, gy))) {
    let best = -1, bd = Infinity;
    for (let d = 0; d < 8; d++) {
      const nx = gx + DX8[d], ny = gy + DY8[d];
      if (!inBounds(nx, ny) || !isFinite(moveCost(u, nx, ny))) continue;
      const dd = octile(sx, sy, nx, ny);
      if (dd < bd) { bd = dd; best = idx(nx, ny); }
    }
    if (best < 0) return null;
    target = best;
  }
  const tgx = target % W, tgy = (target / W) | 0;

  generation++;
  heapClear();
  gScore[start] = 0; cameFrom[start] = -1; stamp[start] = generation; closed[start] = 0;
  heapPush(start, octile(sx, sy, tgx, tgy));

  let expanded = 0, bestNode = start, bestH = octile(sx, sy, tgx, tgy);

  while (heapSize > 0) {
    const cur = heapPop();
    if (closed[cur] === 1 && stamp[cur] === generation) continue;
    closed[cur] = 1; stamp[cur] = generation;

    if (cur === target) return rebuild(cur, start);
    if (++expanded > maxNodes) break;

    const cx = cur % W, cy = (cur / W) | 0;
    const cg = gScore[cur];

    for (let d = 0; d < 8; d++) {
      const nx = cx + DX8[d], ny = cy + DY8[d];
      if (!inBounds(nx, ny)) continue;
      const n = idx(nx, ny);
      if (stamp[n] === generation && closed[n] === 1) continue;

      const step = moveCost(u, nx, ny);
      if (!isFinite(step)) continue;
      const diag = (d & 1) === 1;
      // No cutting corners around impassable terrain.
      if (diag && (!isFinite(moveCost(u, cx, ny)) || !isFinite(moveCost(u, nx, cy)))) continue;

      const ng = cg + step * (diag ? SQRT2 : 1);
      if (stamp[n] === generation && ng >= gScore[n]) continue;

      stamp[n] = generation; closed[n] = 0;
      gScore[n] = ng; cameFrom[n] = cur;
      const h = octile(nx, ny, tgx, tgy);
      if (h < bestH) { bestH = h; bestNode = n; }
      heapPush(n, ng + h * 1.02);      // the slight weight keeps searches snappy
    }
  }

  // Out of budget or genuinely blocked: walk as far towards it as we got.
  return bestNode !== start ? rebuild(bestNode, start) : null;
}

function rebuild(node, start) {
  const out = [];
  let cur = node;
  while (cur !== start && cur >= 0) { out.push(cur); cur = cameFrom[cur]; }
  out.reverse();
  return out;
}
