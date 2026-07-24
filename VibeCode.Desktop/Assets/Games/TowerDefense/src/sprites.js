// Hundreds of tiny glowing circles are far too many to light individually, so
// every enemy body is pre-baked into a small canvas once per resolution and
// then blitted. Cache key is colour + radius, which is also how status effects
// are drawn: a chilled circle simply asks for a bluer version of itself.
import { hexA, mix } from './colors.js';

const cache = new Map();
let scale = 2;

function bake(color, r, glow) {
  const pad = Math.ceil(r * (glow ? 2.6 : 1.5)) + 2;
  const size = Math.ceil(pad * 2 * scale);
  const c = document.createElement('canvas');
  c.width = size; c.height = size;

  const g = c.getContext('2d');
  g.scale(scale, scale);
  g.translate(pad, pad);

  if (glow) {
    const halo = g.createRadialGradient(0, 0, r * 0.4, 0, 0, r * 2.5);
    halo.addColorStop(0, hexA(color, 0.5));
    halo.addColorStop(0.45, hexA(color, 0.16));
    halo.addColorStop(1, hexA(color, 0));
    g.fillStyle = halo;
    g.beginPath(); g.arc(0, 0, r * 2.5, 0, Math.PI * 2); g.fill();
  }

  const body = g.createRadialGradient(-r * 0.35, -r * 0.4, r * 0.1, 0, 0, r);
  body.addColorStop(0, mix(color, '#ffffff', 0.55));
  body.addColorStop(0.55, color);
  body.addColorStop(1, mix(color, '#05060a', 0.55));
  g.fillStyle = body;
  g.beginPath(); g.arc(0, 0, r, 0, Math.PI * 2); g.fill();

  g.strokeStyle = hexA(mix(color, '#ffffff', 0.4), 0.85);
  g.lineWidth = Math.max(0.6, r * 0.16);
  g.beginPath(); g.arc(0, 0, r - g.lineWidth / 2, 0, Math.PI * 2); g.stroke();

  return { img: c, pad, k: scale };
}

export function sprite(color, r, glow = true) {
  const key = `${color}|${r.toFixed(2)}|${glow ? 1 : 0}`;
  let s = cache.get(key);
  if (!s) { s = bake(color, r, glow); cache.set(key, s); }
  return s;
}

export function drawSprite(ctx, s, x, y, alpha = 1) {
  if (alpha !== 1) ctx.globalAlpha = alpha;
  ctx.drawImage(s.img, x - s.pad, y - s.pad, s.img.width / s.k, s.img.height / s.k);
  if (alpha !== 1) ctx.globalAlpha = 1;
}

/** Re-bake at the new device resolution when the window size changes enough. */
export function rescaleSprites(want) {
  const next = Math.max(1, Math.min(3, want));
  if (Math.abs(next - scale) < 0.2 && cache.size) return;
  scale = next;
  cache.clear();
}
