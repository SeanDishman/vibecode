// One frame: blit the static board, then draw build hints, turrets, circles,
// projectiles and effects in world space.
import { CELL, COLS, W, H } from './config.js';
import { hexA, mix } from './colors.js';
import { S, canBuild, statRange, rangeOf, coneScale } from './state.js';
import { fx, parts } from './fx.js';
import { sprite, drawSprite } from './sprites.js';
import { drawTurretBody, roundRect } from './turret-art.js';
import { view } from './view.js';

export function renderFrame() {
  const ctx = view.ctx, d = view.dpr;
  ctx.setTransform(d, 0, 0, d, 0, 0);
  ctx.fillStyle = '#06070c';
  ctx.fillRect(0, 0, view.w, view.h);

  const sh = S.shake;
  const jx = sh ? (Math.random() - 0.5) * sh : 0;
  const jy = sh ? (Math.random() - 0.5) * sh : 0;

  ctx.drawImage(view.board, view.ox + jx, view.oy + jy, W * view.scale, H * view.scale);

  ctx.save();
  ctx.translate(view.ox + jx, view.oy + jy);
  ctx.scale(view.scale, view.scale);

  drawBuildHints(ctx);
  for (const t of S.towers) drawTurret(ctx, t);
  drawEnemies(ctx);
  drawShots(ctx);
  drawFx(ctx);
  drawParticles(ctx);

  ctx.restore();

  if (S.hurt > 0) {
    const g = ctx.createRadialGradient(
      view.w / 2, view.h / 2, Math.min(view.w, view.h) * 0.3,
      view.w / 2, view.h / 2, Math.max(view.w, view.h) * 0.62);
    g.addColorStop(0, 'rgba(255,84,112,0)');
    g.addColorStop(1, `rgba(255,84,112,${0.28 * S.hurt})`);
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, view.w, view.h);
  }
}

/** Red disc for mortar-style min-range — filled so it reads as "no fire here". */
function drawMinRangeDeadzone(ctx, x, y, minR, strong) {
  if (!minR || minR <= 0) return;
  ctx.save();
  ctx.fillStyle = strong ? 'rgba(255, 70, 90, 0.22)' : 'rgba(255, 70, 90, 0.10)';
  ctx.strokeStyle = strong ? 'rgba(255, 90, 110, 0.85)' : 'rgba(255, 90, 110, 0.45)';
  ctx.lineWidth = strong ? 2 : 1.3;
  ctx.beginPath();
  ctx.arc(x, y, minR, 0, Math.PI * 2);
  ctx.fill();
  ctx.setLineDash(strong ? [] : [4, 4]);
  ctx.stroke();
  ctx.setLineDash([]);
  // Inner cross so it never looks like a friendly aura
  if (strong) {
    ctx.strokeStyle = 'rgba(255, 120, 130, 0.55)';
    ctx.lineWidth = 1.2;
    const s = minR * 0.35;
    ctx.beginPath();
    ctx.moveTo(x - s, y - s); ctx.lineTo(x + s, y + s);
    ctx.moveTo(x + s, y - s); ctx.lineTo(x - s, y + s);
    ctx.stroke();
  }
  ctx.restore();
}

function drawBuildHints(ctx) {
  const sel = S.selectedType, i = S.hoverCell;

  if (sel && i >= 0) {
    const cx = (i % COLS) * CELL, cy = ((i / COLS) | 0) * CELL;
    const px = cx + CELL / 2, py = cy + CELL / 2;
    const ok = canBuild(i) && S.gold >= sel.cost;

    ctx.fillStyle = ok ? hexA(sel.color, 0.10) : 'rgba(255,84,112,.12)';
    ctx.strokeStyle = ok ? hexA(sel.color, 0.55) : 'rgba(255,84,112,.55)';
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.arc(px, py, rangeOf(sel, 1), 0, Math.PI * 2);
    ctx.fill(); ctx.stroke();

    // Mortar blind zone ONLY while placing — never after it's on the board.
    if (sel.minRange) drawMinRangeDeadzone(ctx, px, py, sel.minRange, true);

    ctx.fillStyle = ok ? hexA(sel.color, 0.22) : 'rgba(255,84,112,.2)';
    roundRect(ctx, cx + 3, cy + 3, CELL - 6, CELL - 6, 7);
    ctx.fill();

    if (ok) {
      ctx.globalAlpha = 0.72;
      drawTurretBody(ctx, { def: sel, level: 1, angle: -Math.PI / 2, heat: 0, pulse: S.time }, px, py);
      ctx.globalAlpha = 1;
    }
  }

  // Selected tower: range ring (current level) + cell outline so you can see coverage.
  if (S.selected) {
    const t = S.selected;
    const r = statRange(t);

    ctx.fillStyle = hexA(t.def.color, 0.10);
    ctx.strokeStyle = hexA(t.def.color, 0.55);
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.arc(t.x, t.y, r, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();

    // Mortar-style blind zone — same read as while placing.
    if (t.def.minRange) drawMinRangeDeadzone(ctx, t.x, t.y, t.def.minRange, true);

    ctx.strokeStyle = hexA(t.def.color, 0.9);
    ctx.lineWidth = 2;
    roundRect(ctx, t.x - CELL / 2 + 2, t.y - CELL / 2 + 2, CELL - 4, CELL - 4, 8);
    ctx.stroke();
  }
}

function drawTurret(ctx, t) {
  drawTurretBody(ctx, t, t.x, t.y);

  // No always-on range rings for aura/buff/nova/etc. — the board was a soup of
  // overlapping circles. Range is drawn in drawBuildHints for placement/selection.

  if ((t.def.kind === 'beam' || t.def.kind === 'oblivion') && t.target && !t.target.dead) {
    const g = t.target;
    const power = 1 + t.charge / t.def.ramp;
    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    ctx.strokeStyle = hexA(t.def.color, 0.20);
    ctx.lineWidth = 6 * power;
    ctx.beginPath(); ctx.moveTo(t.x, t.y); ctx.lineTo(g.x, g.y); ctx.stroke();
    ctx.strokeStyle = mix(t.def.color, '#ffffff', 0.6);
    ctx.lineWidth = 1.6 * power;
    ctx.beginPath(); ctx.moveTo(t.x, t.y); ctx.lineTo(g.x, g.y); ctx.stroke();
    ctx.restore();
  }

  // Flame cone while cooking something
  if (t.def.kind === 'flame' && t.target && !t.target.dead && t.heat > 0.2) {
    const r = statRange(t);
    const half = (t.def.cone || 0.55) * coneScale(t.level);
    ctx.save();
    ctx.globalCompositeOperation = 'lighter';
    ctx.fillStyle = hexA(t.def.color, 0.12 + t.heat * 0.1);
    ctx.beginPath();
    ctx.moveTo(t.x, t.y);
    ctx.arc(t.x, t.y, r, t.angle - half, t.angle + half);
    ctx.closePath();
    ctx.fill();
    ctx.restore();
  }
}

function drawEnemies(ctx) {
  for (const e of S.enemies) {
    let color = e.def.color;
    if (e.chill > 0.05) color = mix(color, '#7ec8ff', 0.5);
    else if (e.burnT > 0) color = mix(color, '#ff7a3d', 0.50);
    else if (e.poisonT > 0) color = mix(color, '#9ff05f', 0.42);

    drawSprite(ctx, sprite(color, e.r), e.x, e.y, e.immune > 0 ? 0.35 : 1);

    if (e.flash > 0) {
      ctx.save();
      ctx.globalCompositeOperation = 'lighter';
      drawSprite(ctx, sprite('#ffffff', e.r * 0.9, false), e.x, e.y, Math.min(1, e.flash * 8));
      ctx.restore();
    }

    if (e.shield > 0) {
      ctx.strokeStyle = hexA('#9fb6ff', 0.25 + 0.55 * (e.shield / e.shieldMax));
      ctx.lineWidth = 1.1;
      ctx.beginPath(); ctx.arc(e.x, e.y, e.r + 3.2, 0, Math.PI * 2); ctx.stroke();
    }

    if (e.immune > 0) {
      ctx.strokeStyle = hexA('#ffffff', 0.5);
      ctx.setLineDash([2, 3]);
      ctx.lineWidth = 1;
      ctx.beginPath(); ctx.arc(e.x, e.y, e.r + 4, 0, Math.PI * 2); ctx.stroke();
      ctx.setLineDash([]);
    }

    // only the big circles get a bar; hundreds of tiny ones would be noise
    if (e.r >= 6.4 && e.hp < e.max) {
      const w = e.r * 2.6, hpw = w * Math.max(0, e.hp / e.max);
      ctx.fillStyle = 'rgba(4,6,11,.75)';
      ctx.fillRect(e.x - w / 2, e.y - e.r - 6, w, 2.6);
      ctx.fillStyle = e.def.boss ? '#ff5470' : '#7defb0';
      ctx.fillRect(e.x - w / 2, e.y - e.r - 6, hpw, 2.6);
    }
  }
}

function drawShots(ctx) {
  ctx.save();
  ctx.globalCompositeOperation = 'lighter';

  for (const s of S.shots) {
    if (s.kind === 'mortar') {
      const k = s.t / s.dur;
      const lift = Math.sin(k * Math.PI) * 26;
      ctx.fillStyle = 'rgba(0,0,0,.28)';
      ctx.beginPath(); ctx.ellipse(s.x, s.y, 3.4, 1.8, 0, 0, Math.PI * 2); ctx.fill();
      drawSprite(ctx, sprite(s.color, 3.4), s.x, s.y - lift);
      ctx.strokeStyle = hexA(s.color, 0.35);
      ctx.lineWidth = 1;
      ctx.beginPath(); ctx.arc(s.tx, s.ty, 6 + (1 - k) * 10, 0, Math.PI * 2); ctx.stroke();
      continue;
    }

    const a = Math.atan2(s.vy, s.vx);
    const tail = s.splash ? 9 : 13;
    const grd = ctx.createLinearGradient(s.x, s.y, s.x - Math.cos(a) * tail, s.y - Math.sin(a) * tail);
    grd.addColorStop(0, hexA(mix(s.color, '#ffffff', 0.5), 0.95));
    grd.addColorStop(1, hexA(s.color, 0));
    ctx.strokeStyle = grd;
    ctx.lineWidth = s.r * 1.5;
    ctx.lineCap = 'round';
    ctx.beginPath();
    ctx.moveTo(s.x, s.y);
    ctx.lineTo(s.x - Math.cos(a) * tail, s.y - Math.sin(a) * tail);
    ctx.stroke();
  }

  ctx.restore();
}

function drawFx(ctx) {
  ctx.save();
  ctx.globalCompositeOperation = 'lighter';

  for (const f of fx) {
    const k = f.t / f.life;
    if (f.kind === 'ring') {
      ctx.strokeStyle = hexA(f.color, (1 - k) * 0.75);
      ctx.lineWidth = 2.4 * (1 - k) + 0.6;
      ctx.beginPath(); ctx.arc(f.x, f.y, f.r * (0.25 + 0.85 * k), 0, Math.PI * 2); ctx.stroke();
    } else if (f.kind === 'beam') {
      ctx.strokeStyle = hexA(f.color, (1 - k) * 0.85);
      ctx.lineWidth = f.width * (1 - k * 0.6);
      ctx.beginPath(); ctx.moveTo(f.x1, f.y1); ctx.lineTo(f.x2, f.y2); ctx.stroke();
      ctx.strokeStyle = hexA('#ffffff', (1 - k) * 0.5);
      ctx.lineWidth = f.width * 0.35;
      ctx.stroke();
    } else if (f.kind === 'flash') {
      const r = 7 * (1 - k);
      ctx.fillStyle = hexA(mix(f.color, '#ffffff', 0.55), (1 - k) * 0.85);
      ctx.beginPath();
      ctx.moveTo(f.x + Math.cos(f.a) * r * 1.9, f.y + Math.sin(f.a) * r * 1.9);
      ctx.lineTo(f.x + Math.cos(f.a + 2.4) * r, f.y + Math.sin(f.a + 2.4) * r);
      ctx.lineTo(f.x + Math.cos(f.a - 2.4) * r, f.y + Math.sin(f.a - 2.4) * r);
      ctx.closePath(); ctx.fill();
    }
  }

  ctx.restore();
}

function drawParticles(ctx) {
  ctx.save();
  ctx.globalCompositeOperation = 'lighter';
  for (const p of parts) {
    const k = 1 - p.t / p.life;
    ctx.fillStyle = hexA(p.color, k * 0.9);
    ctx.beginPath();
    ctx.arc(p.x, p.y, p.r * (0.4 + k * 0.9), 0, Math.PI * 2);
    ctx.fill();
  }
  ctx.restore();
}
