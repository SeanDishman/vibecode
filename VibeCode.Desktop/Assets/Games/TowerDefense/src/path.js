// The road, baked once from ROUTE into world-space segments with cumulative
// lengths — an enemy is then just a distance travelled along it.
import { CELL, COLS, ROWS, ROUTE } from './config.js';

export const PATH = ROUTE.map(([cx, cy]) => ({ x: cx * CELL + CELL / 2, y: cy * CELL + CELL / 2 }));
export const SEGS = [];

let total = 0;
for (let i = 0; i < PATH.length - 1; i++) {
  const a = PATH[i], b = PATH[i + 1];
  const dx = b.x - a.x, dy = b.y - a.y;
  const len = Math.hypot(dx, dy);
  SEGS.push({ ax: a.x, ay: a.y, ux: dx / len, uy: dy / len, len, start: total });
  total += len;
}
export const PATH_LEN = total;

/** World position at distance `d` along the road, offset `lat` sideways. */
export function pathAt(d, lat, out) {
  let i = 0;
  while (i < SEGS.length - 1 && d > SEGS[i].start + SEGS[i].len) i++;
  const s = SEGS[i];
  const t = Math.max(0, d - s.start);
  out.x = s.ax + s.ux * t - s.uy * lat;
  out.y = s.ay + s.uy * t + s.ux * lat;
  out.ux = s.ux; out.uy = s.uy;
  return out;
}

// Cells the road runs through can't be built on: a cell is blocked when its
// centre sits closer to the centreline than roughly one cell.
export const BLOCKED = new Uint8Array(COLS * ROWS);
for (let cy = 0; cy < ROWS; cy++) {
  for (let cx = 0; cx < COLS; cx++) {
    const px = cx * CELL + CELL / 2, py = cy * CELL + CELL / 2;
    let near = Infinity;
    for (const s of SEGS) {
      const t = Math.max(0, Math.min(s.len, (px - s.ax) * s.ux + (py - s.ay) * s.uy));
      near = Math.min(near, Math.hypot(px - (s.ax + s.ux * t), py - (s.ay + s.uy * t)));
    }
    if (near < CELL * 0.78) BLOCKED[cy * COLS + cx] = 1;
  }
}

export function cellIndex(x, y) {
  const cx = Math.floor(x / CELL), cy = Math.floor(y / CELL);
  if (cx < 0 || cy < 0 || cx >= COLS || cy >= ROWS) return -1;
  return cy * COLS + cx;
}
