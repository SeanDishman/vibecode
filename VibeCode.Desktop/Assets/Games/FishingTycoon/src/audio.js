// audio.js — custom WAV ambiance + SFX for Fishing Tycoon.
// Samples live under ./audio/*.wav (embedded with the game). Falls back to
// synthesised tones if a file fails to load so the game still has sound.

let ctx = null;
let master = null;
let unlocked = false;
let enabled = true;
const buffers = Object.create(null);
const loops = Object.create(null); // name -> { src, gain }

const SAMPLE_URLS = {
  ocean: new URL('../audio/ocean.wav', import.meta.url).href,
  rain: new URL('../audio/rain.wav', import.meta.url).href,
  thunder: new URL('../audio/thunder.wav', import.meta.url).href,
  motor: new URL('../audio/motor.wav', import.meta.url).href,
  splash: new URL('../audio/splash.wav', import.meta.url).href,
  sell: new URL('../audio/sell.wav', import.meta.url).href,
  gull: new URL('../audio/gull.wav', import.meta.url).href,
};

function ensure() {
  if (ctx) return ctx;
  const AC = window.AudioContext || window.webkitAudioContext;
  if (!AC) return null;
  ctx = new AC();
  master = ctx.createGain();
  master.gain.value = 0.42;
  master.connect(ctx.destination);
  return ctx;
}

async function loadBuffer(name, url) {
  const c = ensure();
  if (!c) return null;
  try {
    const res = await fetch(url);
    if (!res.ok) throw new Error(res.status + ' ' + url);
    const raw = await res.arrayBuffer();
    const buf = await c.decodeAudioData(raw.slice(0));
    buffers[name] = buf;
    return buf;
  } catch (e) {
    console.warn('[FT audio] failed to load', name, e);
    return null;
  }
}

/** Kick off sample load (safe to call early; plays after unlock). */
export function preloadAudio() {
  ensure();
  return Promise.all(Object.entries(SAMPLE_URLS).map(([n, u]) => loadBuffer(n, u)));
}

/** Must be called from a user gesture (Cast off) for autoplay policy. */
export function unlockAudio() {
  const c = ensure();
  if (!c) return;
  if (c.state === 'suspended') c.resume().catch(() => {});
  unlocked = true;
  // Start ocean as soon as the player commits to a voyage.
  setAmbience(true, 0);
  setMotor(0);
}

export function setMuted(m) {
  enabled = !m;
  if (master) master.gain.value = enabled ? 0.42 : 0;
}

export function isMuted() { return !enabled; }

function playOnce(name, vol = 0.5, rate = 1) {
  if (!enabled || !unlocked) return;
  const c = ensure();
  if (!c) return;
  const buf = buffers[name];
  if (!buf) {
    // Tiny synthesised fallbacks so silence never fully wins.
    synthFallback(name, vol);
    return;
  }
  const src = c.createBufferSource();
  src.buffer = buf;
  src.playbackRate.value = rate;
  const g = c.createGain();
  g.gain.value = vol;
  src.connect(g);
  g.connect(master);
  src.start();
}

function startLoop(name, vol) {
  if (!enabled || !unlocked) return;
  const c = ensure();
  if (!c) return;
  stopLoop(name);
  const buf = buffers[name];
  if (!buf) return;
  const src = c.createBufferSource();
  src.buffer = buf;
  src.loop = true;
  const g = c.createGain();
  g.gain.value = vol;
  src.connect(g);
  g.connect(master);
  src.start();
  loops[name] = { src, gain: g };
}

function stopLoop(name) {
  const L = loops[name];
  if (!L) return;
  try { L.src.stop(); } catch { /* already stopped */ }
  delete loops[name];
}

function setLoopVol(name, vol) {
  const L = loops[name];
  if (!L) {
    if (vol > 0.01) startLoop(name, vol);
    return;
  }
  const c = ensure();
  if (!c) return;
  const t = c.currentTime;
  L.gain.gain.cancelScheduledValues(t);
  L.gain.gain.setTargetAtTime(Math.max(0, vol), t, 0.25);
  if (vol < 0.005) {
    // Let the fade finish then stop.
    setTimeout(() => { if (loops[name] === L) stopLoop(name); }, 800);
  }
}

/**
 * Ocean always-on when playing. Rain volume tracks storm intensity 0..1.
 * @param {boolean} playing
 * @param {number} storm 0 clear … 1 full thunderstorm
 */
export function setAmbience(playing, storm = 0) {
  if (!unlocked || !enabled) {
    stopLoop('ocean');
    stopLoop('rain');
    return;
  }
  if (!playing) {
    setLoopVol('ocean', 0);
    setLoopVol('rain', 0);
    setLoopVol('motor', 0);
    return;
  }
  setLoopVol('ocean', 0.22 + storm * 0.08);
  setLoopVol('rain', storm * 0.55);
}

/** Motor hum scales with |boat velocity| / top speed (0..1). */
export function setMotor(amount) {
  if (!unlocked || !enabled) return;
  setLoopVol('motor', Math.max(0, Math.min(1, amount)) * 0.28);
}

export function sfxThunder(intensity = 1) {
  playOnce('thunder', 0.35 + 0.45 * intensity, 0.9 + Math.random() * 0.2);
}

export function sfxCatch() {
  playOnce('splash', 0.35, 0.95 + Math.random() * 0.15);
}

export function sfxSell() {
  playOnce('sell', 0.45);
}

export function sfxBuy() {
  playOnce('sell', 0.28, 1.35);
}

/** Gulls over the boat. Pitch and level vary so the same sample does not read
 *  as one bird on a loop — `near` 0..1 is how close that one sounds. */
export function sfxGull(near = 0.6) {
  playOnce('gull', 0.10 + near * 0.24, 0.82 + Math.random() * 0.42);
}

/* ── synthesised fallbacks if WAV decode fails ───────────────────────────── */
function synthFallback(name, vol) {
  const c = ensure();
  if (!c || !master) return;
  const t = c.currentTime;
  if (name === 'thunder') {
    const g = c.createGain();
    g.gain.setValueAtTime(0.0001, t);
    g.gain.exponentialRampToValueAtTime(vol * 0.6, t + 0.01);
    g.gain.exponentialRampToValueAtTime(0.0001, t + 1.4);
    g.connect(master);
    const o = c.createOscillator();
    o.type = 'sine';
    o.frequency.setValueAtTime(55, t);
    o.frequency.exponentialRampToValueAtTime(30, t + 1.2);
    o.connect(g);
    o.start(t);
    o.stop(t + 1.5);
    // crackle
    const n = c.createBuffer(1, c.sampleRate * 0.15 | 0, c.sampleRate);
    const d = n.getChannelData(0);
    for (let i = 0; i < d.length; i++) d[i] = (Math.random() * 2 - 1) * (1 - i / d.length);
    const src = c.createBufferSource();
    src.buffer = n;
    const ng = c.createGain();
    ng.gain.value = vol * 0.35;
    src.connect(ng);
    ng.connect(master);
    src.start(t);
  } else if (name === 'splash' || name === 'sell') {
    const o = c.createOscillator();
    const g = c.createGain();
    o.type = name === 'sell' ? 'triangle' : 'sine';
    o.frequency.setValueAtTime(name === 'sell' ? 880 : 420, t);
    g.gain.setValueAtTime(vol * 0.3, t);
    g.gain.exponentialRampToValueAtTime(0.0001, t + 0.25);
    o.connect(g);
    g.connect(master);
    o.start(t);
    o.stop(t + 0.3);
  }
}
