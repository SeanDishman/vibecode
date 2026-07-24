// render.js — the pixel map. Terrain, territory and fog are each baked into an
// offscreen canvas at exactly one pixel per tile and blitted up with smoothing
// off, which is what gives the world its crisp, chunky look at any zoom. Sprites
// are drawn on top in device pixels so they never land on a half-pixel.

import {
  W, H, TILES, ZOOMS, T, idx, inBounds, clamp, isWater, hash2, cheb,
  LOD_DETAIL, LOD_FINE,
} from './core.js';
import { world, game, camera, dirty, markDirty, cityAt } from './store.js';
import { TERRAIN, RESOURCES, BLD, UNI } from './data.js';
import { bakeBld, bakeUnit, bakeCity, bake, SPR_UNIT, shade } from './sprites.js';
import { seesOil } from './economy.js';

let cv, ctx, dpr = 1;
let cw = 0, ch = 0;                       // canvas size in device pixels

const terrainCv = document.createElement('canvas');
const terrCtx = terrainCv.getContext('2d');
const terrImg = terrCtx.createImageData(W, H);

const terrCv2 = document.createElement('canvas');    // territory tint
const terrCtx2 = terrCv2.getContext('2d');
const terrImg2 = terrCtx2.createImageData(W, H);

const fogCv = document.createElement('canvas');
const fogCtx = fogCv.getContext('2d');
const fogImg = fogCtx.createImageData(W, H);

let miniCv, miniCtx;
const miniImg = new ImageData(W, H);

/** Mutated by input.js so the renderer can draw the cursor's intent. */
export const hover = { x: -1, y: -1, ok: false, why: '' };
export const dragBox = { on: false, x0: 0, y0: 0, x1: 0, y1: 0 };

for (const c of [terrainCv, terrCv2, fogCv]) { c.width = W; c.height = H; }

export function initRender(canvas, minimap) {
  cv = canvas;
  ctx = cv.getContext('2d', { alpha: false });
  miniCv = minimap;
  miniCtx = miniCv.getContext('2d');
  resize();
}

export function resize() {
  if (!cv) return;
  dpr = Math.max(1, Math.min(2, window.devicePixelRatio || 1));
  const r = cv.getBoundingClientRect();
  cw = Math.max(2, Math.round(r.width * dpr));
  ch = Math.max(2, Math.round(r.height * dpr));
  if (cv.width !== cw || cv.height !== ch) { cv.width = cw; cv.height = ch; }
  ctx.imageSmoothingEnabled = false;
}

/* ── camera ───────────────────────────────────────────────────────────────── */

/** Screen pixels per tile — always an integer so tiles stay pixel-aligned. */
export const zpx = () => Math.max(1, Math.round(ZOOMS[camera.zi] * dpr));

export function view() {
  const z = zpx();
  const tilesW = cw / z, tilesH = ch / z;
  const x0 = camera.x - tilesW / 2, y0 = camera.y - tilesH / 2;
  return { z, x0, y0, tilesW, tilesH };
}

export function worldToScreen(wx, wy) {
  const v = view();
  return { x: (wx - v.x0) * v.z, y: (wy - v.y0) * v.z };
}

/** CSS pixel coordinates (what mouse events give us) to tile coordinates. */
export function screenToWorld(px, py) {
  const v = view();
  return { x: v.x0 + (px * dpr) / v.z, y: v.y0 + (py * dpr) / v.z };
}

export function clampCamera() {
  const v = view();
  camera.x = clamp(camera.x, v.tilesW / 2 - 2, W - v.tilesW / 2 + 2);
  camera.y = clamp(camera.y, v.tilesH / 2 - 2, H - v.tilesH / 2 + 2);
  if (v.tilesW > W) camera.x = W / 2;
  if (v.tilesH > H) camera.y = H / 2;
}

export function zoomAt(delta, cssX, cssY) {
  const before = screenToWorld(cssX, cssY);
  camera.zi = clamp(camera.zi + delta, 0, ZOOMS.length - 1);
  const after = screenToWorld(cssX, cssY);
  camera.x += before.x - after.x;
  camera.y += before.y - after.y;
  clampCamera();
}

/* ── offscreen layers ─────────────────────────────────────────────────────── */

const rgbCache = new Map();
function rgb(hex) {
  let v = rgbCache.get(hex);
  if (!v) {
    const n = parseInt(hex.slice(1), 16);
    v = [(n >> 16) & 255, (n >> 8) & 255, n & 255];
    rgbCache.set(hex, v);
  }
  return v;
}

const RIVER_RGB = [58, 122, 176];

function buildTerrain() {
  const d = terrImg.data;
  for (let i = 0; i < TILES; i++) {
    const t = world.terr[i];
    let [r, g, b] = rgb(TERRAIN[t].col);

    // A touch of per-tile jitter turns flat colour into pixel-art texture.
    const j = (hash2(i % W, (i / W) | 0, 7717) - 0.5) * 22;
    r = clamp(r + j, 0, 255); g = clamp(g + j, 0, 255); b = clamp(b + j, 0, 255);

    if (world.river[i] && !isWater(t)) { r = RIVER_RGB[0]; g = RIVER_RGB[1]; b = RIVER_RGB[2]; }

    const o = i * 4;
    d[o] = r; d[o + 1] = g; d[o + 2] = b; d[o + 3] = 255;
  }
  terrCtx.putImageData(terrImg, 0, 0);
  dirty.terrain = false;
}

function buildTerritory() {
  const d = terrImg2.data;
  for (let i = 0; i < TILES; i++) {
    const own = world.owner[i];
    const o = i * 4;
    if (own < 0) { d[o + 3] = 0; continue; }
    const e = game.empires[own];
    if (!e) { d[o + 3] = 0; continue; }
    const [r, g, b] = rgb(e.col);

    // Edge tiles get a much stronger tint, which reads as a border line.
    const x = i % W, y = (i / W) | 0;
    let edge = false;
    if (x === 0 || y === 0 || x === W - 1 || y === H - 1) edge = true;
    else if (world.owner[i - 1] !== own || world.owner[i + 1] !== own ||
             world.owner[i - W] !== own || world.owner[i + W] !== own) edge = true;

    d[o] = r; d[o + 1] = g; d[o + 2] = b;
    d[o + 3] = edge ? 168 : 46;
  }
  terrCtx2.putImageData(terrImg2, 0, 0);
  dirty.territory = false;
}

/* The whole point of the game is looking at a world, so unexplored ground is
   dimmed rather than blacked out — the coastlines stay readable while anything
   that matters (cities, buildings, enemy units) is still hidden until seen. */
function buildFog() {
  const d = fogImg.data;
  for (let i = 0; i < TILES; i++) {
    const o = i * 4;
    d[o] = 4; d[o + 1] = 6; d[o + 2] = 11;
    d[o + 3] = world.vis[i] ? 0 : world.explored[i] ? 58 : 148;
  }
  fogCtx.putImageData(fogImg, 0, 0);
  dirty.fog = false;
}

function buildMinimap() {
  const d = miniImg.data;
  for (let i = 0; i < TILES; i++) {
    const o = i * 4;
    let [r, g, b] = rgb(TERRAIN[world.terr[i]].col);
    if (world.explored[i]) {
      const own = world.owner[i];
      if (own >= 0 && game.empires[own]) {
        const [er, eg, eb] = rgb(game.empires[own].col);
        r = (r + er * 1.7) / 2.7; g = (g + eg * 1.7) / 2.7; b = (b + eb * 1.7) / 2.7;
      }
      if (!world.vis[i]) { r *= 0.68; g *= 0.68; b *= 0.68; }
    } else {
      // Never-seen ground: the shape of the land, but none of its politics.
      r *= 0.34; g *= 0.34; b *= 0.34;
    }
    d[o] = r; d[o + 1] = g; d[o + 2] = b; d[o + 3] = 255;
  }
  if (miniCtx) {
    miniCtx.putImageData(miniImg, 0, 0);
    // Cities pop as bright dots so the minimap is actually useful.
    for (const c of game.cities) {
      if (c.dead || !world.explored[c.ti]) continue;
      miniCtx.fillStyle = game.empires[c.owner].col;
      miniCtx.fillRect(c.x - 1, c.y - 1, 3, 3);
    }
  }
  dirty.minimap = false;
}

/* ── main draw ────────────────────────────────────────────────────────────── */

export function draw(dt) {
  if (!ctx) return;
  // Cheap, and it means nothing else has to remember to clamp: a capital near
  // the map edge, a window resize and a zoom all land inside the world.
  clampCamera();
  if (dirty.terrain) buildTerrain();
  if (dirty.territory) buildTerritory();
  if (dirty.fog) buildFog();

  const v = view();
  const z = v.z;

  ctx.imageSmoothingEnabled = false;
  ctx.fillStyle = '#05070c';
  ctx.fillRect(0, 0, cw, ch);

  // Blit the whole map scaled; the browser clips what falls outside.
  const dx = Math.round(-v.x0 * z), dy = Math.round(-v.y0 * z);
  ctx.drawImage(terrainCv, 0, 0, W, H, dx, dy, W * z, H * z);
  ctx.drawImage(terrCv2, 0, 0, W, H, dx, dy, W * z, H * z);

  const x0 = Math.max(0, Math.floor(v.x0) - 1), x1 = Math.min(W - 1, Math.ceil(v.x0 + v.tilesW) + 1);
  const y0 = Math.max(0, Math.floor(v.y0) - 1), y1 = Math.min(H - 1, Math.ceil(v.y0 + v.tilesH) + 1);

  if (z >= LOD_DETAIL) drawScenery(x0, y0, x1, y1, dx, dy, z);
  if (z >= 5) drawResources(x0, y0, x1, y1, dx, dy, z);
  drawWorkedTiles(dx, dy, z);
  drawBuildings(x0, y0, x1, y1, dx, dy, z);
  drawCities(dx, dy, z);
  if (z >= 5) drawVillagers(dx, dy, z);
  drawUnits(dx, dy, z);
  drawFx(dx, dy, z);

  ctx.drawImage(fogCv, 0, 0, W, H, dx, dy, W * z, H * z);

  drawPlacementGhost(dx, dy, z);
  drawSelection(dx, dy, z);
  drawDragBox();

  if (dirty.minimap) buildMinimap();
  drawMinimapViewport();
}

/* ── scenery ──────────────────────────────────────────────────────────────
   Terrain gains real furniture as you zoom in: trees in the woods, boulders on
   the hills, dunes in the desert, wave crests at sea. Every position comes from
   a hash of the tile coordinate, so the decoration is identical frame to frame
   (anything random here would shimmer horribly) and costs nothing to store. */

const SCENERY = {
  [T.FOREST]:   { kind: 'tree', n: 3, a: '#20492a', b: '#39734068' },
  [T.JUNGLE]:   { kind: 'tree', n: 4, a: '#17401f', b: '#2c6b3168' },
  [T.HILLS]:    { kind: 'rock', n: 2, a: '#4d5730', b: '#7d8a5a' },
  [T.MOUNTAIN]: { kind: 'rock', n: 3, a: '#585a61', b: '#8b8d95' },
  [T.PEAK]:     { kind: 'snowcap', n: 2, a: '#8e939c', b: '#e8eef5' },
  [T.GRASS]:    { kind: 'tuft', n: 3, a: '#3c7038', b: '#5d9b52' },
  [T.PLAINS]:   { kind: 'tuft', n: 2, a: '#63803c', b: '#8fae5e' },
  [T.SAVANNA]:  { kind: 'tuft', n: 2, a: '#797d3f', b: '#a8ac66' },
  [T.MARSH]:    { kind: 'tuft', n: 3, a: '#38513a', b: '#5b7a5c' },
  [T.DESERT]:   { kind: 'dune', n: 2, a: '#b89c5f', b: '#e0cb92' },
  [T.BEACH]:    { kind: 'dune', n: 2, a: '#b8a274', b: '#e3d1a5' },
  [T.TUNDRA]:   { kind: 'speck', n: 3, a: '#6d7c72', b: '#9aa89d' },
  [T.SNOW]:     { kind: 'speck', n: 3, a: '#b6c2cb', b: '#eef4f8' },
  [T.SHALLOW]:  { kind: 'wave', n: 2, a: '#3b83b4', b: '#5fa6d2' },
  [T.OCEAN]:    { kind: 'wave', n: 1, a: '#20496f', b: '#33628c' },
  [T.DEEP]:     { kind: 'wave', n: 1, a: '#173355', b: '#22406a' },
};

function drawScenery(x0, y0, x1, y1, dx, dy, z) {
  const fine = z >= LOD_FINE;
  const px = Math.max(1, Math.round(z / 14));      // one "detail pixel"
  for (let y = y0; y <= y1; y++) {
    for (let x = x0; x <= x1; x++) {
      const i = idx(x, y);
      if (!world.explored[i]) continue;
      if (world.bld[i] >= 0 || world.city[i] >= 0) continue;   // don't litter under buildings
      const s = SCENERY[world.terr[i]];
      if (!s) continue;

      const n = fine ? s.n : Math.max(1, s.n - 1);
      const ox = dx + x * z, oy = dy + y * z;
      for (let k = 0; k < n; k++) {
        const hx = hash2(x * 7 + k, y * 13, 991);
        const hy = hash2(x * 11, y * 5 + k, 313);
        const fx = ox + hx * (z - px * 3), fy = oy + hy * (z - px * 3);
        drawProp(s, fx, fy, px, fine);
      }
    }
  }
}

function drawProp(s, x, y, p, fine) {
  switch (s.kind) {
    case 'tree':
      ctx.fillStyle = s.a;
      ctx.fillRect(x + p, y + p * 2, p, p * 2);            // trunk
      ctx.fillStyle = s.b;
      ctx.fillRect(x, y, p * 3, p * 2);                    // canopy
      if (fine) ctx.fillRect(x + p * 0.5, y - p, p * 2, p);
      break;
    case 'rock':
      ctx.fillStyle = s.a;
      ctx.fillRect(x, y + p, p * 3, p * 2);
      ctx.fillStyle = s.b;
      ctx.fillRect(x + p, y, p * 2, p);
      break;
    case 'snowcap':
      ctx.fillStyle = s.a;
      ctx.fillRect(x, y + p, p * 3, p * 2);
      ctx.fillStyle = s.b;
      ctx.fillRect(x + p * 0.5, y, p * 2, p);
      break;
    case 'tuft':
      ctx.fillStyle = s.b;
      ctx.fillRect(x, y + p, p, p);
      ctx.fillRect(x + p * 2, y + p, p, p);
      if (fine) { ctx.fillStyle = s.a; ctx.fillRect(x + p, y, p, p * 2); }
      break;
    case 'dune':
      ctx.fillStyle = s.b;
      ctx.fillRect(x, y + p, p * 3, p);
      if (fine) { ctx.fillStyle = s.a; ctx.fillRect(x + p, y + p * 2, p * 3, p); }
      break;
    case 'speck':
      ctx.fillStyle = s.b;
      ctx.fillRect(x, y, p, p);
      if (fine) { ctx.fillStyle = s.a; ctx.fillRect(x + p * 2, y + p * 2, p, p); }
      break;
    case 'wave':
      ctx.fillStyle = s.b;
      ctx.fillRect(x, y, p * 3, p);
      if (fine) ctx.fillRect(x + p, y + p * 2, p * 2, p);
      break;
  }
}

function drawResources(x0, y0, x1, y1, dx, dy, z) {
  const s = Math.max(1, Math.round(z / 4));
  // Strategic resources stay off the map until the player can actually use them.
  const oilKnown = seesOil(game.empires[game.player]);
  for (let y = y0; y <= y1; y++) {
    for (let x = x0; x <= x1; x++) {
      const i = idx(x, y);
      const r = world.res[i];
      if (!r || !world.explored[i]) continue;
      if (RESOURCES[r].strategic && !oilKnown) continue;
      if (world.bld[i] >= 0 || world.city[i] >= 0) continue;
      ctx.fillStyle = RESOURCES[r].col;
      const px = dx + x * z + ((z - s * 2) >> 1);
      const py = dy + y * z + ((z - s * 2) >> 1);
      ctx.fillRect(px, py, s, s);
      ctx.fillRect(px + s, py + s, s, s);
    }
  }
}

/** Faint markers on the tiles the selected city is actually working. */
function drawWorkedTiles(dx, dy, z) {
  if (game.sel.kind !== 'city') return;
  const c = game.cities[game.sel.idx];
  if (!c || c.dead || z < 4) return;
  ctx.fillStyle = 'rgba(255,255,255,.22)';
  const s = Math.max(1, Math.round(z / 6));
  for (const ti of c.worked) {
    const x = ti % W, y = (ti / W) | 0;
    ctx.fillRect(dx + x * z + ((z - s) >> 1), dy + y * z + ((z - s) >> 1), s, s);
  }
}

/* Sprite scales. Art is 16px (buildings), 24px (cities), 12–14px (foot units).
   `z` is device px/tile (already includes dpr). Buildings bake 1:1 art→screen;
   units bake crisp then blit to a *fraction of the tile* so they can be smaller
   than one art pixel per screen pixel (the old z/20 path could never go under
   14 device-px tall, which is almost a whole tile at mid zoom — house-sized jits). */
const bldScale = z => Math.max(1, Math.round(z / 12));
const cityScale = z => Math.max(1, Math.round(z / 9));
// On-screen height as a fraction of one tile. Soldiers ~quarter tile; villagers tinier.
const UNIT_TILE_H = 0.30;
const VILLAGER_TILE_H = 0.16;
// Bake resolution only — final blit size is driven by UNIT_TILE_H, not this.
const unitBakeScale = z => Math.max(1, Math.min(3, Math.round(z / 28)));
const villagerBakeScale = z => Math.max(1, Math.min(2, Math.round(z / 36)));

/* Below this zoom, draw compact role markers instead of sprites. */
const UNIT_SPRITE_MIN_ZOOM = 15;

function drawBuildings(x0, y0, x1, y1, dx, dy, z) {
  const scale = bldScale(z);
  for (let y = y0; y <= y1; y++) {
    for (let x = x0; x <= x1; x++) {
      const i = idx(x, y);
      const bi = world.bld[i];
      if (bi < 0 || !world.explored[i]) continue;
      const b = game.buildings[bi];
      if (!b || b.dead) continue;
      const emp = game.empires[b.owner];
      const img = bakeBld(b.id, emp.col, scale);
      // Sit the sprite on the bottom edge of its tile so it reads as standing there.
      const px = dx + x * z + ((z - img.width) >> 1);
      const py = dy + y * z + z - img.height + Math.max(1, (z >> 2));

      const prog = b.progress ?? 1;
      if (prog >= 0.999 && b.phase !== 'strip') {
        ctx.drawImage(img, px, py);
        continue;
      }

      // Progressive reveal: foundations first, walls grow upward as workers
      // assemble (or peel downward while stripping). Scaffolding fills the rest.
      const shown = Math.max(0.08, Math.min(1, prog));
      const h = Math.max(1, Math.round(img.height * shown));
      const srcY = img.height - h;
      // Faint scaffold / rubble under the rising structure.
      ctx.save();
      ctx.globalAlpha = 0.35 + shown * 0.25;
      ctx.fillStyle = b.phase === 'strip' ? '#6b7280' : '#8a7348';
      const sw = Math.max(2, img.width - 2);
      ctx.fillRect(px + 1, py + img.height - Math.max(2, Math.round(img.height * 0.22)),
        sw, Math.max(2, Math.round(img.height * 0.18)));
      // Scaffold posts for early builds.
      if (shown < 0.85) {
        ctx.fillStyle = '#5c4a32';
        const postH = Math.round(img.height * (1 - shown) * 0.7);
        ctx.fillRect(px + 1, py + srcY - 1, Math.max(1, scale), postH + 2);
        ctx.fillRect(px + img.width - 2, py + srcY - 1, Math.max(1, scale), postH + 2);
      }
      ctx.restore();
      ctx.drawImage(img, 0, srcY, img.width, h, px, py + srcY, img.width, h);

      // Tiny progress pip so half-built walls read as "under construction".
      if (z >= 5 && shown < 1) {
        const bw = img.width;
        ctx.fillStyle = 'rgba(0,0,0,.5)';
        ctx.fillRect(px, py + img.height + 1, bw, 2 * dpr);
        ctx.fillStyle = b.phase === 'strip' ? '#ff9d5c' : '#7ee787';
        ctx.fillRect(px, py + img.height + 1, bw * shown, 2 * dpr);
      }
    }
  }
}

function drawCities(dx, dy, z) {
  const scale = cityScale(z);
  for (const c of game.cities) {
    if (c.dead || !world.explored[c.ti]) continue;
    const emp = game.empires[c.owner];
    const img = bakeCity(c.stage | 0, emp.col, scale);
    const px = dx + c.x * z + ((z - img.width) >> 1);
    const py = dy + c.y * z + z - img.height + Math.max(1, (z >> 2));

    if (c.flash > 0) {
      ctx.save();
      ctx.globalAlpha = c.flash * 0.6;
      ctx.fillStyle = '#ff6da9';
      ctx.fillRect(px - 2, py - 2, img.width + 4, img.height + 4);
      ctx.restore();
    }
    ctx.drawImage(img, px, py);

    if (z >= 4) {
      // Name plate and a health bar only when the city is hurt.
      if (c.hp < c.maxHp - 0.5) {
        const bw = Math.max(12, img.width);
        ctx.fillStyle = 'rgba(0,0,0,.55)';
        ctx.fillRect(px, py - 4 * dpr, bw, 3 * dpr);
        ctx.fillStyle = c.hp > c.maxHp * 0.45 ? '#ffd166' : '#ff6da9';
        ctx.fillRect(px, py - 4 * dpr, bw * clamp(c.hp / c.maxHp, 0, 1), 3 * dpr);
      }
      if (z >= 6) {
        const label = `${c.name} ${c.pop}`;
        ctx.font = `${Math.round(8 * dpr)}px "Cascadia Code", Consolas, monospace`;
        ctx.textAlign = 'center';
        const tx = px + img.width / 2, tyy = py + img.height + 9 * dpr;
        ctx.fillStyle = 'rgba(4,6,11,.78)';
        const tw = ctx.measureText(label).width;
        ctx.fillRect(tx - tw / 2 - 2 * dpr, tyy - 8 * dpr, tw + 4 * dpr, 10 * dpr);
        ctx.fillStyle = c.owner === game.player ? '#dfe6f2' : emp.col;
        ctx.fillText(label, tx, tyy);
        ctx.textAlign = 'left';
      }
    }
  }
}

function drawVillagers(dx, dy, z) {
  if (z < UNIT_SPRITE_MIN_ZOOM) return;
  const bakeSc = villagerBakeScale(z);
  const th = Math.max(3, Math.round(z * VILLAGER_TILE_H));
  for (const v of game.villagers) {
    if (v.dead) continue;
    const tx = Math.floor(v.x), ty = Math.floor(v.y);
    if (!inBounds(tx, ty) || !world.vis[idx(tx, ty)]) continue;
    const img = bake('u_villager', SPR_UNIT.villager, game.empires[v.owner].col, bakeSc);
    const tw = Math.max(2, Math.round(img.width * (th / img.height)));
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(img,
      dx + Math.round(v.x * z) - (tw >> 1),
      dy + Math.round(v.y * z) - th,
      tw, th);
  }
}

/** Zoomed-out stand-in: a chunky token whose shape says what kind of unit it is. */
function drawUnitMarker(u, cx, cy, z, col) {
  const r = Math.max(2, z * 0.30);
  const role = u.def.air ? 'air' : u.def.sea || u.embarked ? 'sea'
    : u.def.role === 'siege' ? 'siege' : u.def.cav ? 'cav'
    : u.def.role === 'settler' ? 'settler' : 'foot';

  ctx.beginPath();
  if (role === 'air') {                       // triangle, nose up
    ctx.moveTo(cx, cy - r * 1.2); ctx.lineTo(cx + r, cy + r * 0.8); ctx.lineTo(cx - r, cy + r * 0.8);
    ctx.closePath();
  } else if (role === 'sea') {                // hull
    ctx.moveTo(cx - r * 1.3, cy - r * 0.4); ctx.lineTo(cx + r * 1.3, cy - r * 0.4);
    ctx.lineTo(cx + r * 0.7, cy + r * 0.7); ctx.lineTo(cx - r * 0.7, cy + r * 0.7);
    ctx.closePath();
  } else if (role === 'siege') {              // square
    ctx.rect(cx - r, cy - r, r * 2, r * 2);
  } else if (role === 'cav') {                // diamond
    ctx.moveTo(cx, cy - r * 1.1); ctx.lineTo(cx + r * 1.1, cy);
    ctx.lineTo(cx, cy + r * 1.1); ctx.lineTo(cx - r * 1.1, cy);
    ctx.closePath();
  } else if (role === 'settler') {            // rounded, softer than a fighter
    ctx.arc(cx, cy, r * 0.9, 0, 6.283);
  } else {                                    // foot: circle
    ctx.arc(cx, cy, r, 0, 6.283);
  }
  ctx.fillStyle = col;
  ctx.fill();
  ctx.lineWidth = Math.max(1, z * 0.06);
  ctx.strokeStyle = 'rgba(10,12,18,.85)';
  ctx.stroke();
}

function drawUnits(dx, dy, z) {
  const useSprites = z >= UNIT_SPRITE_MIN_ZOOM;
  const bakeSc = unitBakeScale(z);
  // Hard cap: soldiers are ~30% of a tile tall no matter the zoom/dpr.
  const th = Math.max(4, Math.round(z * UNIT_TILE_H));
  const t = game.time;
  for (const u of game.units) {
    if (u.dead) continue;
    const tx = Math.floor(u.x), ty = Math.floor(u.y);
    if (!inBounds(tx, ty) || !world.vis[idx(tx, ty)]) continue;

    const emp = game.empires[u.owner];

    if (!useSprites) {
      const cx = dx + u.x * z, cy = dy + u.y * z;
      if (u.def.air) {
        ctx.save(); ctx.globalAlpha = 0.3; ctx.fillStyle = '#000';
        ctx.beginPath(); ctx.ellipse(cx, cy, z * 0.3, z * 0.14, 0, 0, 6.283); ctx.fill(); ctx.restore();
      }
      drawUnitMarker(u, cx, cy - (u.def.air ? z * 0.55 : 0), z, u.flash > 0 ? '#fff' : emp.col);
      if (u.hp < u.maxHp - 0.5 && z >= 5) {
        const bw = z * 0.8;
        ctx.fillStyle = 'rgba(0,0,0,.6)';
        ctx.fillRect(cx - bw / 2, cy - z * 0.62, bw, 2 * dpr);
        ctx.fillStyle = u.hp > u.maxHp * 0.4 ? '#7ee787' : '#ff6da9';
        ctx.fillRect(cx - bw / 2, cy - z * 0.62, bw * clamp(u.hp / u.maxHp, 0, 1), 2 * dpr);
      }
      continue;
    }

    const spriteId = u.embarked ? 'boat' : u.id;
    const img = bakeUnit(spriteId, emp.col, bakeSc);
    const tw = Math.max(3, Math.round(img.width * (th / img.height)));
    // A gentle bob makes a static army look alive without animating sprites.
    const bob = u.state === 'move' ? Math.round(Math.sin(t * 7 + u.bob) * Math.max(1, th * 0.06)) : 0;
    const px = dx + Math.round(u.x * z) - (tw >> 1);
    // Aircraft ride well above the ground and drop a shadow, so it is obvious
    // at a glance that they are not standing on the tile they are over.
    const lift = u.def.air ? Math.round(z * 0.9) : 0;
    const py = dy + Math.round(u.y * z) - th + bob - lift;

    if (lift) {
      ctx.save();
      ctx.globalAlpha = 0.30;
      ctx.fillStyle = '#000';
      ctx.beginPath();
      ctx.ellipse(dx + u.x * z, dy + u.y * z, z * 0.34, z * 0.16, 0, 0, 6.283);
      ctx.fill();
      ctx.restore();
    }

    if (u.flash > 0) {
      ctx.save();
      ctx.globalAlpha = u.flash * 0.8;
      ctx.fillStyle = '#fff';
      ctx.fillRect(px - 1, py - 1, tw + 2, th + 2);
      ctx.restore();
    }
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(img, px, py, tw, th);

    if (u.hp < u.maxHp - 0.5 && z >= 4) {
      const bw = tw;
      ctx.fillStyle = 'rgba(0,0,0,.6)';
      ctx.fillRect(px, py - 3 * dpr, bw, 2 * dpr);
      ctx.fillStyle = u.hp > u.maxHp * 0.4 ? '#7ee787' : '#ff6da9';
      ctx.fillRect(px, py - 3 * dpr, bw * clamp(u.hp / u.maxHp, 0, 1), 2 * dpr);
    }
  }
}

function drawFx(dx, dy, z) {
  for (const f of game.fx) {
    const k = f.t / f.life;
    const px = dx + f.x * z, py = dy + f.y * z;
    ctx.save();
    if (f.kind === 'spark') {
      ctx.globalAlpha = 1 - k;
      ctx.fillStyle = f.col;
      const s = Math.max(1, Math.round(z / 3));
      ctx.fillRect(px - s / 2, py - s / 2 - k * z, s, s);
    } else if (f.kind === 'puff') {
      ctx.globalAlpha = (1 - k) * 0.8;
      ctx.strokeStyle = f.col;
      ctx.lineWidth = Math.max(1, dpr);
      ctx.beginPath();
      ctx.arc(px, py, (0.3 + k * 1.1) * z, 0, 6.283);
      ctx.stroke();
    } else if (f.kind === 'ring') {
      ctx.globalAlpha = (1 - k) * 0.9;
      ctx.strokeStyle = f.col;
      ctx.lineWidth = Math.max(1, 2 * dpr);
      ctx.beginPath();
      ctx.arc(px, py, (0.5 + k * 3.5) * z, 0, 6.283);
      ctx.stroke();
    } else if (f.kind === 'text') {
      ctx.globalAlpha = 1 - k * k;
      ctx.fillStyle = f.col;
      ctx.font = `700 ${Math.round(9 * dpr)}px "Cascadia Code", Consolas, monospace`;
      ctx.textAlign = 'center';
      ctx.fillText(f.text, px, py - k * 14 * dpr);
      ctx.textAlign = 'left';
    }
    ctx.restore();
  }
}

function drawPlacementGhost(dx, dy, z) {
  if (!game.place || hover.x < 0) return;
  const scale = bldScale(z);
  const img = bakeBld(game.place, game.empires[game.player].col, scale);
  ctx.save();
  ctx.globalAlpha = 0.75;
  ctx.drawImage(img,
    dx + hover.x * z + ((z - img.width) >> 1),
    dy + hover.y * z + z - img.height + Math.max(1, (z >> 2)));
  ctx.restore();

  ctx.strokeStyle = hover.ok ? 'rgba(126,231,135,.95)' : 'rgba(255,109,169,.95)';
  ctx.lineWidth = Math.max(1, dpr);
  ctx.strokeRect(dx + hover.x * z + 0.5, dy + hover.y * z + 0.5, z - 1, z - 1);
}

function drawSelection(dx, dy, z) {
  const sel = game.sel;
  ctx.lineWidth = Math.max(1, dpr);

  for (const ui of sel.units) {
    const u = game.units[ui];
    if (!u || u.dead) continue;
    ctx.strokeStyle = 'rgba(255,255,255,.85)';
    ctx.beginPath();
    // Tight ring under the (now tiny) sprite — the old 0.62× tile oval made a
    // half-tile soldier look like it filled the whole cell.
    ctx.ellipse(dx + u.x * z, dy + u.y * z, z * 0.38, z * 0.22, 0, 0, 6.283);
    ctx.stroke();

    // Show where this unit has been told to go.
    if (u.path && u.pathI < u.path.length) {
      ctx.strokeStyle = 'rgba(69,230,210,.45)';
      ctx.beginPath();
      ctx.moveTo(dx + u.x * z, dy + u.y * z);
      for (let i = u.pathI; i < u.path.length; i += Math.max(1, Math.floor(u.path.length / 40))) {
        const ti = u.path[i];
        ctx.lineTo(dx + ((ti % W) + 0.5) * z, dy + (((ti / W) | 0) + 0.5) * z);
      }
      ctx.stroke();
    }
  }

  if (sel.kind === 'city') {
    const c = game.cities[sel.idx];
    if (c && !c.dead) {
      const r = Math.round(c.radius);
      ctx.strokeStyle = 'rgba(255,255,255,.5)';
      ctx.setLineDash([4 * dpr, 4 * dpr]);
      ctx.beginPath();
      ctx.arc(dx + (c.x + 0.5) * z, dy + (c.y + 0.5) * z, (r + 0.5) * z, 0, 6.283);
      ctx.stroke();
      ctx.setLineDash([]);
    }
  }
}

function drawDragBox() {
  if (!dragBox.on) return;
  const x = Math.min(dragBox.x0, dragBox.x1) * dpr, y = Math.min(dragBox.y0, dragBox.y1) * dpr;
  const w = Math.abs(dragBox.x1 - dragBox.x0) * dpr, h = Math.abs(dragBox.y1 - dragBox.y0) * dpr;
  ctx.fillStyle = 'rgba(69,230,210,.10)';
  ctx.fillRect(x, y, w, h);
  ctx.strokeStyle = 'rgba(69,230,210,.8)';
  ctx.lineWidth = Math.max(1, dpr);
  ctx.strokeRect(x + 0.5, y + 0.5, w, h);
}

function drawMinimapViewport() {
  if (!miniCtx) return;
  const v = view();
  miniCtx.strokeStyle = 'rgba(255,255,255,.85)';
  miniCtx.lineWidth = 1;
  miniCtx.strokeRect(
    Math.round(v.x0) + 0.5, Math.round(v.y0) + 0.5,
    Math.max(2, Math.round(v.tilesW)), Math.max(2, Math.round(v.tilesH)));
}

/** Force a minimap repaint — the turn loop calls this a few times a second. */
export const refreshMinimap = () => markDirty('minimap');
