// Soft synthesised SFX via Web Audio. No sample files — short beeps and noise
// bursts that stay quiet and throttled so a full board of Pulse towers does not
// turn into a machine-gun headache.

let ctx = null;
let master = null;
let enabled = true;
let unlocked = false;

/** Cap simultaneous fire chirps so 40 towers don't stack into white noise. */
const fireBuckets = new Map(); // id -> last time
const FIRE_GAP = {
  pulse: 0.055, flak: 0.12, cryo: 0.4, cannon: 0.18, venom: 0.14,
  tesla: 0.16, laser: 0.08, missile: 0.22, rail: 0.28, mortar: 0.32,
  amp: 0.5, flame: 0.09, sniper: 0.35, nova: 0.28, gatling: 0.04,
  singularity: 0.18, oblivion: 0.38, tempest: 0.22,
  default: 0.1,
};

function ensure() {
  if (ctx) return ctx;
  const AC = window.AudioContext || window.webkitAudioContext;
  if (!AC) return null;
  ctx = new AC();
  master = ctx.createGain();
  master.gain.value = 0.13;         // overall quiet
  master.connect(ctx.destination);
  return ctx;
}

/** Call from any user gesture so autoplay policies let us make noise. */
export function unlockAudio() {
  const c = ensure();
  if (!c) return;
  if (c.state === 'suspended') c.resume().catch(() => {});
  unlocked = true;
}

export function setMuted(m) {
  enabled = !m;
  if (master) master.gain.value = enabled ? 0.13 : 0;
}

export function isMuted() { return !enabled; }

function now() {
  const c = ensure();
  return c ? c.currentTime : 0;
}

function envGain(peak, attack, hold, release) {
  const c = ensure();
  if (!c || !enabled) return null;
  const g = c.createGain();
  const t = c.currentTime;
  g.gain.setValueAtTime(0.0001, t);
  g.gain.exponentialRampToValueAtTime(Math.max(0.0002, peak), t + attack);
  g.gain.exponentialRampToValueAtTime(Math.max(0.0002, peak * 0.7), t + attack + hold);
  g.gain.exponentialRampToValueAtTime(0.0001, t + attack + hold + release);
  g.connect(master);
  return { g, t, c };
}

function tone(freq, peak, attack, hold, release, type = 'sine') {
  const e = envGain(peak, attack, hold, release);
  if (!e) return;
  const o = e.c.createOscillator();
  o.type = type;
  o.frequency.setValueAtTime(freq, e.t);
  o.connect(e.g);
  o.start(e.t);
  o.stop(e.t + attack + hold + release + 0.02);
}

function noiseBurst(peak, attack, hold, release, hpFreq = 800) {
  const e = envGain(peak, attack, hold, release);
  if (!e) return;
  const n = 0.08 * e.c.sampleRate | 0;
  const buf = e.c.createBuffer(1, n, e.c.sampleRate);
  const data = buf.getChannelData(0);
  for (let i = 0; i < n; i++) data[i] = (Math.random() * 2 - 1) * (1 - i / n);
  const src = e.c.createBufferSource();
  src.buffer = buf;
  const filt = e.c.createBiquadFilter();
  filt.type = 'highpass';
  filt.frequency.value = hpFreq;
  src.connect(filt);
  filt.connect(e.g);
  src.start(e.t);
}

function canFire(id) {
  const t = performance.now() / 1000;
  const gap = FIRE_GAP[id] || FIRE_GAP.default;
  const last = fireBuckets.get(id) || 0;
  if (t - last < gap) return false;
  fireBuckets.set(id, t);
  return true;
}

/** Distinct soft voice per turret type. */
export function sfxFire(id) {
  if (!enabled || !unlocked) return;
  if (!canFire(id)) return;
  ensure();

  switch (id) {
    case 'pulse':
      tone(880 + Math.random() * 40, 0.04, 0.005, 0.02, 0.05, 'triangle');
      break;
    case 'flak':
      noiseBurst(0.05, 0.002, 0.02, 0.06, 1200);
      tone(220, 0.03, 0.002, 0.01, 0.05, 'square');
      break;
    case 'cryo':
      tone(520, 0.025, 0.02, 0.08, 0.18, 'sine');
      tone(780, 0.015, 0.03, 0.1, 0.2, 'sine');
      break;
    case 'cannon':
      noiseBurst(0.06, 0.002, 0.04, 0.1, 200);
      tone(90, 0.07, 0.005, 0.04, 0.12, 'sine');
      break;
    case 'venom':
      tone(310, 0.035, 0.01, 0.04, 0.1, 'sawtooth');
      tone(470, 0.02, 0.02, 0.05, 0.12, 'sine');
      break;
    case 'tesla':
      noiseBurst(0.045, 0.001, 0.015, 0.05, 2500);
      tone(1400 + Math.random() * 200, 0.03, 0.002, 0.02, 0.06, 'square');
      break;
    case 'laser':
      tone(1100, 0.02, 0.01, 0.04, 0.05, 'sine');
      break;
    case 'missile':
      tone(180, 0.04, 0.01, 0.05, 0.15, 'sawtooth');
      noiseBurst(0.035, 0.01, 0.06, 0.12, 400);
      break;
    case 'rail':
      tone(160, 0.05, 0.002, 0.02, 0.08, 'square');
      tone(2100, 0.035, 0.002, 0.03, 0.1, 'sine');
      break;
    case 'mortar':
      tone(70, 0.08, 0.008, 0.06, 0.18, 'sine');
      noiseBurst(0.05, 0.005, 0.05, 0.15, 150);
      break;
    case 'amp':
      tone(440, 0.02, 0.02, 0.1, 0.2, 'triangle');
      tone(660, 0.015, 0.03, 0.12, 0.22, 'sine');
      break;
    case 'flame':
      noiseBurst(0.04, 0.01, 0.05, 0.08, 600);
      tone(140 + Math.random() * 30, 0.025, 0.01, 0.04, 0.08, 'sawtooth');
      break;
    case 'sniper':
      tone(1900, 0.04, 0.002, 0.02, 0.08, 'triangle');
      tone(420, 0.03, 0.002, 0.03, 0.1, 'sine');
      break;
    case 'nova':
      tone(260, 0.045, 0.01, 0.05, 0.2, 'sine');
      tone(520, 0.03, 0.02, 0.08, 0.25, 'triangle');
      break;
    case 'gatling':
      tone(700 + Math.random() * 80, 0.025, 0.002, 0.01, 0.03, 'square');
      break;
    case 'singularity':
      tone(70, 0.05, 0.02, 0.08, 0.2, 'sine');
      tone(140, 0.03, 0.03, 0.1, 0.22, 'triangle');
      break;
    case 'oblivion':
      tone(90, 0.07, 0.005, 0.04, 0.15, 'sawtooth');
      tone(1800, 0.04, 0.002, 0.03, 0.1, 'sine');
      noiseBurst(0.05, 0.002, 0.04, 0.12, 400);
      break;
    case 'tempest':
      noiseBurst(0.05, 0.001, 0.02, 0.08, 1800);
      tone(900 + Math.random() * 400, 0.035, 0.002, 0.03, 0.08, 'square');
      break;
    default:
      tone(600, 0.03, 0.005, 0.03, 0.06, 'sine');
  }
}

export function sfxPlace(colorHint) {
  if (!enabled || !unlocked) return;
  ensure();
  tone(320, 0.04, 0.01, 0.04, 0.1, 'triangle');
  tone(480, 0.03, 0.02, 0.05, 0.12, 'sine');
}

export function sfxUpgrade() {
  if (!enabled || !unlocked) return;
  ensure();
  tone(520, 0.04, 0.01, 0.04, 0.08, 'triangle');
  tone(780, 0.035, 0.02, 0.05, 0.12, 'sine');
  tone(1040, 0.03, 0.03, 0.06, 0.14, 'sine');
}

export function sfxSell() {
  if (!enabled || !unlocked) return;
  ensure();
  tone(400, 0.035, 0.01, 0.04, 0.1, 'triangle');
  tone(240, 0.03, 0.03, 0.05, 0.12, 'sine');
}

export function sfxLeak() {
  if (!enabled || !unlocked) return;
  ensure();
  tone(180, 0.06, 0.01, 0.08, 0.25, 'sawtooth');
  tone(90, 0.05, 0.02, 0.1, 0.3, 'sine');
}

export function sfxWave() {
  if (!enabled || !unlocked) return;
  ensure();
  tone(360, 0.03, 0.02, 0.08, 0.2, 'sine');
  tone(540, 0.025, 0.05, 0.1, 0.25, 'triangle');
}

export function sfxUi() {
  if (!enabled || !unlocked) return;
  ensure();
  tone(700, 0.02, 0.005, 0.02, 0.05, 'sine');
}
