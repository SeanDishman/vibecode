// The run: everything the simulation reads or writes. No DOM here — the HUD
// polls this state once a frame rather than the state pushing at the HUD.
import { CELL, COLS, ROWS, W, H, ENEMIES, MAX_LEVEL, hpScale, spdScale, armorScale, START_GOLD, START_LIVES } from './config.js';
import { PATH, BLOCKED } from './path.js';
import { burst, ring, clearFx } from './fx.js';
import { sfxPlace, sfxUpgrade, sfxSell, sfxLeak } from './audio.js';

export const S = {
  lives: START_LIVES, gold: START_GOLD, wave: 0, kills: 0, built: 0, leaked: 0,
  enemies: [], towers: [], shots: [], queue: [],
  spawnT: 0, breakT: 0, inWave: false,
  time: 0, speed: 1, shake: 0, hurt: 0,
  selectedType: null, selected: null, hoverCell: -1,
  grid: new Int32Array(COLS * ROWS).fill(-1),
  gameOver: false,
};

export function resetRun() {
  S.lives = START_LIVES; S.gold = START_GOLD;
  S.wave = 0; S.kills = 0; S.built = 0; S.leaked = 0;
  S.enemies.length = 0; S.towers.length = 0; S.shots.length = 0; S.queue.length = 0;
  S.grid.fill(-1);
  clearFx();
  S.time = 0; S.speed = 1; S.inWave = false; S.breakT = 12;
  S.shake = 0; S.hurt = 0;
  S.selected = null; S.selectedType = null; S.hoverCell = -1;
  S.gameOver = false;
}

// ======================================================================
//  Spawning
// ======================================================================
export function spawn(type, d = 0, lat = null) {
  const def = ENEMIES[type];
  // Bosses keep climbing with the run so late supers still have a wall to chew.
  const bossMul = def.boss
    ? Math.min(4.5, 1 + Math.max(0, S.wave / 8 - 1) * 0.42)
    : def.lives && def.lives > 1
      ? Math.min(2.4, 1 + Math.max(0, S.wave - 12) * 0.035) // tanks get a bit fatter late
      : 1;
  const scale = hpScale(S.wave);
  const hp = Math.round(def.hp * bossMul * scale);
  const shield = def.shield ? Math.round(def.shield * scale * (1 + Math.max(0, S.wave - 15) * 0.02)) : 0;
  // Instance armour so late armoured packs don't stay at base armour forever.
  const armor = (def.armor || 0) + (def.armor ? armorScale(S.wave) : 0);
  S.enemies.push({
    type, def, x: PATH[0].x, y: PATH[0].y,
    d, lat: lat === null ? (Math.random() * 2 - 1) * (def.boss ? 2 : 11) : lat,
    r: def.r, hp, max: hp, armor,
    shield, shieldMax: shield, shieldT: 0,
    spd: def.spd * spdScale(S.wave) * (0.94 + Math.random() * 0.12),
    chill: 0, chillT: 0, poison: 0, poisonT: 0, burn: 0, burnT: 0, flash: 0,
    phaseT: Math.random() * 4, immune: 0, healT: Math.random(),
    dead: false, ux: 1, uy: 0,
  });
}

// ======================================================================
//  Spatial hash — a busy wave is several hundred circles, and every turret
//  asks "who is in range?" every tick.
// ======================================================================
const HASH = 80;
const HCOLS = Math.ceil(W / HASH) + 2, HROWS = Math.ceil(H / HASH) + 2;
const buckets = Array.from({ length: HCOLS * HROWS }, () => []);

export function rehash() {
  for (let i = 0; i < buckets.length; i++) buckets[i].length = 0;
  for (const e of S.enemies) {
    if (e.dead) continue;
    const cx = Math.min(HCOLS - 1, Math.max(0, ((e.x / HASH) | 0) + 1));
    const cy = Math.min(HROWS - 1, Math.max(0, ((e.y / HASH) | 0) + 1));
    buckets[cy * HCOLS + cx].push(e);
  }
}

/** Visit every enemy whose bucket overlaps the given circle. */
export function nearby(x, y, radius, fn) {
  const x0 = Math.max(0, ((x - radius) / HASH | 0) + 1), x1 = Math.min(HCOLS - 1, ((x + radius) / HASH | 0) + 1);
  const y0 = Math.max(0, ((y - radius) / HASH | 0) + 1), y1 = Math.min(HROWS - 1, ((y + radius) / HASH | 0) + 1);
  for (let cy = y0; cy <= y1; cy++) {
    for (let cx = x0; cx <= x1; cx++) {
      const b = buckets[cy * HCOLS + cx];
      for (let i = 0; i < b.length; i++) fn(b[i]);
    }
  }
}

export const TARGET_MODES = ['first', 'last', 'strong', 'close'];
export const TARGET_LABEL = { first: 'First', last: 'Last', strong: 'Strongest', close: 'Closest' };

export function pickTarget(t, range, minRange) {
  let best = null, score = -Infinity;
  const r2 = range * range, min2 = minRange ? minRange * minRange : 0;
  nearby(t.x, t.y, range, e => {
    if (e.dead || e.immune > 0) return;
    const dx = e.x - t.x, dy = e.y - t.y, d2 = dx * dx + dy * dy;
    if (d2 > r2 || d2 < min2) return;
    let s;
    switch (t.targeting) {
      case 'last': s = -e.d; break;
      case 'strong': s = e.hp + e.shield; break;
      case 'close': s = -d2; break;
      default: s = e.d;
    }
    if (s > score) { score = s; best = e; }
  });
  return best;
}

// ======================================================================
//  Damage, death, leaks
// ======================================================================
export function damage(e, amount, opts) {
  if (e.dead || amount <= 0) return 0;
  if (e.immune > 0) return 0;
  const armor = (opts && opts.ignoreArmor) ? 0 : (e.armor ?? e.def.armor ?? 0);
  // Continuous beams/flames pass tiny per-frame amounts — don't floor those to 1.
  let dmg = amount < 1 ? Math.max(0, amount - armor * amount) : Math.max(1, amount - armor);
  let applied = 0;
  if (e.shield > 0) {
    const absorbed = Math.min(e.shield, dmg);
    e.shield -= absorbed;
    dmg -= absorbed;
    applied += absorbed;
    e.shieldT = 0;
  }
  if (dmg > 0) {
    const before = e.hp;
    e.hp -= dmg;
    applied += Math.min(before, dmg);
  }
  e.flash = 0.09;
  if (opts && opts.from) opts.from.damageDealt = (opts.from.damageDealt || 0) + applied;
  if (e.hp <= 0) kill(e);
  return applied;
}

export function kill(e) {
  if (e.dead) return;
  e.dead = true;
  S.kills++;
  S.gold += e.def.gold;
  burst(e.x, e.y, e.def.color, e.def.boss ? 34 : Math.min(10, 3 + e.r));
  if (e.def.boss) { S.shake = Math.max(S.shake, 9); ring(e.x, e.y, e.def.color, 90, 0.6); }
  if (e.def.splits) {
    for (let i = 0; i < e.def.splits; i++) {
      spawn('mini', e.d, (i - (e.def.splits - 1) / 2) * 8 + e.lat * 0.4);
    }
  }
}

export function leak(e) {
  e.dead = true;
  S.leaked++;
  S.lives -= e.def.lives || 1;
  S.shake = Math.max(S.shake, 7);
  S.hurt = 1;
  ring(e.x, e.y, '#ff5470', 70, 0.5);
  sfxLeak();
  if (S.lives <= 0) { S.lives = 0; S.gameOver = true; }
}

// ======================================================================
//  Building
// ======================================================================
export const canBuild = i => i >= 0 && !BLOCKED[i] && S.grid[i] === -1;

export function place(def, i) {
  if (!canBuild(i) || S.gold < def.cost) return false;
  S.gold -= def.cost;
  const cx = i % COLS, cy = (i / COLS) | 0;
  S.towers.push({
    def, id: def.id, i, level: 1, invested: def.cost,
    x: cx * CELL + CELL / 2, y: cy * CELL + CELL / 2,
    cool: 0, angle: -Math.PI / 2, heat: 0, charge: 0,
    targeting: def.targeting || 'first', target: null,
    dmgMul: 1, rateMul: 1, pulse: Math.random() * 3,
    spin: 0,
    damageDealt: 0,   // lifetime damage this placement has scored
  });
  S.grid[i] = S.towers.length - 1;
  S.built++;
  recomputeBuffs();
  burst(S.towers[S.towers.length - 1].x, S.towers[S.towers.length - 1].y, def.color, 12);
  sfxPlace(def.color);
  return true;
}

/**
 * Upgrade price scales with the tower's place cost so a 40g Pulse stays cheap to
 * rank and a 480g Oblivion is a serious investment every step.
 *
 * rank = current level (1 = first upgrade). Optional def.upgradePremium (>1)
 * makes end-game supers even steeper.
 */
export function upgradeCost(t) {
  if (!t) return 0;
  const base = t.def.cost;
  const rank = t.level; // 1..4 while under MAX_LEVEL
  const premium = t.def.upgradePremium || 1;
  // L1→2 ≈ 1.05× place, L4→5 ≈ 3.3× place (before premium).
  const curve = (0.70 + 0.35 * rank) * Math.pow(1.30, rank - 1);
  return Math.max(1, Math.round(base * curve * premium));
}

export function upgrade(t) {
  if (!t || t.level >= MAX_LEVEL) return false;
  const cost = upgradeCost(t);
  if (S.gold < cost) return false;
  S.gold -= cost;
  t.invested += cost;
  t.level++;
  recomputeBuffs();
  ring(t.x, t.y, t.def.color, 44, 0.4);
  sfxUpgrade();
  return true;
}

export const sellValue = t => Math.round(t.invested * 0.65);

export function sell(t) {
  if (!t) return false;
  const idx = S.towers.indexOf(t);
  if (idx < 0) return false;
  S.gold += sellValue(t);
  S.towers.splice(idx, 1);
  S.grid.fill(-1);
  S.towers.forEach((tw, k) => { S.grid[tw.i] = k; });
  if (S.selected === t) S.selected = null;
  recomputeBuffs();
  burst(t.x, t.y, '#ffd166', 14);
  sfxSell();
  return true;
}

/** Amplifiers are the only turret-to-turret interaction, so their effect is
 *  recomputed on every build/sell/upgrade instead of every tick. */
export function recomputeBuffs() {
  for (const t of S.towers) { t.dmgMul = 1; t.rateMul = 1; }
  for (const a of S.towers) {
    if (a.def.kind !== 'buff') continue;
    const range = statRange(a), lvl = buffScale(a.level);
    for (const t of S.towers) {
      if (t === a || t.def.kind === 'buff') continue;
      if (Math.hypot(t.x - a.x, t.y - a.y) <= range) {
        t.dmgMul += a.def.dmgMul * lvl;
        t.rateMul += a.def.rateMul * lvl;
      }
    }
  }
}

// Upgrade curves — each rank costs ~a new tower, so the bump has to feel it.
// Old values (range +9% / dmg +48% / rate +16%) left range feeling like a joke
// and left secondary DoTs (poison) frozen at level-1 numbers.
export const rangeOf = (def, level) => def.range * (1 + (level - 1) * 0.14);
export const statRange = t => rangeOf(t.def, t.level);
export const statDmg = t => (t.def.dmg || 0) * (1 + (t.level - 1) * 0.55) * t.dmgMul;
export const statDps = t => (t.def.dps || 0) * (1 + (t.level - 1) * 0.55) * t.dmgMul;
export const statRate = t => t.def.rate / (1 + (t.level - 1) * 0.22) / t.rateMul;

/** Splash / nova radius growth per level (~+14% each rank). */
export const splashScale = level => 1 + (level - 1) * 0.14;
/** Flame cone half-angle growth (~+12% each rank). */
export const coneScale = level => 1 + (level - 1) * 0.12;
/** Cryo slow growth — capped in sim so it never freezes solid. */
export const slowScale = level => 1 + (level - 1) * 0.18;
/** Amp aura strength growth. */
export const buffScale = level => 1 + (level - 1) * 0.40;

/**
 * Venom poison — scales hard with level. Base is already meaningful; each rank
 * is ~+45% dps and a bit more duration so late-game tanks still melt.
 * Returns null if the turret has no poison profile.
 */
export function statPoison(t) {
  const p = t && t.def && t.def.poison;
  if (!p) return null;
  const lv = Math.max(0, (t.level || 1) - 1);
  return {
    dps: p.dps * (1 + lv * 0.45) * (t.dmgMul || 1),
    dur: p.dur * (1 + lv * 0.18),
  };
}

/**
 * Flame napalm burn. Unlocks at level 3 (the "Napalm" rank), then ramps.
 * Decent lingering damage, not a second laser — total DoT ≈ a few seconds of
 * the cone's own DPS on a single target.
 * Returns null below level 3.
 */
export function statBurn(t) {
  if (!t || t.def.kind !== 'flame' || t.level < 3) return null;
  // L3 = Napalm unlock, L4 Sticky fuel, L5 Inferno.
  // ~0.85× cone DPS while burning — solid linger on packs that walk through,
  // not a second laser (cone still does the bulk while they're in the jet).
  const rank = t.level - 2; // 1 at L3, 2 at L4, 3 at L5
  const baseDps = (t.def.dps || 22) * 0.85;
  return {
    dps: baseDps * (1 + (rank - 1) * 0.38) * (t.dmgMul || 1),
    dur: 3.0 + (rank - 1) * 1.0, // 3s → 4s → 5s
  };
}

/** Normalize upgrade entry (string legacy or { name, desc }). */
function upgradeEntry(list, index) {
  if (!list || !list.length) return null;
  const raw = list[Math.min(list.length - 1, Math.max(0, index))];
  if (raw == null) return null;
  if (typeof raw === 'string') return { name: raw, desc: '' };
  return { name: raw.name || `Level ${index + 2}`, desc: raw.desc || '' };
}

/** Name of the rank this tower currently has (level ≥ 2), for the inspector subtitle. */
export function currentUpgradeName(t) {
  if (!t || t.level < 2) return null;
  const e = upgradeEntry(t.def.upgrades, t.level - 2);
  return e ? e.name : null;
}

/** Flavour line for the next upgrade, if any. */
export function nextUpgradeName(t) {
  if (!t || t.level >= MAX_LEVEL) return null;
  const e = upgradeEntry(t.def.upgrades, t.level - 1);
  return e ? e.name : `Level ${t.level + 1}`;
}

/** Authored description for the next upgrade rank (empty string if missing). */
export function nextUpgradeDesc(t) {
  if (!t || t.level >= MAX_LEVEL) return '';
  const e = upgradeEntry(t.def.upgrades, t.level - 1);
  return (e && e.desc) || '';
}

/**
 * Hover preview for the next rank: authored blurb first, then live number deltas.
 * @returns {{ name: string, cost: number, desc: string, lines: string[] } | null}
 */
export function upgradePreview(t) {
  if (!t || t.level >= MAX_LEVEL) return null;
  const name = nextUpgradeName(t) || `Level ${t.level + 1}`;
  const desc = nextUpgradeDesc(t);
  const cost = upgradeCost(t);
  const cur = t.level;
  const nxt = cur + 1;
  const now = { def: t.def, level: cur, dmgMul: t.dmgMul || 1, rateMul: t.rateMul || 1 };
  const next = { def: t.def, level: nxt, dmgMul: t.dmgMul || 1, rateMul: t.rateMul || 1 };
  const kind = t.def.kind;
  const lines = [];
  const pct = (a, b) => {
    if (!(a > 0)) return '';
    const p = Math.round(((b - a) / a) * 100);
    return p > 0 ? ` (+${p}%)` : p < 0 ? ` (${p}%)` : '';
  };
  const pushRange = () => {
    const a = Math.round(statRange(now)), b = Math.round(statRange(next));
    if (a !== b) lines.push(`Range ${a} → ${b}${pct(a, b)}`);
  };
  const pushShotDmg = (label = 'Shot damage') => {
    const a = Math.round(statDmg(now)), b = Math.round(statDmg(next));
    if (a !== b) lines.push(`${label} ${a} → ${b}${pct(a, b)}`);
  };
  const pushDps = (label = 'Damage/s') => {
    const a = Math.round(statDps(now)), b = Math.round(statDps(next));
    if (a !== b) lines.push(`${label} ${a} → ${b}${pct(a, b)}`);
  };
  const pushRate = () => {
    if (!t.def.rate) return;
    const a = 1 / statRate(now), b = 1 / statRate(next);
    if (Math.abs(a - b) > 0.05) {
      lines.push(`Fire rate ${a.toFixed(1)} → ${b.toFixed(1)}/s${pct(a, b)}`);
    }
  };
  const pushSplash = (label = 'Splash') => {
    if (!t.def.splash && kind !== 'nova') return;
    const base = t.def.splash || t.def.range || 0;
    const a = Math.round(base * splashScale(cur));
    const b = Math.round(base * splashScale(nxt));
    if (a !== b) lines.push(`${label} ${a} → ${b}${pct(a, b)}`);
  };

  if (kind === 'aura') {
    pushRange();
    const a = Math.round(t.def.slow * slowScale(cur) * 100);
    const b = Math.round(t.def.slow * slowScale(nxt) * 100);
    if (a !== b) lines.push(`Slow ${a}% → ${b}% of enemy speed`);
  } else if (kind === 'buff') {
    pushRange();
    const bsA = buffScale(cur), bsB = buffScale(nxt);
    const da = Math.round(t.def.dmgMul * bsA * 100);
    const db = Math.round(t.def.dmgMul * bsB * 100);
    const ra = Math.round(t.def.rateMul * bsA * 100);
    const rb = Math.round(t.def.rateMul * bsB * 100);
    if (da !== db) lines.push(`Damage boost +${da}% → +${db}%`);
    if (ra !== rb) lines.push(`Fire-rate boost +${ra}% → +${rb}%`);
  } else if (kind === 'beam') {
    pushRange();
    pushDps('Beam DPS');
  } else if (kind === 'flame') {
    pushRange();
    pushDps('Cone DPS');
    const ca = Math.round((t.def.cone || 0.6) * coneScale(cur) * (180 / Math.PI));
    const cb = Math.round((t.def.cone || 0.6) * coneScale(nxt) * (180 / Math.PI));
    if (ca !== cb) lines.push(`Cone width ~${ca}° → ~${cb}°`);
    if (cur < 3 && nxt >= 3) {
      const burn = statBurn(next);
      if (burn) lines.push(`Napalm unlock: ${Math.round(burn.dps)}/s for ${burn.dur.toFixed(0)}s`);
    } else if (nxt >= 3) {
      const a = statBurn(now), b = statBurn(next);
      if (a && b) {
        lines.push(`Napalm burn ${Math.round(a.dps)} → ${Math.round(b.dps)}/s`);
        if (a.dur !== b.dur) lines.push(`Burn duration ${a.dur.toFixed(0)}s → ${b.dur.toFixed(0)}s`);
      }
    }
  } else if (kind === 'nova') {
    pushRange();
    pushShotDmg('Pulse damage');
    pushRate();
    pushSplash('Blast radius');
  } else if (kind === 'singularity') {
    pushRange();
    pushDps('Melt DPS');
    const a = Math.round(Math.min(0.90, (t.def.slow || 0.5) * (1 + (cur - 1) * 0.09)) * 100);
    const b = Math.round(Math.min(0.90, (t.def.slow || 0.5) * (1 + (nxt - 1) * 0.09)) * 100);
    if (a !== b) lines.push(`Slow ${a}% → ${b}%`);
    if (cur < 3 && nxt >= 3) lines.push('UNLOCK tidal pull (drag enemies backward)');
    if (cur < 4 && nxt >= 4) lines.push('UNLOCK armour ignore on melt');
    if (cur < 5 && nxt >= 5) lines.push('UNLOCK core collapse pulse every ~2.4s');
  } else if (kind === 'tempest') {
    pushRange();
    pushShotDmg('Bolt damage');
    pushRate();
    const a = (t.def.strikes || 4) + cur - 1;
    const b = (t.def.strikes || 4) + nxt - 1;
    if (a !== b) lines.push(`Lightning bolts ${a} → ${b}`);
    if (cur < 3 && nxt >= 3) lines.push('UNLOCK fork jump to a nearby enemy (60% dmg)');
    if (cur < 4 && nxt >= 4) lines.push('UNLOCK bolts ignore armour');
    if (cur < 5 && nxt >= 5) lines.push('UNLOCK shock stun on hit (~0.45s)');
  } else if (kind === 'oblivion') {
    pushRange();
    pushShotDmg('Ray damage');
    pushRate();
    lines.push('Always ignores armour');
    if (cur < 2 && nxt >= 2) lines.push('UNLOCK execute on low-HP targets');
    else if (nxt >= 2) {
      const a = Math.round((0.16 + Math.max(0, cur - 2) * 0.04) * 100);
      const b = Math.round((0.16 + Math.max(0, nxt - 2) * 0.04) * 100);
      if (a !== b) lines.push(`Execute threshold ${a}% → ${b}% HP`);
    }
    if (cur < 4 && nxt >= 4) lines.push('UNLOCK split beam (55% ray on #2 strongest)');
  } else {
    pushRange();
    pushShotDmg();
    pushRate();
    pushSplash();
    if (t.def.pellets) {
      const a = t.def.pellets + cur - 1;
      const b = t.def.pellets + nxt - 1;
      if (a !== b) lines.push(`Pellets ${a} → ${b}`);
    }
    if (t.def.pierce) {
      const a = t.def.pierce + cur - 1;
      const b = t.def.pierce + nxt - 1;
      if (a !== b) lines.push(`Pierce ${a} → ${b} enemies`);
    }
    if (t.def.chains) {
      const a = t.def.chains + cur - 1;
      const b = t.def.chains + nxt - 1;
      if (a !== b) lines.push(`Chain jumps ${a} → ${b}`);
    }
    if (t.def.poison) {
      const a = statPoison(now), b = statPoison(next);
      if (a && b) {
        lines.push(`Poison ${Math.round(a.dps)} → ${Math.round(b.dps)}/s (ignores armour)`);
        if (Math.abs(a.dur - b.dur) > 0.05) {
          lines.push(`Poison duration ${a.dur.toFixed(1)}s → ${b.dur.toFixed(1)}s`);
        }
      }
    }
    if (kind === 'sniper' && cur < 3 && nxt >= 3) {
      lines.push('Armor ignore unlocks');
    }
  }

  return { name, cost, desc, lines };
}
