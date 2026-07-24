// weather.js — clear skies roll into squalls and full thunderstorms.
// Intensity drives sky tint, rain density, wave height, and thunder SFX.

import { rand, clamp } from './core.js';
import { sfxThunder, sfxGull } from './audio.js';

/** Live weather state the renderer and audio both read. */
export const weather = {
  /** 0 clear → ~0.35 overcast → 0.7 rain → 1.0 thunderstorm */
  storm: 0,
  target: 0,
  /** Seconds until the next weather target is rolled. */
  nextChange: 18,
  /** Screen flash 1 → 0 after a bolt. */
  flash: 0,
  /** Pending thunder delay after a lightning flash (seconds). */
  thunderIn: 0,
  thunderPower: 0,
  /** Rain drop particles (screen-space-ish, regenerated each frame). */
  drops: [],
  bolts: [], // short-lived {x, life, branches}
  t: 0,

  /** The honey sun-break: 0 normal daylight → 1 full gold. Rain clearing is the
   *  only thing that starts one, and it does not start one every time. */
  sun: 0,
  sunTarget: 0,
  sunFor: 0,      // seconds left in the current break
  wasWet: false,  // was it raining on the previous tick?
  gullIn: 25,     // seconds until the next gull call
};

export function resetWeather() {
  weather.storm = 0;
  // First voyage often opens fair, then a real weather beat within ~20s so
  // thunderstorms are not a myth the player never lives long enough to see.
  weather.target = rand(0.15);
  weather.nextChange = 8 + rand(14);
  weather.flash = 0;
  weather.thunderIn = 0;
  weather.thunderPower = 0;
  weather.drops = [];
  weather.bolts = [];
  weather.t = 0;
  weather.sun = 0;
  weather.sunTarget = 0;
  weather.sunFor = 0;
  weather.wasWet = false;
  weather.gullIn = 25;
  for (let i = 0; i < 40; i++) {
    weather.drops.push({ x: Math.random(), y: Math.random(), s: 0.6 + Math.random() * 0.8 });
  }
}

/**
 * Advance weather. Call only while playing.
 * @param {number} dt
 * @param {{w:number,h:number}} view  logical CSS pixels of the canvas
 */
export function updateWeather(dt, view) {
  weather.t += dt;
  weather.nextChange -= dt;

  // Roll a new weather target every so often. Bias toward mild weather, but
  // periodically throw real thunderstorms so the ocean is not always calm.
  if (weather.nextChange <= 0) {
    const roll = Math.random();
    // Guarantee a thunderstorm on the first weather roll of a long session so
    // ambiance (rain + thunder samples) actually plays for everyone.
    if (weather.t < 45 && weather.storm < 0.4 && roll < 0.55) {
      weather.target = rand(1.0, 0.85);
    } else if (roll < 0.35) weather.target = rand(0.12);           // clear / fair
    else if (roll < 0.62) weather.target = rand(0.45, 0.18); // overcast
    else if (roll < 0.84) weather.target = rand(0.78, 0.5);  // rain
    else weather.target = rand(1.0, 0.82);                   // thunderstorm
    weather.nextChange = 14 + rand(22);
  }

  // Ease toward the target (storms build and break over ~20–40s).
  const k = 1 - Math.pow(0.25, dt);
  weather.storm = weather.storm + (weather.target - weather.storm) * k * 0.35;

  /* ── the honey sun-break ────────────────────────────────────────────────
     When rain actually stops the sky sometimes opens up gold for a while —
     sometimes, not every time, and a fresh squall shuts it straight back down. */
  const wet = weather.storm > 0.3;
  if (weather.wasWet && !wet && weather.sunFor <= 0 && Math.random() < 0.55) {
    weather.sunFor = 16 + rand(20);
  }
  weather.wasWet = wet;

  if (weather.sunFor > 0) {
    weather.sunFor -= dt;
    weather.sunTarget = wet ? 0 : 1;      // clouding over again ends it early
    if (weather.sunFor <= 0) { weather.sunFor = 0; weather.sunTarget = 0; }
  } else {
    weather.sunTarget = 0;
  }
  weather.sun += (weather.sunTarget - weather.sun) * (1 - Math.pow(0.3, dt)) * 0.6;

  // Gulls: an occasional cry off in the distance, not a seabird colony. Long
  // gaps on purpose — anything more often than this grates within a minute.
  weather.gullIn -= dt;
  if (weather.gullIn <= 0) {
    if (weather.storm < 0.45) sfxGull(0.3 + weather.sun * 0.35 + Math.random() * 0.2);
    weather.gullIn = weather.storm > 0.45 ? 40 + rand(40)
      : (weather.sun > 0.4 ? 20 + rand(28) : 34 + rand(46));
  }

  // Lightning only in heavy weather.
  weather.flash = Math.max(0, weather.flash - dt * 2.8);
  if (weather.thunderIn > 0) {
    weather.thunderIn -= dt;
    if (weather.thunderIn <= 0) {
      sfxThunder(weather.thunderPower);
      weather.thunderIn = 0;
    }
  }

  if (weather.storm > 0.62 && Math.random() < dt * (0.08 + weather.storm * 0.22)) {
    strike(view);
  }

  // Rain particles — density scales with storm.
  const want = weather.storm > 0.28
    ? Math.floor(40 + weather.storm * 220)
    : 0;
  while (weather.drops.length < want) {
    weather.drops.push({
      x: Math.random(),
      y: Math.random(),
      s: 0.55 + Math.random() * 1.1,
    });
  }
  if (weather.drops.length > want) weather.drops.length = want;

  const fall = (420 + weather.storm * 380) * dt;
  const drift = (40 + weather.storm * 90) * dt;
  for (const d of weather.drops) {
    d.y += (fall * d.s) / Math.max(1, view.h);
    d.x += drift / Math.max(1, view.w);
    if (d.y > 1.05) { d.y = -0.05; d.x = Math.random(); }
    if (d.x > 1.05) d.x -= 1.1;
  }

  for (let i = weather.bolts.length - 1; i >= 0; i--) {
    weather.bolts[i].life -= dt;
    if (weather.bolts[i].life <= 0) weather.bolts.splice(i, 1);
  }
}

function strike(view) {
  const power = clamp(0.55 + weather.storm * 0.5 + Math.random() * 0.2, 0, 1);
  weather.flash = Math.max(weather.flash, 0.55 + power * 0.55);
  // Thunder lags behind the flash — distance cue.
  weather.thunderIn = 0.25 + Math.random() * 1.4 * (1.15 - weather.storm * 0.4);
  weather.thunderPower = power;

  // Jagged bolt in the sky band (screen-normalized x, y 0 at top).
  const x0 = 0.08 + Math.random() * 0.84;
  const segs = [];
  let x = x0, y = 0.02;
  const steps = 8 + (Math.random() * 6 | 0);
  for (let i = 0; i < steps; i++) {
    x += (Math.random() - 0.5) * 0.06;
    y += 0.04 + Math.random() * 0.05;
    segs.push({ x, y });
    // occasional branch
    if (Math.random() < 0.35) {
      segs.push({
        x: x + (Math.random() - 0.5) * 0.08,
        y: y + 0.03 + Math.random() * 0.04,
        branch: true,
        from: segs.length - 1,
      });
    }
  }
  weather.bolts.push({ segs, life: 0.12 + Math.random() * 0.1, power });
}

/** Wave amplitude multiplier for surface drawing (1 calm → ~2.2 storm). */
export function waveAmp() {
  return 1 + weather.storm * 1.2;
}

/** Sky gradient stops for the current storm and sun-break. */
export function skyColors() {
  const s = weather.storm;
  // Clear teal → bruised purple-grey storm.
  let top = mix3(hex('#123a52'), hex('#1a1f2e'), clamp(s * 1.1, 0, 1));
  let bottom = mix3(hex('#3d7f9c'), hex('#3a4558'), clamp(s * 1.05, 0, 1));

  // Then bend the whole thing toward honey while the sun is out: warm bronze
  // overhead falling to gold on the horizon.
  const g = clamp(weather.sun, 0, 1);
  if (g > 0.002) {
    top = mix3(top, hex('#7a4a52'), g * 0.66);
    bottom = mix3(bottom, hex('#ffbe62'), g * 0.9);
  }
  return { top: rgbOf(top), bottom: rgbOf(bottom) };
}

/** Warm, low sunlight tint for the sea surface during a break. 0 when grey. */
export function sunTint() { return clamp(weather.sun, 0, 1); }

const hex = h => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
const mix3 = (a, b, t) => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
const rgbOf = c => `rgb(${Math.round(c[0])},${Math.round(c[1])},${Math.round(c[2])})`;
