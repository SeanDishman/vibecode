// The viewport: letterboxes the fixed-size board into whatever space the HUD
// leaves, and owns the static board backdrop (grid + road), which is painted
// once per resize into an offscreen canvas and blitted every frame.
import { CELL, COLS, ROWS, W, H } from './config.js';
import { PATH, BLOCKED } from './path.js';
import { rescaleSprites } from './sprites.js';

export const view = {
  canvas: null, ctx: null, board: null, bctx: null,
  scale: 1, ox: 0, oy: 0, dpr: 1, w: 0, h: 0,
};

export function initView(canvas) {
  view.canvas = canvas;
  view.ctx = canvas.getContext('2d', { alpha: false });
  view.board = document.createElement('canvas');
  view.bctx = view.board.getContext('2d');
}

export function layout(stage, shopEl) {
  const cw = Math.max(320, stage.clientWidth || 960);
  const ch = Math.max(240, stage.clientHeight || 600);
  const dpr = Math.min(2, window.devicePixelRatio || 1);

  view.dpr = dpr; view.w = cw; view.h = ch;
  view.canvas.width = Math.round(cw * dpr);
  view.canvas.height = Math.round(ch * dpr);
  view.canvas.style.width = cw + 'px';
  view.canvas.style.height = ch + 'px';

  const top = 54, bottom = (shopEl?.offsetHeight || 80) + 6;
  const availW = cw - 16, availH = Math.max(120, ch - top - bottom);
  view.scale = Math.min(availW / W, availH / H);
  view.ox = (cw - W * view.scale) / 2;
  view.oy = top + (availH - H * view.scale) / 2;

  rescaleSprites(view.scale * view.dpr * 1.15);
  paintBoard();
}

export function toWorld(px, py) {
  return { x: (px - view.ox) / view.scale, y: (py - view.oy) / view.scale };
}

/** Grid, buildable plates, and the road — everything that never moves. */
export function paintBoard() {
  const s = view.scale * view.dpr;
  view.board.width = Math.max(1, Math.round(W * s));
  view.board.height = Math.max(1, Math.round(H * s));

  const g = view.bctx;
  g.setTransform(s, 0, 0, s, 0, 0);
  g.clearRect(0, 0, W, H);

  const bg = g.createLinearGradient(0, 0, W, H);
  bg.addColorStop(0, '#0b0f1a');
  bg.addColorStop(0.5, '#090c15');
  bg.addColorStop(1, '#070911');
  g.fillStyle = bg;
  g.fillRect(0, 0, W, H);

  g.strokeStyle = 'rgba(120,150,200,.055)';
  g.lineWidth = 1;
  g.beginPath();
  for (let x = 0; x <= COLS; x++) { g.moveTo(x * CELL, 0); g.lineTo(x * CELL, H); }
  for (let y = 0; y <= ROWS; y++) { g.moveTo(0, y * CELL); g.lineTo(W, y * CELL); }
  g.stroke();

  // faint plates on buildable cells, so the road reads as a cut-out
  g.fillStyle = 'rgba(140,180,240,.028)';
  for (let cy = 0; cy < ROWS; cy++) {
    for (let cx = 0; cx < COLS; cx++) {
      if (BLOCKED[cy * COLS + cx]) continue;
      g.fillRect(cx * CELL + 3, cy * CELL + 3, CELL - 6, CELL - 6);
    }
  }

  // the road: glow, dark bed, lit rim, dashed centre line
  g.lineCap = 'round';
  g.lineJoin = 'round';
  const road = new Path2D();
  road.moveTo(PATH[0].x, PATH[0].y);
  for (let i = 1; i < PATH.length; i++) road.lineTo(PATH[i].x, PATH[i].y);

  g.strokeStyle = 'rgba(69,230,210,.10)'; g.lineWidth = 42; g.stroke(road);
  g.strokeStyle = '#0d1524'; g.lineWidth = 34; g.stroke(road);
  g.strokeStyle = 'rgba(120,220,255,.26)'; g.lineWidth = 33; g.stroke(road);
  g.strokeStyle = '#161f33'; g.lineWidth = 29; g.stroke(road);   // bed reads lighter than the ground
  g.strokeStyle = 'rgba(90,150,210,.16)'; g.lineWidth = 22; g.stroke(road);
  g.strokeStyle = 'rgba(160,220,255,.16)'; g.lineWidth = 1.5;
  g.setLineDash([9, 11]); g.stroke(road); g.setLineDash([]);

  // The road runs on from off-board at both ends, so the spawn and base markers
  // are pinned to the board edge instead of to the path's own endpoints.
  const inP = PATH[0], outP = PATH[PATH.length - 1];
  // The whole board gets scaled down to fit the window, so board text is authored
  // large enough to survive that.
  g.font = '700 19px Inter, "Segoe UI", sans-serif';
  g.textBaseline = 'middle';

  const gateIn = g.createLinearGradient(0, 0, 90, 0);
  gateIn.addColorStop(0, 'rgba(69,230,210,.34)');
  gateIn.addColorStop(1, 'rgba(69,230,210,0)');
  g.fillStyle = gateIn;
  g.fillRect(0, inP.y - 17, 90, 34);
  g.textAlign = 'left';
  g.fillStyle = 'rgba(150,230,220,.75)';
  g.fillText('SPAWN', 40, inP.y - 44);      // clear of the board edge and the road's 42-wide glow

  const gateOut = g.createLinearGradient(W, 0, W - 110, 0);
  gateOut.addColorStop(0, 'rgba(255,84,112,.42)');
  gateOut.addColorStop(1, 'rgba(255,84,112,0)');
  g.fillStyle = gateOut;
  g.fillRect(W - 110, outP.y - 17, 110, 34);
  g.textAlign = 'right';
  g.fillStyle = 'rgba(255,150,170,.85)';
  g.fillText('YOUR BASE', W - 12, outP.y - 44);
}
