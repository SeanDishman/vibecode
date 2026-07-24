// Purely cosmetic transients: sparks, shockwave rings, tracer flashes.
// Kept in their own module (and out of the run state) so nothing in the
// simulation ever has to reach into rendering to make a puff of light.

export const fx = [];
export const parts = [];

const PART_CAP = 700;

export function burst(x, y, color, n) {
  if (parts.length > PART_CAP) return;
  for (let i = 0; i < n; i++) {
    const a = Math.random() * Math.PI * 2, sp = 30 + Math.random() * 130;
    parts.push({
      x, y, vx: Math.cos(a) * sp, vy: Math.sin(a) * sp,
      life: 0.32 + Math.random() * 0.35, t: 0, color, r: 0.9 + Math.random() * 1.7,
    });
  }
}

export function ring(x, y, color, r, life) {
  fx.push({ kind: 'ring', x, y, color, r, t: 0, life });
}

export function beamFx(x1, y1, x2, y2, color, life = 0.09, width = 2) {
  fx.push({ kind: 'beam', x1, y1, x2, y2, color, t: 0, life, width });
}

export function muzzleFlash(x, y, a, color) {
  fx.push({ kind: 'flash', x, y, a, color, t: 0, life: 0.07 });
}

export function updateFx(dt) {
  for (let i = fx.length - 1; i >= 0; i--) {
    const f = fx[i];
    f.t += dt;
    if (f.t >= f.life) fx.splice(i, 1);
  }
  for (let i = parts.length - 1; i >= 0; i--) {
    const p = parts[i];
    p.t += dt;
    if (p.t >= p.life) { parts.splice(i, 1); continue; }
    p.x += p.vx * dt; p.y += p.vy * dt;
    p.vx *= 0.94; p.vy *= 0.94;
  }
}

export function clearFx() {
  fx.length = 0;
  parts.length = 0;
}
