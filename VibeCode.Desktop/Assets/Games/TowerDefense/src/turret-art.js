// How a turret looks. Shared by the board renderer and the shop icons.
// Level is not a row of dots — each rank adds real hardware: longer barrels,
// extra pods, armour plates, glow cores, so L5 reads as a different machine.
import { hexA, mix } from './colors.js';

export function roundRect(ctx, x, y, w, h, r) {
  const rr = Math.max(0.5, Math.min(r, w / 2, h / 2));
  ctx.beginPath();
  ctx.moveTo(x + rr, y);
  ctx.arcTo(x + w, y, x + w, y + h, rr);
  ctx.arcTo(x + w, y + h, x, y + h, rr);
  ctx.arcTo(x, y + h, x, y, rr);
  ctx.arcTo(x, y, x + w, y, rr);
  ctx.closePath();
}

/**
 * @param {object} t {def, level, angle, heat, pulse, spin}
 */
export function drawTurretBody(ctx, t, x, y) {
  const def = t.def, c = def.color;
  const heat = Math.max(0, t.heat || 0);
  const lvl = Math.max(1, Math.min(5, t.level || 1));
  const pulse = t.pulse || 0;

  ctx.save();
  ctx.translate(x, y);

  drawBase(ctx, c, heat, lvl);

  // Rank badge on the plate (not the only upgrade signal — hardware grows too)
  if (lvl >= 2) drawRankChevrons(ctx, c, lvl);

  ctx.rotate(t.angle || 0);
  const barrel = mix(c, '#ffffff', 0.12 + heat * 0.55);
  const dark = mix(c, '#05070c', 0.4);
  const bright = mix(c, '#ffffff', 0.55);

  switch (def.kind) {
    case 'bullet':
      if (def.id === 'venom') drawVenom(ctx, barrel, dark, bright, c, heat, lvl);
      else drawPulse(ctx, barrel, dark, bright, c, heat, lvl);
      break;
    case 'shotgun': drawFlak(ctx, barrel, dark, bright, c, heat, lvl); break;
    case 'shell': drawCannon(ctx, barrel, dark, bright, c, heat, lvl); break;
    case 'missile': drawMissile(ctx, barrel, dark, bright, c, heat, lvl); break;
    case 'rail': drawRail(ctx, barrel, dark, bright, c, heat, lvl); break;
    case 'beam': drawLaser(ctx, barrel, dark, bright, c, heat, lvl); break;
    case 'mortar': drawMortar(ctx, barrel, dark, bright, c, heat, lvl); break;
    case 'chain': drawTesla(ctx, t, c, heat, pulse, lvl); break;
    case 'aura': drawCryo(ctx, t, c, heat, pulse, lvl); break;
    case 'buff': drawAmp(ctx, t, c, heat, pulse, lvl); break;
    case 'flame': drawFlame(ctx, barrel, dark, bright, c, heat, pulse, lvl); break;
    case 'sniper': drawSniper(ctx, barrel, dark, bright, c, heat, lvl); break;
    case 'nova': drawNova(ctx, t, c, heat, pulse, lvl); break;
    case 'gatling': drawGatling(ctx, barrel, dark, bright, c, heat, pulse, lvl, t); break;
    case 'singularity': drawSingularity(ctx, t, c, heat, pulse, lvl); break;
    case 'oblivion': drawOblivion(ctx, barrel, dark, bright, c, heat, lvl); break;
    case 'tempest': drawTempest(ctx, t, c, heat, pulse, lvl); break;
    default:
      ctx.fillStyle = barrel;
      ctx.fillRect(-2, -3, 14 + lvl * 2, 6);
  }

  // Max-rank crown glow sits on top of everything
  if (lvl >= 5) {
    ctx.rotate(-(t.angle || 0));
    ctx.strokeStyle = hexA(c, 0.55 + heat * 0.3);
    ctx.lineWidth = 1.6;
    ctx.beginPath(); ctx.arc(0, 0, 17.5, 0, Math.PI * 2); ctx.stroke();
    ctx.fillStyle = hexA(c, 0.12);
    ctx.beginPath(); ctx.arc(0, 0, 17.5, 0, Math.PI * 2); ctx.fill();
  }

  ctx.restore();
}

/* ── shared base ─────────────────────────────────────────────────────────── */

function drawBase(ctx, c, heat, lvl) {
  // Plate grows and gains a second rim as ranks climb
  const pad = 13.5 + (lvl - 1) * 1.15;
  const plate = ctx.createLinearGradient(-pad, -pad, pad, pad);
  plate.addColorStop(0, mix('#2a3348', c, 0.04 * (lvl - 1)));
  plate.addColorStop(0.45, '#171e2c');
  plate.addColorStop(1, '#0c1018');
  ctx.fillStyle = plate;
  roundRect(ctx, -pad, -pad, pad * 2, pad * 2, 7 + lvl * 0.5);
  ctx.fill();

  // Primary rim
  ctx.strokeStyle = hexA(c, 0.35 + heat * 0.4 + lvl * 0.04);
  ctx.lineWidth = 1.4 + (lvl >= 4 ? 0.4 : 0);
  roundRect(ctx, -pad + 0.8, -pad + 0.8, pad * 2 - 1.6, pad * 2 - 1.6, 6.5);
  ctx.stroke();

  // L3+: inner armour ring
  if (lvl >= 3) {
    ctx.strokeStyle = hexA(c, 0.22);
    ctx.lineWidth = 1;
    roundRect(ctx, -pad + 3.2, -pad + 3.2, pad * 2 - 6.4, pad * 2 - 6.4, 5);
    ctx.stroke();
  }

  // Corner armour blocks at high rank
  if (lvl >= 4) {
    ctx.fillStyle = mix('#1a2233', c, 0.25);
    const s = 4.2;
    const o = pad - 1.5;
    for (const [ox, oy] of [[-o, -o], [o - s, -o], [-o, o - s], [o - s, o - s]]) {
      roundRect(ctx, ox, oy, s, s, 1.2);
      ctx.fill();
    }
  }

  // Rivets
  ctx.fillStyle = mix('#4a556c', c, 0.2);
  const rv = pad - 3.4;
  for (const [rx, ry] of [[-rv, -rv], [rv, -rv], [-rv, rv], [rv, rv]]) {
    ctx.beginPath(); ctx.arc(rx, ry, 1.1 + (lvl >= 3 ? 0.25 : 0), 0, Math.PI * 2); ctx.fill();
  }

  // Glow well
  ctx.fillStyle = hexA(c, 0.08 + heat * 0.12 + (lvl - 1) * 0.02);
  ctx.beginPath(); ctx.arc(0, 0, 9.5 + lvl * 0.7, 0, Math.PI * 2); ctx.fill();
}

function drawRankChevrons(ctx, c, lvl) {
  // Small chevrons on the bottom edge of the plate — secondary rank cue
  ctx.fillStyle = hexA(c, 0.85);
  const n = lvl - 1;
  for (let i = 0; i < n; i++) {
    const x = -((n - 1) * 3.2) / 2 + i * 3.2;
    ctx.beginPath();
    ctx.moveTo(x, 12.5 + (lvl - 1) * 0.4);
    ctx.lineTo(x + 1.4, 15 + (lvl - 1) * 0.4);
    ctx.lineTo(x - 1.4, 15 + (lvl - 1) * 0.4);
    ctx.closePath();
    ctx.fill();
  }
}

function hub(ctx, r, c, lvl = 1) {
  const g = ctx.createRadialGradient(0, 0, 1, 0, 0, r);
  g.addColorStop(0, mix(c, '#ffffff', 0.3 + lvl * 0.04));
  g.addColorStop(0.55, mix(c, '#1a2030', 0.2));
  g.addColorStop(1, '#0a0e16');
  ctx.fillStyle = g;
  ctx.beginPath(); ctx.arc(0, 0, r, 0, Math.PI * 2); ctx.fill();
  ctx.strokeStyle = hexA(c, 0.45 + lvl * 0.05);
  ctx.lineWidth = 1;
  ctx.stroke();
  if (lvl >= 4) {
    ctx.fillStyle = hexA('#ffffff', 0.35);
    ctx.beginPath(); ctx.arc(-r * 0.25, -r * 0.25, r * 0.22, 0, Math.PI * 2); ctx.fill();
  }
}

/* ── pulse ───────────────────────────────────────────────────────────────── */

function drawPulse(ctx, barrel, dark, bright, c, heat, lvl) {
  hub(ctx, 5 + lvl * 0.25, c, lvl);
  // L2+: side cooling fins
  if (lvl >= 2) {
    ctx.fillStyle = dark;
    ctx.fillRect(-5, -8.5 - lvl * 0.3, 9, 2.2);
    ctx.fillRect(-5, 6.3 + lvl * 0.3, 9, 2.2);
  }
  // Barrel count grows: L1 dual, L3+ triple, L5 quad-ish with center
  const rails = lvl >= 5 ? 4 : lvl >= 3 ? 3 : 2;
  const span = 4.2 + lvl * 0.35;
  const len = 15 + lvl * 2.4;
  ctx.fillStyle = dark;
  ctx.fillRect(-4, -span - 1.5, 7, span * 2 + 3);
  for (let i = 0; i < rails; i++) {
    const y = rails === 1 ? 0 : -span + (i / (rails - 1)) * span * 2;
    ctx.fillStyle = barrel;
    ctx.fillRect(-1.2, y - 1.5, len, 3);
    ctx.fillStyle = bright;
    ctx.fillRect(len - 3.5, y - 1.9, 3.8, 3.8);
  }
  if (heat > 0.3) {
    ctx.fillStyle = hexA(c, heat * 0.7);
    for (let i = 0; i < rails; i++) {
      const y = rails === 1 ? 0 : -span + (i / (rails - 1)) * span * 2;
      ctx.beginPath(); ctx.arc(len + 1, y, 1.8 + heat, 0, Math.PI * 2); ctx.fill();
    }
  }
  // L4+ overcharge coil behind
  if (lvl >= 4) {
    ctx.strokeStyle = hexA(c, 0.7);
    ctx.lineWidth = 1.5;
    ctx.beginPath(); ctx.arc(-6, 0, 3.5, 0, Math.PI * 2); ctx.stroke();
  }
}

/* ── venom ───────────────────────────────────────────────────────────────── */

function drawVenom(ctx, barrel, dark, bright, c, heat, lvl) {
  hub(ctx, 5.2 + lvl * 0.2, c, lvl);
  const len = 16 + lvl * 2;
  ctx.fillStyle = barrel;
  ctx.fillRect(-2, -3.2 - lvl * 0.3, len, 6.4 + lvl * 0.6);
  // toxin tanks stack with level
  const tanks = 1 + Math.min(2, lvl - 1);
  for (let i = 0; i < tanks; i++) {
    const y = tanks === 1 ? 0 : (i - (tanks - 1) / 2) * 6.5;
    ctx.fillStyle = hexA('#9ff05f', 0.8 + heat * 0.15);
    ctx.beginPath(); ctx.arc(-6 - i * 0.5, y, 5 + lvl * 0.25, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = mix('#9ff05f', '#05070c', 0.4);
    ctx.beginPath(); ctx.arc(-6 - i * 0.5, y, 2.8, 0, Math.PI * 2); ctx.fill();
  }
  ctx.fillStyle = bright;
  ctx.beginPath();
  ctx.moveTo(len - 2, -4 - lvl * 0.3);
  ctx.lineTo(len + 3 + lvl * 0.5, 0);
  ctx.lineTo(len - 2, 4 + lvl * 0.3);
  ctx.closePath(); ctx.fill();
  if (lvl >= 4) {
    ctx.fillStyle = hexA('#c6ff8a', 0.7);
    ctx.beginPath(); ctx.arc(4, -5, 1.4, 0, Math.PI * 2); ctx.fill();
    ctx.beginPath(); ctx.arc(8, 5, 1.2, 0, Math.PI * 2); ctx.fill();
  }
}

/* ── flak ────────────────────────────────────────────────────────────────── */

function drawFlak(ctx, barrel, dark, bright, c, heat, lvl) {
  hub(ctx, 5 + lvl * 0.2, c, lvl);
  const tip = 13 + lvl * 2.2;
  ctx.fillStyle = barrel;
  ctx.beginPath();
  ctx.moveTo(-1, -4);
  ctx.lineTo(tip, -8.5 - lvl * 0.6);
  ctx.lineTo(tip, 8.5 + lvl * 0.6);
  ctx.lineTo(-1, 4);
  ctx.closePath(); ctx.fill();
  // Barrel slots increase
  const slots = 3 + Math.min(3, lvl - 1);
  ctx.fillStyle = dark;
  for (let i = 0; i < slots; i++) {
    const y = -6.5 - lvl * 0.3 + i * ((13 + lvl * 0.6) / (slots - 1 || 1));
    ctx.fillRect(3, y, 8 + lvl, 1.5);
  }
  if (lvl >= 3) {
    ctx.fillStyle = dark;
    ctx.fillRect(-6, -7, 5, 14);
  }
  if (lvl >= 5) {
    ctx.fillStyle = hexA(c, 0.5);
    ctx.fillRect(tip - 2, -9, 3, 18);
  }
}

/* ── cannon ──────────────────────────────────────────────────────────────── */

function drawCannon(ctx, barrel, dark, bright, c, heat, lvl) {
  hub(ctx, 5.4 + lvl * 0.2, c, lvl);
  const len = 17 + lvl * 2.5;
  // L2+ reinforced sleeve
  if (lvl >= 2) {
    ctx.fillStyle = dark;
    ctx.fillRect(-6, -8 - lvl * 0.3, 10, 16 + lvl * 0.6);
  }
  ctx.fillStyle = barrel;
  ctx.fillRect(-3, -5.2 - lvl * 0.25, len, 10.4 + lvl * 0.5);
  // Breech rings
  ctx.fillStyle = dark;
  for (let i = 0; i < Math.min(3, lvl); i++) {
    ctx.fillRect(2 + i * 4, -6.5 - lvl * 0.2, 2.2, 13 + lvl * 0.4);
  }
  // Muzzle brake grows
  ctx.fillStyle = mix(c, '#05070c', 0.2);
  ctx.fillRect(len - 5, -7.5 - lvl * 0.4, 5 + lvl * 0.5, 15 + lvl * 0.8);
  ctx.fillStyle = bright;
  ctx.fillRect(len - 1, -3.5, 3 + lvl * 0.3, 7);
  if (lvl >= 4) {
    // Side ammo drum
    ctx.fillStyle = dark;
    ctx.beginPath(); ctx.arc(-2, 9, 4.5, 0, Math.PI * 2); ctx.fill();
    ctx.strokeStyle = hexA(c, 0.5);
    ctx.lineWidth = 1.2;
    ctx.stroke();
  }
}

/* ── missile ─────────────────────────────────────────────────────────────── */

function drawMissile(ctx, barrel, dark, bright, c, heat, lvl) {
  hub(ctx, 5, c, lvl);
  // Pods: 2 → 3 → 4 as you rank
  const pods = lvl >= 5 ? 4 : lvl >= 3 ? 3 : 2;
  const gap = pods >= 4 ? 5.2 : pods === 3 ? 6 : 5.4;
  for (let i = 0; i < pods; i++) {
    const y = (i - (pods - 1) / 2) * gap;
    ctx.fillStyle = barrel;
    const len = 14 + lvl * 1.5;
    ctx.fillRect(-6, y - 2.8 - lvl * 0.1, len, 5.6 + lvl * 0.2);
    ctx.fillStyle = hexA('#ff9f6d', 0.85 + heat * 0.1);
    ctx.beginPath(); ctx.arc(len - 5, y, 1.8 + lvl * 0.1, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = dark;
    ctx.fillRect(-6, y - 3.8, 3.5, 1.1);
    ctx.fillRect(-6, y + 2.7, 3.5, 1.1);
  }
  if (lvl >= 4) {
    // Top radar dish
    ctx.fillStyle = mix(c, '#ffffff', 0.3);
    ctx.beginPath(); ctx.arc(-2, -pods * gap * 0.5 - 5, 3, 0, Math.PI * 2); ctx.fill();
  }
}

/* ── rail ────────────────────────────────────────────────────────────────── */

function drawRail(ctx, barrel, dark, bright, c, heat, lvl) {
  hub(ctx, 4.6 + lvl * 0.15, c, lvl);
  const len = 24 + lvl * 3;
  ctx.fillStyle = dark;
  ctx.fillRect(-4, -4.5 - lvl * 0.2, len, 2);
  ctx.fillRect(-4, 2.5 + lvl * 0.2, len, 2);
  ctx.fillStyle = barrel;
  ctx.fillRect(-3, -2.2, len - 1, 4.4);
  // Capacitors along the rail
  const caps = 2 + Math.min(3, lvl - 1);
  ctx.fillStyle = hexA(c, 0.6 + heat * 0.3);
  for (let i = 0; i < caps; i++) {
    ctx.fillRect(1 + i * (6 + lvl * 0.3), -7 - lvl * 0.2, 3, 14 + lvl * 0.4);
  }
  ctx.fillStyle = bright;
  ctx.beginPath(); ctx.arc(len - 2, 0, 2.2 + heat + lvl * 0.2, 0, Math.PI * 2); ctx.fill();
  if (lvl >= 5) {
    ctx.strokeStyle = hexA(c, 0.8);
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(len - 2, 0);
    ctx.lineTo(len + 8, 0);
    ctx.stroke();
  }
}

/* ── laser ───────────────────────────────────────────────────────────────── */

function drawLaser(ctx, barrel, dark, bright, c, heat, lvl) {
  hub(ctx, 5.2 + lvl * 0.2, c, lvl);
  const len = 16 + lvl * 2;
  ctx.fillStyle = barrel;
  ctx.fillRect(-3, -3.4 - lvl * 0.2, len, 6.8 + lvl * 0.4);
  // Cooling fins multiply
  ctx.fillStyle = dark;
  const fins = 3 + Math.min(3, lvl - 1);
  for (let i = 0; i < fins; i++) {
    ctx.fillRect(0 + i * 3.2, -6 - lvl * 0.3, 1.5, 12 + lvl * 0.6);
  }
  // Emitter lens
  const er = 3.2 + lvl * 0.35 + heat * 0.5;
  const g = ctx.createRadialGradient(len - 1, 0, 0.5, len - 1, 0, er);
  g.addColorStop(0, '#ffffff');
  g.addColorStop(0.35, bright);
  g.addColorStop(1, hexA(c, 0.15));
  ctx.fillStyle = g;
  ctx.beginPath(); ctx.arc(len - 1, 0, er, 0, Math.PI * 2); ctx.fill();
  if (lvl >= 4) {
    // Secondary focusing ring
    ctx.strokeStyle = hexA(c, 0.7);
    ctx.lineWidth = 1.4;
    ctx.beginPath(); ctx.arc(len - 1, 0, er + 2.5, 0, Math.PI * 2); ctx.stroke();
  }
}

/* ── mortar ──────────────────────────────────────────────────────────────── */

function drawMortar(ctx, barrel, dark, bright, c, heat, lvl) {
  // Bowl grows
  const outer = 10 + lvl * 0.9;
  ctx.fillStyle = mix(c, '#05070c', 0.1);
  ctx.beginPath(); ctx.arc(0, 0, outer, 0, Math.PI * 2); ctx.fill();
  ctx.fillStyle = '#070a12';
  ctx.beginPath(); ctx.arc(0, 0, 5.5 + lvl * 0.2, 0, Math.PI * 2); ctx.fill();
  // Tube thickens and lengthens
  ctx.fillStyle = barrel;
  ctx.fillRect(4, -2.8 - lvl * 0.25, 9 + lvl * 1.8, 5.6 + lvl * 0.5);
  ctx.fillStyle = dark;
  ctx.fillRect(11 + lvl * 1.5, -4 - lvl * 0.3, 4 + lvl * 0.3, 8 + lvl * 0.6);
  // Bipod
  ctx.strokeStyle = hexA(c, 0.55);
  ctx.lineWidth = 1.4 + (lvl >= 3 ? 0.4 : 0);
  ctx.beginPath();
  ctx.moveTo(-7 - lvl * 0.4, 8 + lvl * 0.3);
  ctx.lineTo(0, 2);
  ctx.lineTo(7 + lvl * 0.4, 8 + lvl * 0.3);
  ctx.stroke();
  // L3+: side counterweight
  if (lvl >= 3) {
    ctx.fillStyle = dark;
    ctx.beginPath(); ctx.arc(-8, 0, 3.5, 0, Math.PI * 2); ctx.fill();
  }
  // L4+: sandbags / ammo crates
  if (lvl >= 4) {
    ctx.fillStyle = mix('#3d2b1f', c, 0.15);
    roundRect(ctx, -12, 6, 7, 4.5, 1);
    ctx.fill();
    roundRect(ctx, 5, 6.5, 7, 4, 1);
    ctx.fill();
  }
  if (lvl >= 5) {
    ctx.fillStyle = hexA(c, 0.45);
    ctx.beginPath(); ctx.arc(0, -outer + 1, 2.2, 0, Math.PI * 2); ctx.fill();
  }
}

/* ── tesla ───────────────────────────────────────────────────────────────── */

function drawTesla(ctx, t, c, heat, pulse, lvl) {
  ctx.rotate(-(t.angle || 0));
  const spin = pulse * 2.4;
  const rings = 3 + Math.min(2, lvl - 1);
  ctx.strokeStyle = hexA(c, 0.75 + heat * 0.2);
  ctx.lineWidth = 1.8 + lvl * 0.15;
  for (let i = 0; i < rings; i++) {
    ctx.beginPath();
    ctx.arc(0, 0, 4 + i * (2.6 + lvl * 0.15), spin + i * 1.4, spin + i * 1.4 + 1.9 + lvl * 0.1);
    ctx.stroke();
  }
  hub(ctx, 3.2 + lvl * 0.25 + heat * 0.3, c, lvl);
  // Coil posts increase
  const posts = 4 + (lvl >= 4 ? 2 : 0);
  ctx.fillStyle = mix(c, '#ffffff', 0.35);
  for (let i = 0; i < posts; i++) {
    const a = spin * 0.3 + i * (Math.PI * 2 / posts);
    const r = 8.5 + lvl * 0.6;
    ctx.beginPath();
    ctx.arc(Math.cos(a) * r, Math.sin(a) * r, 1.3 + lvl * 0.1, 0, Math.PI * 2);
    ctx.fill();
  }
  if (lvl >= 5) {
    ctx.strokeStyle = hexA(c, 0.5);
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.arc(0, 0, 14, 0, Math.PI * 2); ctx.stroke();
  }
}

/* ── cryo ────────────────────────────────────────────────────────────────── */

function drawCryo(ctx, t, c, heat, pulse, lvl) {
  ctx.rotate(-(t.angle || 0));
  const p = pulse;
  const arms = 6 + (lvl >= 3 ? 2 : 0) + (lvl >= 5 ? 2 : 0);
  ctx.strokeStyle = hexA(c, 0.7 + heat * 0.15);
  ctx.lineWidth = 1.5 + (lvl >= 4 ? 0.4 : 0);
  for (let i = 0; i < arms; i++) {
    const a = p * 0.9 + i * (Math.PI * 2 / arms);
    const outer = 9.5 + lvl * 0.7;
    ctx.beginPath();
    ctx.moveTo(Math.cos(a) * 4, Math.sin(a) * 4);
    ctx.lineTo(Math.cos(a) * outer, Math.sin(a) * outer);
    ctx.stroke();
  }
  // Crystal core grows facets
  ctx.fillStyle = mix(c, '#ffffff', 0.45 + lvl * 0.05);
  ctx.beginPath();
  const facets = 6 + (lvl >= 4 ? 2 : 0);
  for (let i = 0; i < facets; i++) {
    const a = p * 0.2 + i * (Math.PI * 2 / facets);
    const r = i % 2 === 0 ? 4.8 + lvl * 0.4 : 2.6 + lvl * 0.2;
    const px = Math.cos(a) * r, py = Math.sin(a) * r;
    if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
  }
  ctx.closePath(); ctx.fill();
  if (lvl >= 3) {
    ctx.strokeStyle = hexA('#ffffff', 0.4);
    ctx.lineWidth = 1;
    ctx.beginPath(); ctx.arc(0, 0, 7 + lvl * 0.5, 0, Math.PI * 2); ctx.stroke();
  }
}

/* ── amp ─────────────────────────────────────────────────────────────────── */

function drawAmp(ctx, t, c, heat, pulse, lvl) {
  ctx.rotate(-(t.angle || 0) + pulse * 0.7);
  const s = 9 + lvl * 1.1;
  ctx.fillStyle = hexA(c, 0.9);
  ctx.beginPath();
  ctx.moveTo(0, -s); ctx.lineTo(s * 0.75, 0); ctx.lineTo(0, s); ctx.lineTo(-s * 0.75, 0);
  ctx.closePath(); ctx.fill();
  // Nested diamonds per rank
  for (let k = 1; k < Math.min(lvl, 4); k++) {
    const sk = s * (1 - k * 0.22);
    ctx.fillStyle = k % 2 === 0 ? hexA(c, 0.85) : '#0a0d16';
    ctx.beginPath();
    ctx.moveTo(0, -sk); ctx.lineTo(sk * 0.75, 0); ctx.lineTo(0, sk); ctx.lineTo(-sk * 0.75, 0);
    ctx.closePath(); ctx.fill();
  }
  const sparks = 3 + Math.min(3, lvl - 1);
  ctx.fillStyle = hexA(c, 0.85);
  for (let i = 0; i < sparks; i++) {
    const a = pulse * 1.4 + i * (Math.PI * 2 / sparks);
    ctx.beginPath();
    ctx.arc(Math.cos(a) * (s + 2 + lvl), Math.sin(a) * (s + 2 + lvl), 1.2 + lvl * 0.1, 0, Math.PI * 2);
    ctx.fill();
  }
}

/* ── flame ───────────────────────────────────────────────────────────────── */

function drawFlame(ctx, barrel, dark, bright, c, heat, pulse, lvl) {
  hub(ctx, 5.2 + lvl * 0.2, c, lvl);
  // Fuel tanks: 2 → 3 → 4
  const tanks = 2 + (lvl >= 3 ? 1 : 0) + (lvl >= 5 ? 1 : 0);
  for (let i = 0; i < tanks; i++) {
    const y = (i - (tanks - 1) / 2) * 5.5;
    ctx.fillStyle = dark;
    ctx.beginPath();
    ctx.ellipse(-5 - (lvl >= 4 ? 1 : 0), y, 3 + lvl * 0.15, 4 + lvl * 0.2, 0, 0, Math.PI * 2);
    ctx.fill();
  }
  const tip = 13 + lvl * 1.8;
  ctx.fillStyle = barrel;
  ctx.beginPath();
  ctx.moveTo(-1, -4.5 - lvl * 0.3);
  ctx.lineTo(tip, -7 - lvl * 0.5);
  ctx.lineTo(tip, 7 + lvl * 0.5);
  ctx.lineTo(-1, 4.5 + lvl * 0.3);
  ctx.closePath(); ctx.fill();
  const flick = 0.7 + Math.sin(pulse * 14) * 0.3;
  ctx.fillStyle = hexA('#ffd166', 0.5 * flick + heat * 0.3);
  ctx.beginPath();
  ctx.moveTo(6, -2.2 - lvl * 0.2);
  ctx.lineTo(tip + 2 + heat * 3 + lvl, 0);
  ctx.lineTo(6, 2.2 + lvl * 0.2);
  ctx.closePath(); ctx.fill();
  if (lvl >= 4) {
    ctx.fillStyle = hexA('#ffffff', 0.45 * flick);
    ctx.beginPath();
    ctx.moveTo(9, -1);
    ctx.lineTo(tip + 1, 0);
    ctx.lineTo(9, 1);
    ctx.closePath(); ctx.fill();
  }
}

/* ── sniper ──────────────────────────────────────────────────────────────── */

function drawSniper(ctx, barrel, dark, bright, c, heat, lvl) {
  hub(ctx, 4.5 + lvl * 0.15, c, lvl);
  const len = 26 + lvl * 2.8;
  ctx.fillStyle = dark;
  ctx.fillRect(-5, -3 - lvl * 0.15, 9, 6 + lvl * 0.3);
  ctx.fillStyle = barrel;
  ctx.fillRect(2, -1.9 - lvl * 0.1, len - 4, 3.8 + lvl * 0.2);
  // Scope grows
  ctx.fillStyle = mix(c, '#05070c', 0.15);
  ctx.fillRect(2, -6.8 - lvl * 0.3, 8 + lvl, 3 + lvl * 0.2);
  ctx.fillStyle = bright;
  ctx.beginPath(); ctx.arc(6 + lvl * 0.3, -5.2 - lvl * 0.2, 1.6 + lvl * 0.15, 0, Math.PI * 2); ctx.fill();
  // Bipod → bipod + stabilizer
  ctx.strokeStyle = hexA(c, 0.55);
  ctx.lineWidth = 1.3;
  ctx.beginPath();
  ctx.moveTo(8, 2); ctx.lineTo(3, 9 + lvl * 0.3);
  ctx.moveTo(8, 2); ctx.lineTo(13, 9 + lvl * 0.3);
  ctx.stroke();
  if (lvl >= 3) {
    // Suppressor
    ctx.fillStyle = dark;
    ctx.fillRect(len - 8, -2.4, 7 + lvl * 0.5, 4.8);
  }
  if (lvl >= 4) {
    // Magazine
    ctx.fillStyle = dark;
    ctx.fillRect(4, 2.5, 4, 5);
  }
  if (heat > 0.35) {
    ctx.strokeStyle = hexA(c, heat);
    ctx.lineWidth = 1.5;
    ctx.beginPath(); ctx.arc(len - 2, 0, 2.8 + heat, 0, Math.PI * 2); ctx.stroke();
  }
}

/* ── nova ────────────────────────────────────────────────────────────────── */

function drawNova(ctx, t, c, heat, pulse, lvl) {
  ctx.rotate(-(t.angle || 0));
  const p = pulse * 1.5;
  hub(ctx, 5.2 + lvl * 0.3 + heat * 0.4, c, lvl);
  const rings = 2 + Math.min(3, lvl - 1);
  ctx.strokeStyle = hexA(c, 0.5 + heat * 0.3);
  ctx.lineWidth = 1.4 + lvl * 0.1;
  for (let i = 0; i < rings; i++) {
    const r = 6.5 + i * (2.8 + lvl * 0.15) + Math.sin(p + i) * 0.7;
    ctx.beginPath(); ctx.arc(0, 0, r, 0, Math.PI * 2); ctx.stroke();
  }
  const spokes = 4 + (lvl >= 3 ? 2 : 0) + (lvl >= 5 ? 2 : 0);
  ctx.strokeStyle = hexA(c, 0.65);
  ctx.lineWidth = 1.2;
  for (let i = 0; i < spokes; i++) {
    const a = p * 0.4 + i * (Math.PI * 2 / spokes);
    ctx.beginPath();
    ctx.moveTo(Math.cos(a) * 3, Math.sin(a) * 3);
    ctx.lineTo(Math.cos(a) * (10 + lvl * 0.8), Math.sin(a) * (10 + lvl * 0.8));
    ctx.stroke();
  }
}

/* ── gatling ─────────────────────────────────────────────────────────────── */

function drawGatling(ctx, barrel, dark, bright, c, heat, pulse, lvl, t) {
  hub(ctx, 5 + lvl * 0.15, c, lvl);
  const spin = (t.spin || 0) * 8 + pulse * 0.5;
  // Barrels: 5 → 6 → 7 → 8
  const barrels = 5 + Math.min(3, lvl - 1);
  const reach = 15 + lvl * 1.6;
  for (let i = 0; i < barrels; i++) {
    const a = spin + i * (Math.PI * 2 / barrels);
    const oy = Math.sin(a) * (3.2 + lvl * 0.2);
    const scale = 0.5 + 0.5 * ((Math.cos(a) + 1) / 2);
    ctx.fillStyle = mix(barrel, '#05070c', 1 - scale);
    ctx.fillRect(-1, oy - 1.2 * scale, reach, 2.4 * scale);
  }
  // Shroud / receiver thickens
  ctx.fillStyle = dark;
  ctx.fillRect(-4, -6 - lvl * 0.3, 8 + lvl * 0.5, 12 + lvl * 0.6);
  ctx.fillStyle = hexA(c, 0.35 + heat * 0.4);
  ctx.fillRect(2, -5 - lvl * 0.2, 2.2, 10 + lvl * 0.4);
  if (lvl >= 3) {
    // Ammo belt box
    ctx.fillStyle = mix('#2a2030', c, 0.15);
    roundRect(ctx, -10, 4, 8, 6, 1.5);
    ctx.fill();
  }
  if (lvl >= 5) {
    ctx.fillStyle = bright;
    ctx.fillRect(reach - 2, -1.5, 3, 3);
  }
}

/* ── singularity (black hole) ────────────────────────────────────────────── */

function drawSingularity(ctx, t, c, heat, pulse, lvl) {
  ctx.rotate(-(t.angle || 0));
  const p = pulse * 1.8;
  // Outer accretion disc
  ctx.strokeStyle = hexA(c, 0.55 + heat * 0.3);
  ctx.lineWidth = 2 + lvl * 0.2;
  for (let i = 0; i < 2 + Math.min(2, lvl - 1); i++) {
    ctx.beginPath();
    ctx.ellipse(0, 0, 11 + i * 2.2 + lvl * 0.4, 5.5 + i * 0.8, p * 0.15, 0, Math.PI * 2);
    ctx.stroke();
  }
  // Event horizon
  const g = ctx.createRadialGradient(0, 0, 1, 0, 0, 6 + lvl * 0.5);
  g.addColorStop(0, '#05060c');
  g.addColorStop(0.55, mix(c, '#05070c', 0.5));
  g.addColorStop(1, hexA(c, 0.15));
  ctx.fillStyle = g;
  ctx.beginPath(); ctx.arc(0, 0, 5.5 + lvl * 0.45, 0, Math.PI * 2); ctx.fill();
  // Orbiting sparks
  const n = 4 + lvl;
  ctx.fillStyle = mix(c, '#ffffff', 0.4);
  for (let i = 0; i < n; i++) {
    const a = p + i * (Math.PI * 2 / n);
    const r = 9 + lvl * 0.6;
    ctx.beginPath();
    ctx.arc(Math.cos(a) * r, Math.sin(a) * r * 0.45, 1.3, 0, Math.PI * 2);
    ctx.fill();
  }
}

/* ── oblivion (death ray) ────────────────────────────────────────────────── */

function drawOblivion(ctx, barrel, dark, bright, c, heat, lvl) {
  hub(ctx, 5.8 + lvl * 0.25, c, lvl);
  const len = 22 + lvl * 2.5;
  // Heavy chassis
  ctx.fillStyle = dark;
  ctx.fillRect(-7, -7 - lvl * 0.2, 12, 14 + lvl * 0.4);
  // Focusing rings along barrel
  ctx.fillStyle = barrel;
  ctx.fillRect(-2, -3.5 - lvl * 0.15, len, 7 + lvl * 0.3);
  ctx.fillStyle = hexA(c, 0.7 + heat * 0.25);
  for (let i = 0; i < 2 + Math.min(2, lvl - 1); i++) {
    ctx.fillRect(4 + i * 5, -5.5 - lvl * 0.2, 2.5, 11 + lvl * 0.4);
  }
  // Emitter
  const g = ctx.createRadialGradient(len - 1, 0, 0.5, len - 1, 0, 4 + heat + lvl * 0.3);
  g.addColorStop(0, '#ffffff');
  g.addColorStop(0.4, bright);
  g.addColorStop(1, hexA(c, 0.1));
  ctx.fillStyle = g;
  ctx.beginPath(); ctx.arc(len - 1, 0, 3.5 + heat * 0.8 + lvl * 0.2, 0, Math.PI * 2); ctx.fill();
  if (lvl >= 4) {
    ctx.strokeStyle = hexA(c, 0.75);
    ctx.lineWidth = 1.5;
    ctx.beginPath(); ctx.arc(len - 1, 0, 6, 0, Math.PI * 2); ctx.stroke();
  }
}

/* ── tempest (storm tower) ───────────────────────────────────────────────── */

function drawTempest(ctx, t, c, heat, pulse, lvl) {
  ctx.rotate(-(t.angle || 0));
  const p = pulse * 2.5;
  hub(ctx, 5 + lvl * 0.2, c, lvl);
  // Lightning rods
  const rods = 3 + Math.min(2, lvl - 1);
  ctx.strokeStyle = hexA(c, 0.85);
  ctx.lineWidth = 1.6;
  for (let i = 0; i < rods; i++) {
    const a = p * 0.2 + i * (Math.PI * 2 / rods);
    const x0 = Math.cos(a) * 4, y0 = Math.sin(a) * 4;
    const x1 = Math.cos(a) * (10 + lvl * 0.8), y1 = Math.sin(a) * (10 + lvl * 0.8);
    ctx.beginPath();
    ctx.moveTo(x0, y0);
    // zig-zag bolt
    const mx = (x0 + x1) / 2 + Math.cos(a + 1.2) * 2.5;
    const my = (y0 + y1) / 2 + Math.sin(a + 1.2) * 2.5;
    ctx.lineTo(mx, my);
    ctx.lineTo(x1, y1);
    ctx.stroke();
  }
  // Cloud cap
  ctx.fillStyle = mix(c, '#1a2030', 0.35);
  ctx.beginPath();
  ctx.ellipse(0, -2, 7 + lvl * 0.4, 4 + lvl * 0.2, 0, 0, Math.PI * 2);
  ctx.fill();
  if (heat > 0.3) {
    ctx.fillStyle = hexA('#ffffff', heat * 0.5);
    ctx.beginPath(); ctx.arc(0, 0, 2.5, 0, Math.PI * 2); ctx.fill();
  }
}
