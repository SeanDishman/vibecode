// render.js — side-on ocean. The boat is anchored on its mark and the camera
// never moves: the whole point of an idle game is that the scene stays framed
// while you watch it work.
//
// Depth is COMPRESSED, not scrolled. The water column between the waterline and
// the bottom of the canvas always represents 0..viewDepth(), so a level-1 rod
// and a level-13 rod both show their hook on screen — the ocean just gets
// steeper. Everything underwater is positioned through dy(), never raw pixels.

import { ZONES, clamp, lerp } from './core.js';
import { game, ROD_SLOTS, CAST_TIME, viewDepth, levelOf } from './game.js';
import { weather, waveAmp, skyColors, sunTint } from './weather.js';

let cv, ctx, dpr = 1, cw = 0, ch = 0;

export function initRender(canvas) {
  cv = canvas;
  ctx = cv.getContext('2d', { alpha: false });
  resize();
}

export function resize() {
  if (!cv) return;
  dpr = Math.max(1, Math.min(2, window.devicePixelRatio || 1));
  const r = cv.getBoundingClientRect();
  cw = Math.max(2, Math.round(r.width * dpr));
  ch = Math.max(2, Math.round(r.height * dpr));
  if (cv.width !== cw || cv.height !== ch) { cv.width = cw; cv.height = ch; }
  ctx.imageSmoothingEnabled = true;
}

export const view = () => ({ w: cw / dpr, h: ch / dpr });
const P = n => n * dpr;

/** Waterline, in CSS px from the top. Sky gets the majority of the frame: since
 *  nothing is drawn below the surface any more, a tall window spent most of its
 *  height on an empty blue field. The floor keeps a usable sea on a short window,
 *  and the ceiling stops the boat sinking to the very bottom of a huge one. */
function sy() {
  const v = view();
  return clamp(v.h * 0.58, 200, 620);
}

/** Boat centre, in CSS px. Dead centre — she is out in open water with no shore
 *  to sit off, so there is nothing to weight the framing to one side. */
function bx() { return view().w * 0.5; }

/** World depth -> CSS y. The compression that makes the whole game visible. */
function dy(d) {
  const v = view(), top = sy();
  return top + (clamp(d, 0, viewDepth()) / viewDepth()) * (v.h - top);
}

/* ── palette helpers (unchanged from the original) ─────────────────────────── */
function waterColorAt(d) {
  for (const z of ZONES) {
    if (d < z.to) return mix(z.col, z.deep, clamp((d - z.from) / (z.to - z.from), 0, 1));
  }
  return ZONES[ZONES.length - 1].deep;
}

function mix(a, b, t) {
  const pa = parseInt(a.slice(1), 16), pb = parseInt(b.slice(1), 16);
  const r = Math.round(lerp((pa >> 16) & 255, (pb >> 16) & 255, t));
  const g = Math.round(lerp((pa >> 8) & 255, (pb >> 8) & 255, t));
  const bl = Math.round(lerp(pa & 255, pb & 255, t));
  return `rgb(${r},${g},${bl})`;
}

/* ── frame ────────────────────────────────────────────────────────────────── */
export function draw() {
  if (!ctx) return;
  const v = view();
  drawSky(v);
  drawSunGlow(v);
  drawHorizonHaze(v);
  drawGulls(v);
  drawWater(v);
  drawShafts(v);
  drawSurface(v);
  drawBoat(v);
  drawLines(v);
  drawDepthRuler(v);
  drawPops(v);
  drawRain(v);
  drawLightning(v);
  drawFlash();
}

/* ── sky, land, water ─────────────────────────────────────────────────────── */
function drawSky(v) {
  const h = P(sy());
  const c = skyColors();
  const g = ctx.createLinearGradient(0, 0, 0, Math.max(1, h));
  g.addColorStop(0, c.top);
  g.addColorStop(1, c.bottom);
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, cw, h);

  // Clouds drift; storms drag them lower and darker. They are spread down the
  // whole sky band rather than stacked in the top 60px — with the horizon this
  // low that used to leave the middle of the frame completely bare.
  const storm = weather.storm;
  ctx.fillStyle = `rgba(${storm > 0.5 ? '90,100,115' : '235,242,250'},${0.13 + storm * 0.22})`;
  const band = Math.max(30, sy() - 70);
  for (let i = 0; i < CLOUD_HF.length; i++) {
    // Further down the band means further away: smaller, and drifting slower.
    const far = CLOUD_HF[i];
    const cx = ((i * 337 + weather.t * (10 + storm * 34) * (1 - far * 0.55)) % (v.w + 340)) - 170;
    const cy = 16 + far * band;
    if (cy > sy() - 34) continue;
    ctx.beginPath();
    ctx.ellipse(P(cx), P(cy), P((52 + (i % 3) * 20) * (1 - far * 0.34)),
      P((11 + (i % 2) * 5) * (1 - far * 0.3)), 0, 0, 6.283);
    ctx.fill();
  }
}

/** Height of each cloud down the sky band, 0 = overhead. Hand-picked rather than
 *  evenly spaced so they never line up into a visible ladder. */
const CLOUD_HF = [0.05, 0.31, 0.13, 0.54, 0.22, 0.70, 0.41, 0.61, 0.09];

/** A low sun burning through during a honey break: a soft disc sitting just off
 *  the horizon, plus a warm bloom across the whole sky band. */
function drawSunGlow(v) {
  const g = sunTint();
  if (g < 0.01) return;
  const base = sy();
  const sx = v.w * 0.22, syy = base - 46;

  ctx.save();
  ctx.globalCompositeOperation = 'lighter';

  const bloom = ctx.createRadialGradient(P(sx), P(syy), 0, P(sx), P(syy), P(210));
  bloom.addColorStop(0, `rgba(255,214,140,${0.5 * g})`);
  bloom.addColorStop(0.45, `rgba(255,170,80,${0.16 * g})`);
  bloom.addColorStop(1, 'rgba(255,150,60,0)');
  ctx.fillStyle = bloom;
  ctx.fillRect(0, 0, cw, P(base));

  ctx.fillStyle = `rgba(255,240,205,${0.82 * g})`;
  ctx.beginPath();
  ctx.arc(P(sx), P(syy), P(15), 0, 6.283);
  ctx.fill();
  ctx.restore();

  // Glitter path running from under the sun toward the viewer.
  ctx.save();
  ctx.globalCompositeOperation = 'lighter';
  for (let i = 0; i < 16; i++) {
    const t = i / 16;
    const y = base + 6 + t * (v.h - base) * 0.5;
    const w = 12 + t * 66 + Math.sin(weather.t * 2.4 + i) * 5;
    ctx.fillStyle = `rgba(255,206,128,${0.20 * g * (1 - t)})`;
    ctx.fillRect(P(sx - w / 2), P(y), P(w), P(2.4));
  }
  ctx.restore();
}

/* ── gulls ────────────────────────────────────────────────────────────────
   Wheeling over the boat whenever the weather is decent. Pure decoration, and
   pure function of time, so there is no state to keep or reset. */
/** `rx` is orbit radius as a fraction of the view width and `hf` is height as a
 *  fraction of the sky band, so the flock scales with the window instead of
 *  ending up tangled in the rigging or hidden behind the HUD. */
const GULLS = [
  { rx: 0.20, sp: 0.22, ph: 0.0, hf: 0.58, sq: 0.30, sz: 1.15 },
  { rx: 0.28, sp: -0.17, ph: 2.1, hf: 0.34, sq: 0.26, sz: 0.95 },
  { rx: 0.37, sp: 0.13, ph: 4.0, hf: 0.70, sq: 0.20, sz: 0.78 },
  { rx: 0.14, sp: -0.27, ph: 5.2, hf: 0.18, sq: 0.34, sz: 0.66 },
];

function drawGulls(v) {
  // They clear off as the weather closes in, and crowd the boat in the sun.
  const out = clamp(1 - weather.storm * 1.8, 0, 1) * (0.62 + sunTint() * 0.38);
  if (out < 0.04) return;

  const cx = bx(), base = sy();
  const hi = 46, lo = base - 16;          // clear of the HUD bar and of the sea
  if (lo <= hi) return;

  ctx.save();
  ctx.globalAlpha = out;
  ctx.strokeStyle = sunTint() > 0.3 ? 'rgba(44,26,16,.86)' : 'rgba(240,248,253,.92)';
  ctx.lineCap = 'round';

  for (const g of GULLS) {
    const a = weather.t * g.sp + g.ph;
    const rad = v.w * g.rx;
    const x = cx + Math.cos(a) * rad;
    const y = clamp(lerp(hi, lo, g.hf) + Math.sin(a) * rad * g.sq * 0.34, hi, lo);
    if (x < -30 || x > v.w + 30) continue;
    ctx.lineWidth = Math.max(1, P(1.5 * g.sz));

    // Wingbeat: slower on the far side of the circle, as if gliding.
    const beat = Math.sin(weather.t * 5.2 + g.ph) * 0.5 + 0.5;
    const span = (11 + beat * 6) * g.sz;
    const lift = (1.8 + beat * 7) * g.sz;
    const face = Math.cos(a) >= 0 ? 1 : -1;

    ctx.beginPath();
    ctx.moveTo(P(x - span), P(y + lift * 0.5));
    ctx.quadraticCurveTo(P(x - span * 0.42), P(y - lift), P(x), P(y));
    ctx.quadraticCurveTo(P(x + span * 0.42), P(y - lift), P(x + span), P(y + lift * 0.5));
    ctx.stroke();

    // A stub of a body so they are not just floating chevrons.
    ctx.beginPath();
    ctx.moveTo(P(x), P(y));
    ctx.lineTo(P(x + face * 2.4 * g.sz), P(y + 0.8 * g.sz));
    ctx.stroke();
  }
  ctx.restore();
}

/** Open water to the horizon. The hills and the headland-with-a-lighthouse that
 *  used to sit here are gone: the boat is meant to be out in the middle of the
 *  ocean, and a shoreline a hundred metres off her bow said the opposite.
 *
 *  What replaces them is atmosphere, not scenery — a band of haze thickening
 *  into the waterline, which is what actually reads as "that is a long way off".
 *  Without it the horizon is a ruled line across the canvas. */
function drawHorizonHaze(v) {
  const base = sy();
  const band = 30;
  const strength = 0.16 + sunTint() * 0.18 - weather.storm * 0.07;
  if (strength <= 0.01) return;
  const g = ctx.createLinearGradient(0, P(base - band), 0, P(base));
  g.addColorStop(0, 'rgba(214,234,248,0)');
  g.addColorStop(1, `rgba(214,234,248,${clamp(strength, 0, 1)})`);
  ctx.fillStyle = g;
  ctx.fillRect(0, P(base - band), cw, P(band));
}

function drawWater(v) {
  const top = P(sy());
  const g = ctx.createLinearGradient(0, top, 0, ch);
  const vd = viewDepth();
  for (let i = 0; i <= 16; i++) g.addColorStop(i / 16, waterColorAt((i / 16) * vd));
  ctx.fillStyle = g;
  ctx.fillRect(0, top, cw, ch - top);
}

function drawShafts(v) {
  if (weather.storm > 0.65) return;
  const top = P(sy());
  ctx.save();
  ctx.globalCompositeOperation = 'lighter';
  ctx.fillStyle = `rgba(150,220,255,${0.05 * (1 - weather.storm)})`;
  for (let i = 0; i < 5; i++) {
    const x = ((i * 233 + weather.t * 7) % (v.w + 300)) - 150;
    ctx.beginPath();
    ctx.moveTo(P(x), top);
    ctx.lineTo(P(x + 44), top);
    ctx.lineTo(P(x + 150), ch);
    ctx.lineTo(P(x + 26), ch);
    ctx.closePath();
    ctx.fill();
  }
  ctx.restore();
}

/** The animated waterline itself, drawn over the water so the hull sits in it. */
function drawSurface(v) {
  const amp = waveAmp();
  const base = sy();
  ctx.beginPath();
  ctx.moveTo(0, P(base + 26));
  for (let x = 0; x <= v.w; x += 8) {
    const y = base + Math.sin(x * 0.03 + weather.t * 2.1) * amp
      + Math.sin(x * 0.011 - weather.t * 1.3) * amp * 0.6;
    ctx.lineTo(P(x), P(y));
  }
  ctx.lineTo(cw, P(base + 26));
  ctx.closePath();
  // Low sun turns the top of the water gold instead of cyan.
  const g = sunTint();
  ctx.fillStyle = g > 0.01
    ? `rgba(${120 + 135 * g | 0},${205 - 12 * g | 0},${235 - 110 * g | 0},.30)`
    : 'rgba(120,205,235,.30)';
  ctx.fill();

  ctx.strokeStyle = g > 0.01
    ? `rgba(255,${240 - 12 * g | 0},${255 - 130 * g | 0},.55)`
    : 'rgba(190,240,255,.55)';
  ctx.lineWidth = Math.max(1, P(1.4));
  ctx.beginPath();
  for (let x = 0; x <= v.w; x += 8) {
    const y = base + Math.sin(x * 0.03 + weather.t * 2.1) * amp
      + Math.sin(x * 0.011 - weather.t * 1.3) * amp * 0.6;
    if (x === 0) ctx.moveTo(P(x), P(y)); else ctx.lineTo(P(x), P(y));
  }
  ctx.stroke();
}

/* ── the boat ─────────────────────────────────────────────────────────────── */

const HULL_L = 92;    // half-length at the rail
const DECK_UP = 21;   // rail height above the waterline

/** Height of the rail (the deck's top edge) at any x along the hull, fitted to
 *  the sheer curve the hull is drawn with. Everything on deck is placed through
 *  this: placing by eye is why the wheelhouse, winch and railing used to hover
 *  a few px above the deck everywhere except the stern. */
function railY(x) {
  const u = (x / HULL_L + 1) / 2;
  return -2 - 13.85 * u + 4.85 * u * u;
}

/** Deck y in CSS px, including the swell bob. */
function deckY() { return sy() - DECK_UP + Math.sin(game.boat.bob) * (1.8 + weather.storm * 2.2); }

function drawBoat(v) {
  const x = bx(), y = deckY();
  ctx.save();
  ctx.translate(P(x), P(y));
  ctx.rotate(game.boat.tilt);

  drawHull();
  drawRail();
  drawWheelhouse();
  drawMast();
  drawUpgradeGear();
  drawNet();
  for (let i = 0; i < game.lines.length; i++) drawAngler(game.lines[i]);

  ctx.restore();
}

function drawHull() {
  const L = HULL_L, H = DECK_UP + 15;

  // Reflection smear on the water under the hull.
  ctx.fillStyle = 'rgba(4,20,32,.28)';
  ctx.beginPath();
  ctx.ellipse(0, P(H * 0.92), P(L * 0.98), P(7), 0, 0, 6.283);
  ctx.fill();

  // Hull: sheer line rises toward the bow (right).
  ctx.beginPath();
  ctx.moveTo(P(-L), P(-2));
  ctx.quadraticCurveTo(P(-L * 0.2), P(-8), P(L), P(-11));      // rail, bow up
  ctx.lineTo(P(L + 6), P(6));                                   // stem
  ctx.quadraticCurveTo(P(L * 0.3), P(H), P(-L * 0.72), P(H));   // bottom
  ctx.lineTo(P(-L - 4), P(H - 12));                             // transom
  ctx.closePath();
  ctx.fillStyle = '#20303f';
  ctx.fill();

  ctx.save();
  ctx.clip();
  // Planking
  ctx.fillStyle = '#e9eef4';
  ctx.fillRect(P(-L - 8), P(-12), P(L * 2 + 20), P(16));
  ctx.fillStyle = '#d24b45';                                    // bootline
  ctx.fillRect(P(-L - 8), P(4), P(L * 2 + 20), P(7));
  ctx.fillStyle = '#16222e';                                    // antifoul below the line
  ctx.fillRect(P(-L - 8), P(11), P(L * 2 + 20), P(H));
  ctx.strokeStyle = 'rgba(30,45,60,.22)';
  ctx.lineWidth = Math.max(1, P(1));
  for (let i = -10; i < 4; i += 5) {
    ctx.beginPath();
    ctx.moveTo(P(-L - 8), P(i));
    ctx.lineTo(P(L + 8), P(i - 3));
    ctx.stroke();
  }
  ctx.restore();

  // Rub rail
  ctx.strokeStyle = '#0f1a24';
  ctx.lineWidth = Math.max(1, P(2.4));
  ctx.beginPath();
  ctx.moveTo(P(-L - 3), P(-2));
  ctx.quadraticCurveTo(P(-L * 0.2), P(-8), P(L + 4), P(-11));
  ctx.stroke();

  // Portholes on the topsides, forward of the name board.
  for (const px of [-2, 26]) {
    const py = railY(px) + 5.2;
    ctx.fillStyle = '#10222e';
    ctx.beginPath(); ctx.arc(P(px), P(py), P(2.5), 0, 6.283); ctx.fill();
    ctx.strokeStyle = '#c8d4de';
    ctx.lineWidth = Math.max(1, P(0.9));
    ctx.beginPath(); ctx.arc(P(px), P(py), P(2.5), 0, 6.283); ctx.stroke();
  }

  // Tyre fenders over the side
  for (const fx of [-64, -26, 14]) {
    ctx.strokeStyle = '#14202b';
    ctx.lineWidth = P(3);
    ctx.beginPath();
    ctx.arc(P(fx), P(6), P(6), 0, 6.283);
    ctx.stroke();
  }

  // Name board on the transom
  ctx.fillStyle = '#f2f6fa';
  ctx.font = `700 ${Math.round(P(7))}px "Cascadia Code", Consolas, monospace`;
  ctx.textAlign = 'center';
  ctx.fillText('SALTY DOG', P(-L * 0.45), P(1));
  ctx.textAlign = 'left';
}

/** The bare working deck: a gunwale rail and nothing else. Everything that used
 *  to be scattered here — crates, barrel, winch, rope — is bought now, and lives
 *  in drawUpgradeGear(). She leaves the quay empty on purpose. */
function drawRail() {
  // Posts stand ON the deck and the handrail follows the sheer, instead of
  // running level while the deck curves away under it.
  ctx.strokeStyle = '#9fb0c0';
  ctx.lineWidth = Math.max(1, P(1.2));
  ctx.beginPath();
  for (let x = -86; x <= 80; x += 2) {
    const y = railY(x) - 10.5;
    if (x === -86) ctx.moveTo(P(x), P(y)); else ctx.lineTo(P(x), P(y));
  }
  ctx.stroke();
  for (let x = -84; x <= 80; x += 16.5) {
    ctx.beginPath();
    ctx.moveTo(P(x), P(railY(x)));
    ctx.lineTo(P(x), P(railY(x) - 10.5));
    ctx.stroke();
  }
}

function drawWheelhouse() {
  // Floor sits on the deck (railY amidships ≈ -8), not up on stilts of air.
  const wx = 4, wy = -42, w = 44, h = 34;

  // Cabin body
  ctx.fillStyle = '#f4f8fc';
  ctx.fillRect(P(wx), P(wy), P(w), P(h));
  ctx.strokeStyle = '#16222e';
  ctx.lineWidth = Math.max(1, P(1.6));
  ctx.strokeRect(P(wx), P(wy), P(w), P(h));

  // Roof with an overhang
  ctx.fillStyle = '#1d5c73';
  ctx.fillRect(P(wx - 4), P(wy - 6), P(w + 8), P(7));

  // Windows — lit warm at night-ish storm light
  const lit = weather.storm > 0.45;
  ctx.fillStyle = lit ? '#ffd98a' : '#57e0d6';
  ctx.fillRect(P(wx + 5), P(wy + 6), P(14), P(11));
  ctx.fillRect(P(wx + 24), P(wy + 6), P(14), P(11));
  ctx.strokeStyle = 'rgba(20,32,44,.75)';
  ctx.lineWidth = Math.max(1, P(1));
  ctx.strokeRect(P(wx + 5), P(wy + 6), P(14), P(11));
  ctx.strokeRect(P(wx + 24), P(wy + 6), P(14), P(11));

  // Door
  ctx.fillStyle = '#c9d6e2';
  ctx.fillRect(P(wx + 6), P(wy + 21), P(11), P(13));
  ctx.fillStyle = '#16222e';
  ctx.beginPath(); ctx.arc(P(wx + 15), P(wy + 28), P(1.2), 0, 6.283); ctx.fill();

}

/* ── the mast ───────────────────────────────────────────────────────────────
   Bare pole to begin with. Every Rod level sends it higher, so how deep she
   fishes is legible from the silhouette alone, before you read a single number.
   Everything that hangs off it — spools, outriggers, lanterns, the masthead
   light — is bought, and is placed against mastTop() rather than against magic
   constants, so nothing is ever left hanging in mid-air on a stubby mast. */
const MAST_FOOT = -4;

function mastTop() { return -34 - levelOf('rod') * 5.8; }

function drawMast() {
  const top = mastTop(), foot = railY(MAST_FOOT);
  ctx.fillStyle = '#6b4a2c';
  ctx.fillRect(P(-6), P(top), P(5), P(foot - top));

  // Stays, made fast at the rail fore and aft.
  ctx.strokeStyle = 'rgba(220,232,240,.5)';
  ctx.lineWidth = Math.max(1, P(1));
  ctx.beginPath();
  ctx.moveTo(P(MAST_FOOT), P(top + 2)); ctx.lineTo(P(-HULL_L + 12), P(railY(-HULL_L + 12)));
  ctx.moveTo(P(MAST_FOOT), P(top + 2)); ctx.lineTo(P(HULL_L - 18), P(railY(HULL_L - 18)));
  ctx.stroke();
}

/* ── what the money buys ────────────────────────────────────────────────────
   She sails as a bare hull, a wheelhouse, a rail and a stub mast. Every track in
   the research tree bolts its own piece of gear onto her, so the boat *is* the
   save file: boxes aft for the Fishmonger, spools up the mast for the Line, a
   stack and a power block on the roof for Reel Power. If you can see it, you
   bought it — nothing here is decorative.

   Deck space is tight and the deckhands stand at ROD_SLOTS (58, -34, 88, -66),
   so gear is parked in the four strips between them and grounded on railY() at
   its own x. That is why nothing floats and nobody stands inside a crate. */
function drawUpgradeGear() {
  const net = levelOf('net'), rod = levelOf('rod'), bait = levelOf('bait');
  const trader = levelOf('trader'), line = levelOf('line'), reel = levelOf('reel');
  const crew = levelOf('crew'), power = levelOf('power');
  const top = mastTop();

  const box = (x, yb, w, h, col) => {
    ctx.fillStyle = col;
    ctx.fillRect(P(x), P(yb - h), P(w), P(h));
    ctx.strokeStyle = 'rgba(10,20,30,.5)';
    ctx.lineWidth = Math.max(1, P(1));
    ctx.strokeRect(P(x), P(yb - h), P(w), P(h));
  };

  /* ── Fishmonger: the catch stacked in fish boxes, aft ──
     Three boxes at most, in alternating colours and nudged off each other's
     edges. Four identical ones squared up in a 12px strip stopped reading as a
     stack of boxes at all and turned into a ladder. */
  if (trader > 0) {
    const cols = ['#3f8ea8', '#c9954a', '#4fa2bd'];
    const jog = [0, -1.6, 0.9];
    const base = railY(-50);
    const boxes = trader >= 7 ? 3 : trader >= 4 ? 2 : 1;
    for (let i = 0; i < boxes; i++) box(-56 + jog[i], base - i * 7, 12, 7, cols[i]);

    // Once he is weighing rather than guessing, a gallows over the boxes with a
    // scale swinging under it. The legs stand just outside the boxes but still
    // clear of both deckhands (their bodies span slot ±6, at -34 and -66).
    if (trader >= 4) {
      const armY = base - 34;
      ctx.strokeStyle = '#8fa0ae';
      ctx.lineWidth = Math.max(1, P(1.6));
      ctx.beginPath();
      ctx.moveTo(P(-57), P(base)); ctx.lineTo(P(-57), P(armY));
      ctx.lineTo(P(-44), P(armY)); ctx.lineTo(P(-44), P(base));
      ctx.stroke();
      ctx.beginPath();
      ctx.moveTo(P(-50.5), P(armY)); ctx.lineTo(P(-50.5), P(armY + 4));
      ctx.stroke();
      ctx.fillStyle = '#cfd8e0';
      ctx.beginPath(); ctx.arc(P(-50.5), P(armY + 6), P(2.8), 0, 6.283); ctx.fill();
    }
  }

  /* ── Bait: barrels of it, and a cutting board once there is enough to prep ── */
  const barrel = (x, yb, h) => {
    ctx.fillStyle = '#8a6236';
    ctx.fillRect(P(x), P(yb - h), P(11), P(h));
    ctx.strokeStyle = '#5d4122';
    ctx.lineWidth = Math.max(1, P(1.3));
    for (const hy of [yb - h * 0.7, yb - h * 0.3]) {
      ctx.beginPath(); ctx.moveTo(P(x), P(hy)); ctx.lineTo(P(x + 11), P(hy)); ctx.stroke();
    }
  };
  if (bait > 0) barrel(-24, railY(-19), 13);
  if (bait >= 5) barrel(68, railY(73), 12);
  if (bait >= 9) barrel(-24, railY(-19) - 13, 10);

  /* ── Reel: the trawl winch at the foot of the mast, drum fattening with it ── */
  if (reel > 0) {
    const wb = railY(-12), r = 4 + Math.min(reel, 12) * 0.28;
    ctx.fillStyle = '#55636f';
    ctx.fillRect(P(-12), P(wb - 12), P(10), P(12));
    ctx.fillStyle = '#7d8b97';
    ctx.beginPath(); ctx.arc(P(-7), P(wb - 12), P(r), 0, 6.283); ctx.fill();
    ctx.strokeStyle = '#3c4854';
    ctx.lineWidth = Math.max(1, P(1));
    ctx.beginPath(); ctx.arc(P(-7), P(wb - 12), P(r), 0, 6.283); ctx.stroke();
    // Wire wound on the drum, turning while she works.
    ctx.strokeStyle = 'rgba(220,232,240,.45)';
    ctx.beginPath();
    ctx.arc(P(-7), P(wb - 12), P(r * 0.55), game.t * 1.4, game.t * 1.4 + 4.2);
    ctx.stroke();
  }

  /* ── Line: spare spools racked up the mast, out of the deck's way ── */
  for (let i = 0; i < Math.min(4, Math.ceil(line / 3)); i++) {
    const y = -32 - i * 8;
    if (y < top + 6) break;                 // the pole is not tall enough yet
    ctx.fillStyle = '#2b3a46';
    ctx.beginPath(); ctx.arc(P(-13), P(y), P(3.4), 0, 6.283); ctx.fill();
    ctx.strokeStyle = '#c9d6e2';
    ctx.lineWidth = Math.max(1, P(1));
    ctx.beginPath(); ctx.arc(P(-13), P(y), P(3.4), 0, 6.283); ctx.stroke();
    ctx.beginPath(); ctx.arc(P(-13), P(y), P(1.4), 0, 6.283); ctx.stroke();
  }

  /* ── Rod: electronics to find the deep water, and lights for the top of it ── */
  if (rod >= 3) {
    // Radar scanner on the wheelhouse roof (roof top sits at y = -48).
    ctx.strokeStyle = '#8fa0ae';
    ctx.lineWidth = Math.max(1, P(1.4));
    ctx.beginPath(); ctx.moveTo(P(12), P(-48)); ctx.lineTo(P(12), P(-57)); ctx.stroke();
    ctx.save();
    ctx.translate(P(12), P(-57));
    ctx.scale(Math.cos(game.t * 2.2), 1);
    ctx.fillStyle = '#dfe8f0';
    ctx.fillRect(P(-7), P(-2), P(14), P(3));
    ctx.restore();
  }
  if (rod >= 5) {
    ctx.fillStyle = '#8fe6ff';
    ctx.beginPath(); ctx.arc(P(-3.5), P(top - 2), P(2.2), 0, 6.283); ctx.fill();
    const flap = Math.sin(game.t * 5) * 3;
    ctx.fillStyle = '#ff6b6b';
    ctx.beginPath();
    ctx.moveTo(P(-1), P(top + 2));
    ctx.lineTo(P(15), P(top + 5 + flap));
    ctx.lineTo(P(-1), P(top + 10));
    ctx.closePath();
    ctx.fill();
  }

  /* ── Reel Power: the engine that drives it — stack, smoke, hydraulic block ── */
  if (power > 0) {
    ctx.fillStyle = '#3a4753';
    ctx.fillRect(P(39), P(-59), P(7), P(11));
    ctx.fillStyle = '#22303c';
    ctx.fillRect(P(38), P(-61), P(9), P(3));
    // More power, more smoke: the plume thickens and a second one joins at L6.
    const puffs = 4 + Math.min(power, 8);
    for (let i = 0; i < puffs; i++) {
      const life = ((game.t * 0.55 + i / puffs) % 1);
      const px = 43 + life * 26 + Math.sin(life * 6 + i) * 3;
      const py = -63 - life * 30;
      ctx.fillStyle = `rgba(190,200,210,${(1 - life) * (0.22 + power * 0.02)})`;
      ctx.beginPath();
      ctx.arc(P(px), P(py), P(3 + life * 8), 0, 6.283);
      ctx.fill();
    }
  }
  if (power >= 3) {
    const h = power >= 6 ? 12 : 9;
    box(22, -48, 12, h, '#4a5a67');
    ctx.fillStyle = '#ffd166';                       // running lamp on the block
    ctx.beginPath(); ctx.arc(P(28), P(-48 - h + 3), P(1.4), 0, 6.283); ctx.fill();
  }

  /* ── Deckhands: a life ring on the cabin for each hand who came aboard ──
     Spaced 14 apart: at 9 the strokes touched and two rings read as one "oo",
     and the aft one has to stay clear of the door at x 10..21. */
  for (let i = 0; i < crew && i < 2; i++) {
    ctx.strokeStyle = '#ff7a59';
    ctx.lineWidth = P(2.4);
    ctx.beginPath(); ctx.arc(P(41 - i * 14), P(-16), P(4.4), 0, 6.283); ctx.stroke();
  }
  if (crew >= 3) {                                    // liferaft canister on the roof
    box(0, -48, 9, 6, '#d8dde2');
    ctx.fillStyle = '#c04a3a';
    ctx.fillRect(P(0), P(-51), P(9), P(1.6));
  }

  /* ── Net: outriggers and the lamps that hang off them ── */
  if (net >= 2) {
    const oy = top + 8;
    ctx.strokeStyle = '#6b4a2c';
    ctx.lineWidth = P(2.6);
    ctx.beginPath();
    ctx.moveTo(P(MAST_FOOT), P(oy)); ctx.lineTo(P(-72), P(oy * 0.40));
    ctx.moveTo(P(MAST_FOOT), P(oy)); ctx.lineTo(P(64), P(oy * 0.45));
    ctx.stroke();
    // Lanterns swung from the middle of each pole — they swing harder in a blow.
    for (const [lx, ly] of [[-38, oy * 0.70], [30, oy * 0.72]]) {
      const swing = Math.sin(game.t * 1.6 + lx) * (1 + weather.storm * 3);
      ctx.strokeStyle = 'rgba(200,215,228,.6)';
      ctx.lineWidth = Math.max(1, P(1));
      ctx.beginPath(); ctx.moveTo(P(lx), P(ly)); ctx.lineTo(P(lx + swing), P(ly + 9)); ctx.stroke();
      ctx.fillStyle = '#ffd166';
      ctx.beginPath(); ctx.arc(P(lx + swing), P(ly + 12), P(3.2), 0, 6.283); ctx.fill();
      ctx.fillStyle = 'rgba(255,209,102,.18)';
      ctx.beginPath(); ctx.arc(P(lx + swing), P(ly + 12), P(9), 0, 6.283); ctx.fill();
    }
  }
}

/* ── the net ────────────────────────────────────────────────────────────────
   The first thing anyone buys, and the only upgrade that works where you can
   watch it: the drum sits on the stern and the net itself streams astern in the
   water, wider and deeper at every level. It is drawn clear of the transom
   (x < -HULL_L) so it never fights the hull for the same pixels, and it lifts
   for a beat each time game.netT resets — that is a catch coming aboard. */
function drawNet() {
  const lvl = levelOf('net');
  if (lvl <= 0) return;

  const deck = railY(-83);
  // The drum: end plate, roller, and the wound net on it.
  ctx.fillStyle = '#55636f';
  ctx.fillRect(P(-90), P(deck - 4), P(14), P(4));
  ctx.fillStyle = '#6d7c88';
  ctx.beginPath(); ctx.arc(P(-83), P(deck - 9), P(6), 0, 6.283); ctx.fill();
  ctx.strokeStyle = '#3c4854';
  ctx.lineWidth = Math.max(1, P(1));
  ctx.beginPath(); ctx.arc(P(-83), P(deck - 9), P(6), 0, 6.283); ctx.stroke();
  ctx.strokeStyle = 'rgba(210,225,235,.5)';
  ctx.beginPath(); ctx.arc(P(-83), P(deck - 9), P(3.2), game.t * 0.8, game.t * 0.8 + 4.4); ctx.stroke();

  // Hauling: the whole net rides up for the moment a catch comes over the stern.
  const haul = clamp(1 - game.netT / 0.8, 0, 1) * (game.t > 1 ? 1 : 0);
  const sea = DECK_UP - Math.sin(game.boat.bob) * (1.8 + weather.storm * 2.2);
  const drop = [0, 30, 42, 56][clamp(lvl, 0, 3)] * (1 - haul * 0.45);
  const sway = Math.sin(game.t * 0.9) * 3 * (1 + weather.storm);
  const mouth = sea - haul * 9;                       // top edge, at/near the surface
  const tail = -104 - [0, 26, 40, 52][clamp(lvl, 0, 3)];  // how far astern it streams

  // Warps from the drum down over the transom to the mouth of the net.
  ctx.strokeStyle = 'rgba(220,232,240,.55)';
  ctx.lineWidth = Math.max(1, P(1.1));
  for (const wx of [-98, -101]) {
    ctx.beginPath();
    ctx.moveTo(P(-86), P(deck - 9));
    ctx.quadraticCurveTo(P(-97), P((deck + mouth) / 2), P(wx), P(mouth));
    ctx.stroke();
  }

  // The net: a bag tapering to a cod end, mesh crosshatched inside its outline.
  const x1 = -98, x2 = tail + sway;                   // mouth (fore) → tail (aft)
  const yTop = mouth, yBot = mouth + drop;
  const bagX1 = x1 - 4, bagX2 = x2 + 16;              // cod end is narrower

  ctx.save();
  ctx.beginPath();
  ctx.moveTo(P(x1), P(yTop));
  ctx.lineTo(P(x2), P(yTop + 3));
  ctx.lineTo(P(bagX2), P(yBot));
  ctx.lineTo(P(bagX1), P(yBot * 0.82 + yTop * 0.18));
  ctx.closePath();
  ctx.fillStyle = 'rgba(180,205,220,.10)';
  ctx.fill();
  ctx.clip();

  ctx.strokeStyle = 'rgba(206,226,238,.34)';
  ctx.lineWidth = Math.max(1, P(0.8));
  for (let i = -6; i <= 14; i++) {                    // mesh, both diagonals
    ctx.beginPath();
    ctx.moveTo(P(x1 + i * 9), P(yTop - 6));
    ctx.lineTo(P(x1 + i * 9 - 26), P(yBot + 6));
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(P(x1 + i * 9 - 26), P(yTop - 6));
    ctx.lineTo(P(x1 + i * 9), P(yBot + 6));
    ctx.stroke();
  }
  ctx.restore();

  // Outline, so the bag still reads against dark water.
  ctx.strokeStyle = 'rgba(214,232,244,.5)';
  ctx.lineWidth = Math.max(1, P(1));
  ctx.beginPath();
  ctx.moveTo(P(x1), P(yTop));
  ctx.lineTo(P(x2), P(yTop + 3));
  ctx.lineTo(P(bagX2), P(yBot));
  ctx.lineTo(P(bagX1), P(yBot * 0.82 + yTop * 0.18));
  ctx.closePath();
  ctx.stroke();

  // Cork floats along the head rope — the only part of her gear that shows above
  // the surface, and the tell that the net is actually in the water.
  for (let i = 0; i <= 5; i++) {
    const t = i / 5;
    const fx = lerp(x1, x2, t), fy = lerp(yTop, yTop + 3, t);
    ctx.fillStyle = '#e8955c';
    ctx.beginPath(); ctx.ellipse(P(fx), P(fy - 1), P(2.6), P(2), 0, 0, 6.283); ctx.fill();
  }

  // Something in the cod end on the way up.
  if (haul > 0.25) {
    ctx.fillStyle = `rgba(190,225,235,${haul * 0.8})`;
    ctx.beginPath();
    ctx.ellipse(P((bagX1 + bagX2) / 2), P(yBot - 5), P(7), P(4), 0.2, 0, 6.283);
    ctx.fill();
  }
}

/* ── the deckhands ────────────────────────────────────────────────────────── */

/** Rod angle for a line's current state, in radians (0 = straight out, right).
 *  Fishing is done tip-up: a rod held level reads as a stick poking at the
 *  water, not as a rod, so every working pose keeps the tip well up. */
function rodAngle(L) {
  switch (L.state) {
    // Whip back over the shoulder, then punch through as the hook flies out.
    case 'cast':  return lerp(-1.9, -0.25, clamp(L.t / CAST_TIME, 0, 1) ** 0.65);
    case 'sink':  return -0.35;
    case 'wait':  return -0.55 + Math.sin(game.t * 1.5 + L.i) * 0.025;
    case 'fight': return -0.95 + Math.sin(L.t * 15) * 0.07;
    case 'reel':  return -0.60 + Math.sin(L.t * 9) * 0.10;
    case 'snap':  return -1.35;   // sprung straight up the instant the line goes
    default:      return -0.85;   // re-baiting: rod up, out of the way
  }
}

/** How far the blank is pulled over, in px of mid-rod deflection. A fish on
 *  loads the rod; a waiting rod stays straight. */
function rodBend(L) {
  switch (L.state) {
    case 'fight': return 11 + Math.sin(L.t * 15) * 3;
    case 'reel':  return 5 + Math.sin(L.t * 9) * 1.5;
    case 'snap':  return -4;
    default:      return 0;
  }
}

const ROD_LEN = 56;

/** Where a rod's tip is, in boat-local CSS px. The line hangs from here. */
export function rodTip(L) {
  const slot = ROD_SLOTS[L.i] ?? 0;
  const a = rodAngle(L);
  const hy = railY(slot) - 30;   // hand height — the angler stands on the deck
  return { x: slot + Math.cos(a) * ROD_LEN, y: hy + Math.sin(a) * ROD_LEN, hx: slot, hy };
}

function drawAngler(L) {
  const slot = ROD_SLOTS[L.i] ?? 0;
  const deck = railY(slot);
  const t = rodTip(L);
  const lean = L.state === 'reel' || L.state === 'fight' ? -2 : 0;

  ctx.save();
  ctx.translate(P(slot), P(deck));

  // legs
  ctx.fillStyle = '#2c3e50';
  ctx.fillRect(P(-4), P(-14), P(3.4), P(14));
  ctx.fillRect(P(1), P(-14), P(3.4), P(14));
  // boots
  ctx.fillStyle = '#141d26';
  ctx.fillRect(P(-5), P(-3), P(5), P(3));
  ctx.fillRect(P(0.6), P(-3), P(5), P(3));
  // coat
  ctx.fillStyle = L.i === 0 ? '#e0a83c' : '#3f7fa8';
  ctx.beginPath();
  ctx.moveTo(P(-6 + lean), P(-15));
  ctx.lineTo(P(6 + lean), P(-15));
  ctx.lineTo(P(5 + lean), P(-30));
  ctx.lineTo(P(-5 + lean), P(-30));
  ctx.closePath();
  ctx.fill();
  // head + cap + beard
  ctx.fillStyle = '#e8b98f';
  ctx.beginPath(); ctx.arc(P(lean), P(-35), P(4.6), 0, 6.283); ctx.fill();
  ctx.fillStyle = '#22303c';
  ctx.beginPath();
  ctx.arc(P(lean), P(-36.5), P(4.8), Math.PI, 0);
  ctx.fill();
  ctx.fillRect(P(lean - 5), P(-37), P(11), P(1.8));
  ctx.fillStyle = '#cfd8e0';
  ctx.beginPath(); ctx.arc(P(lean + 1), P(-32.5), P(2.6), 0, Math.PI); ctx.fill();

  // arm reaching to the rod grip (the hand is at local (0, -30))
  ctx.strokeStyle = L.i === 0 ? '#c9922f' : '#356f95';
  ctx.lineWidth = P(2.8);
  ctx.lineCap = 'round';
  ctx.beginPath();
  ctx.moveTo(P(lean), P(-27));
  ctx.lineTo(P(0), P(-30));
  ctx.stroke();

  // ── the rod ──
  const a = rodAngle(L);
  const dx = Math.cos(a), dyv = Math.sin(a);
  const px = -dyv, py = dx;                 // "below" the blank, where a fish pulls
  const tipX = t.x - slot, tipY = t.y - deck;
  const bend = rodBend(L);

  // The bent blank is a quadratic from the grip (0, -30) to the tip, its
  // control point pushed under by the bend. Three chords read as a taper.
  const ctlX = tipX / 2 + px * bend, ctlY = (-30 + tipY) / 2 + py * bend;
  const q = s => {
    const u = 1 - s;
    return {
      x: 2 * u * s * ctlX + s * s * tipX,
      y: u * u * -30 + 2 * u * s * ctlY + s * s * tipY,
    };
  };
  const hand = { x: 0, y: -30 };
  const p1 = q(0.42), p2 = q(0.78), tip = { x: tipX, y: tipY };
  const seg = (pA, pB, w, col) => {
    ctx.strokeStyle = col;
    ctx.lineWidth = P(w);
    ctx.beginPath(); ctx.moveTo(P(pA.x), P(pA.y)); ctx.lineTo(P(pB.x), P(pB.y)); ctx.stroke();
  };

  // Blank: fat and dark at the butt, thin and warm at the tip.
  seg(hand, p1, 3.2, '#3f2f1e');
  seg(p1, p2, 2.2, '#524026');
  seg(p2, tip, 1.3, '#6d5330');

  // Cork grip behind the hands, reel slung under the blank ahead of them.
  seg({ x: -dx * 9, y: -30 - dyv * 9 }, { x: dx * 3, y: -30 + dyv * 3 }, 4.4, '#c9a06a');
  const rx = dx * 7 + px * 4.5, ry = -30 + dyv * 7 + py * 4.5;
  ctx.strokeStyle = '#1d262e';
  ctx.lineWidth = P(1.4);
  ctx.beginPath(); ctx.moveTo(P(dx * 7), P(-30 + dyv * 7)); ctx.lineTo(P(rx), P(ry)); ctx.stroke();
  ctx.fillStyle = '#27333d';
  ctx.beginPath(); ctx.arc(P(rx), P(ry), P(3.1), 0, 6.283); ctx.fill();
  ctx.strokeStyle = '#9fb2c0';
  ctx.lineWidth = Math.max(1, P(1));
  ctx.beginPath(); ctx.arc(P(rx), P(ry), P(1.6), 0, 6.283); ctx.stroke();

  ctx.restore();
}

/* ── lines, hooks and the fish coming up ──────────────────────────────────── */

/** Where each rod's line lands, as a fraction of the clear water to the right of
 *  the boat. Fanned right out: four lines dropping into the same patch of sea
 *  read as one line, and the whole point is seeing where each one goes in. */
const ENTRY_SPREAD = [0.18, 0.40, 0.62, 0.84];

/** Where this rod's line pierces the surface. Anchored to the boat rather than
 *  to the rod tip — hanging it off the tip made the mark slide about every time
 *  the rod changed angle, which is exactly what it must not do. */
function entryX(L, cx) {
  const room = Math.max(80, view().w - cx - 40);
  return cx + 46 + (ENTRY_SPREAD[L.i] ?? 0.5) * (room - 46);
}

function drawLines(v) {
  const cx = bx(), by = deckY();
  for (const L of game.lines) {
    const t = rodTip(L);
    const tipX = cx + t.x, tipY = by + t.y;
    const ex = entryX(L, cx), ey = sy();

    ctx.strokeStyle = L.state === 'snap' ? 'rgba(255,150,130,.85)' : 'rgba(232,244,252,.72)';
    ctx.lineWidth = Math.max(1, P(1.2));

    if (L.state === 'cast') {
      // Still in the air: a straight line out to the flying hook, and the hook
      // itself, which is the only time either is visible.
      const hook = castHookPos(L, cx, by);
      ctx.beginPath();
      ctx.moveTo(P(tipX), P(tipY));
      ctx.lineTo(P(hook.x), P(hook.y));
      ctx.stroke();
      ctx.fillStyle = '#cfd9e3';
      ctx.beginPath();
      ctx.arc(P(hook.x), P(hook.y), P(2.2), 0, 6.283);
      ctx.fill();
    } else {
      // Down: rod tip out to where it pierces the surface, then straight down
      // to the hook. Drawing it as two segments is what makes the entry read.
      const sag = (L.state === 'fight' || L.state === 'reel') ? 1 : 7;
      const fy = ey + floatBob(L);
      ctx.beginPath();
      ctx.moveTo(P(tipX), P(tipY));
      ctx.quadraticCurveTo(
        P((tipX + ex) / 2 + Math.sin(game.t * 1.2 + L.i) * 2), P((tipY + fy) / 2 + sag),
        P(ex), P(fy));
      ctx.stroke();

      // Nothing below the waterline is drawn — no line, no hook, no fish. What
      // is down there is the sea's business. The float is the whole story.
    }

    if (L.state === 'snap') continue;
    if (L.splash >= 0) drawEntryMark(L, ex, ey);
  }
}

/** How far the float sits below the waterline. Swell while it waits, hauled
 *  under in hard jerks while a fish is on. The line is drawn down to this same
 *  point, so the tug pulls float and line together instead of the float dipping
 *  away from a line still pinned to the surface. */
function floatBob(L) {
  let b = Math.sin(game.t * 2.2 + L.i * 1.7) * 1.5;
  if (L.state === 'fight') b += 5.5 + Math.sin(L.t * 21) * 4.5 + Math.sin(L.t * 37) * 1.5;
  else if (L.state === 'reel') b += 2.4 + Math.sin(game.t * 17 + L.i) * 1.4;
  else if (L.state === 'sink') b += 1.2;
  return b;
}

/** The hook mid-flight, lobbed up and over onto the entry mark. Only ever needed
 *  during the cast — the moment it lands it stops being drawn. */
function castHookPos(L, cx, by) {
  const t = rodTip(L);
  const k = clamp(L.t / CAST_TIME, 0, 1);
  return {
    x: lerp(cx + t.x, entryX(L, cx), k),
    y: lerp(by + t.y, sy(), k) - Math.sin(k * Math.PI) * 40,
  };
}

/** The float and the water around it.
 *
 *  With nothing rendered below the surface this is the only thing that can say
 *  what is happening down there, so it does the acting: it rides the swell while
 *  the bait waits, gets yanked under when a fish takes it, and shudders on the
 *  way in. A bite you cannot see is a bite that did not happen.
 */
function drawEntryMark(L, ex, ey) {
  const fresh = L.splash < 1.5 ? 1 - L.splash / 1.5 : 0;
  const bob = floatBob(L);
  const churn = L.state === 'fight' ? 1 : L.state === 'reel' ? 0.55 : 0;

  ctx.save();
  ctx.lineWidth = Math.max(1, P(1.1));
  const rings = 3;
  for (let r = 0; r < rings; r++) {
    const p = ((game.t * (0.85 + churn * 1.5)) + r / rings) % 1;
    const rad = 3 + p * (11 + fresh * 26 + churn * 16);
    ctx.strokeStyle = `rgba(214,242,255,${(0.26 + fresh * 0.5 + churn * 0.28) * (1 - p)})`;
    ctx.beginPath();
    ctx.ellipse(P(ex), P(ey + bob * 0.35), P(rad), P(rad * 0.30), 0, 0, 6.283);
    ctx.stroke();
  }

  // The plume thrown up the instant it hits.
  if (fresh > 0.55) {
    const k = (1 - fresh) / 0.45;
    ctx.fillStyle = `rgba(232,248,255,${fresh})`;
    for (let i = 0; i < 5; i++) {
      const a = -Math.PI * (0.24 + i * 0.13);
      const d = k * 17;
      ctx.beginPath();
      ctx.arc(P(ex + Math.cos(a) * d), P(ey + Math.sin(a) * d * 0.8 + k * k * 11),
        P(1.9 * fresh), 0, 6.283);
      ctx.fill();
    }
  }

  // No float once the line is out of the water — after a fish is landed only
  // the disturbance it left behind is still there.
  if (L.state === 'rest') { ctx.restore(); return; }

  // Float: red over white, the classic bobber. Drawn a size up now that it is
  // the only thing on screen reporting what the line is doing.
  ctx.fillStyle = '#ff5049';
  ctx.beginPath(); ctx.arc(P(ex), P(ey + bob - 3.0), P(4.4), 0, 6.283); ctx.fill();
  ctx.fillStyle = '#f4f8fc';
  ctx.beginPath(); ctx.arc(P(ex), P(ey + bob - 5.2), P(2.2), 0, 6.283); ctx.fill();
  ctx.strokeStyle = 'rgba(20,40,56,.5)';
  ctx.lineWidth = Math.max(1, P(0.9));
  ctx.beginPath(); ctx.arc(P(ex), P(ey + bob - 3.6), P(4.5), 0, 6.283); ctx.stroke();
  ctx.restore();
}

/* ── depth ruler ──────────────────────────────────────────────────────────── */
/** Because depth is compressed, the player needs to see the scale they bought. */
function drawDepthRuler(v) {
  const vd = viewDepth();
  ctx.save();
  ctx.font = `600 ${Math.round(P(8.5))}px "Cascadia Code", Consolas, monospace`;
  ctx.textAlign = 'right';
  for (const z of ZONES) {
    if (z.from <= 0 || z.from > vd) continue;
    const y = dy(z.from);
    ctx.strokeStyle = 'rgba(190,225,245,.16)';
    ctx.lineWidth = Math.max(1, P(1));
    ctx.setLineDash([P(5), P(6)]);
    ctx.beginPath(); ctx.moveTo(0, P(y)); ctx.lineTo(cw, P(y)); ctx.stroke();
    ctx.setLineDash([]);
    ctx.fillStyle = 'rgba(200,232,248,.4)';
    ctx.fillText(`${z.name}  ${z.from.toLocaleString('en-US')}m`, cw - P(8), P(y - 4));
  }
  // No marker for the hook's own depth any more: it would be pointing at
  // something deliberately not drawn. The HUD carries the exact number.
  ctx.restore();
}

/* ── floating labels ──────────────────────────────────────────────────────── */
function drawPops(v) {
  const cx = bx(), by = deckY();
  ctx.save();
  ctx.textAlign = 'center';
  for (const p of game.pops) {
    const k = p.t / 2.2;
    const a = k < 0.12 ? k / 0.12 : 1 - Math.max(0, (k - 0.55) / 0.45);
    if (a <= 0) continue;
    const slot = ROD_SLOTS[p.slot] ?? 0;
    // Each rod gets its own lane. Four deckhands landing at once used to stack
    // every label on the same line and render as unreadable mush.
    const lane = (p.slot ?? 0) * 15;
    ctx.globalAlpha = clamp(a, 0, 1);
    ctx.font = `700 ${Math.round(P(11))}px "Cascadia Code", Consolas, monospace`;
    ctx.fillStyle = p.bad ? '#ff9a86' : p.col;
    ctx.strokeStyle = 'rgba(4,14,22,.75)';
    ctx.lineWidth = P(2.6);
    ctx.strokeText(p.text, P(cx + slot), P(by - 44 - lane - k * 42));
    ctx.fillText(p.text, P(cx + slot), P(by - 44 - lane - k * 42));
  }
  ctx.restore();
  ctx.globalAlpha = 1;
}

/* ── weather overlays (unchanged behaviour) ───────────────────────────────── */
function drawRain(v) {
  if (weather.storm < 0.28 || !weather.drops.length) return;
  ctx.save();
  ctx.strokeStyle = `rgba(200,220,240,${0.25 + weather.storm * 0.35})`;
  ctx.lineWidth = Math.max(1, P(1.2));
  const len = P(10 + weather.storm * 10);
  const ang = 0.25 + weather.storm * 0.15;
  const horizon = P(sy());
  for (const d of weather.drops) {
    const x = d.x * cw, y = d.y * ch;
    if (y > horizon + P(40)) continue;
    ctx.beginPath();
    ctx.moveTo(x, y);
    ctx.lineTo(x + Math.sin(ang) * len * d.s, y + Math.cos(ang) * len * d.s);
    ctx.stroke();
  }
  ctx.restore();
}

function drawLightning(v) {
  if (!weather.bolts.length) return;
  const horizon = Math.max(1, P(sy()));
  for (const bolt of weather.bolts) {
    const a = clamp(bolt.life / 0.15, 0, 1) * bolt.power;
    ctx.save();
    ctx.strokeStyle = `rgba(230,245,255,${0.85 * a})`;
    ctx.lineWidth = P(2.5);
    ctx.shadowColor = 'rgba(180,220,255,.9)';
    ctx.shadowBlur = P(12);
    ctx.beginPath();
    let started = false, last = null;
    for (const seg of bolt.segs) {
      const x = seg.x * cw, y = seg.y * horizon;
      // A branch forks off the trunk, then the pen goes back so the next trunk
      // segment does not draw a stray line from the branch tip.
      if (seg.branch && last) { ctx.moveTo(last.x, last.y); ctx.lineTo(x, y); ctx.moveTo(last.x, last.y); }
      else if (!started) { ctx.moveTo(x, y); started = true; last = { x, y }; }
      else { ctx.lineTo(x, y); last = { x, y }; }
    }
    ctx.stroke();
    ctx.restore();
  }
}

function drawFlash() {
  if (weather.flash <= 0) return;
  ctx.fillStyle = `rgba(230,240,255,${weather.flash * 0.45})`;
  ctx.fillRect(0, 0, cw, ch);
}
