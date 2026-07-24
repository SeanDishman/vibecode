// Turret "profile pictures" — 20×20 pixel icons for the inspect card.
// Built like real TD card art from the user sketch:
//   wide base → boxy housing on top → barrel + muzzle facing the viewer (¾ front).
// Level stamps bolt on extra hardware so L1 / L3 / L5 read as different machines.

import { mix } from './colors.js';

const cache = new Map();

/** Palette keys shared across portraits. */
const P = {
  k: '#080a10',   // hard outline
  d: '#1c2434',   // dark metal / base
  m: '#4e5a70',   // mid metal body
  l: '#9aa6ba',   // light metal rim
  w: '#f0f4fa',   // highlight / muzzle
  x: null,
};

/**
 * @param {string} id turret def id
 * @param {string} color empire/turret accent
 * @param {number} level 1..5
 * @param {number} scale pixels per art pixel (default 4 → 80px for the 72px card)
 */
export function bakePortrait(id, color, level = 1, scale = 4) {
  const tier = level >= 5 ? 2 : level >= 3 ? 1 : 0;
  const key = `${id}|${color}|${tier}|${scale}`;
  if (cache.has(key)) return cache.get(key);

  const size = 20;
  const cv = document.createElement('canvas');
  cv.width = size * scale;
  cv.height = size * scale;
  const g = cv.getContext('2d');
  g.imageSmoothingEnabled = false;

  // Flat dark card fill only — no vignette ring (it fought the turret silhouette).
  const bg = g.createLinearGradient(0, 0, cv.width, cv.height);
  bg.addColorStop(0, mix('#1a2438', color, 0.18));
  bg.addColorStop(0.55, '#0c101c');
  bg.addColorStop(1, '#06080f');
  g.fillStyle = bg;
  g.fillRect(0, 0, cv.width, cv.height);

  blit(g, portraitRows(id, tier), scale, color);

  if (tier >= 1) {
    g.fillStyle = color;
    for (let i = 0; i <= tier; i++) {
      const bx = cv.width - scale * 2.2 - i * scale * 2.1;
      const by = cv.height - scale * 2.4;
      g.fillRect(bx, by, scale, scale);
    }
  }

  cache.set(key, cv);
  return cv;
}

export function clearPortraitCache() { cache.clear(); }

function blit(g, rows, scale, accent) {
  const pal = {
    ...P,
    a: accent,
    A: mix(accent, '#ffffff', 0.45),
    b: mix(accent, '#05070c', 0.40),
    g: mix(accent, '#9ff05f', 0.45),
    o: '#ff9d5c',
    y: '#ffd166',
    c: '#7ec8ff',
    p: '#c9a2ff',
    r: '#ff6da9',
    f: '#ff7a3d',
  };
  for (let y = 0; y < rows.length; y++) {
    const row = rows[y];
    for (let x = 0; x < row.length; x++) {
      const ch = row[x];
      if (ch === '.' || ch === 'x') continue;
      const col = pal[ch];
      if (!col) continue;
      g.fillStyle = col;
      g.fillRect(x * scale, y * scale, scale, scale);
    }
  }
}

function portraitRows(id, tier) {
  const base = BASE[id] || BASE.pulse;
  const extra = EXTRA[id];
  const rows = base.map(r => r.split(''));
  if (extra) {
    for (const stamp of extra) {
      if (stamp.tier > tier) continue;
      for (const [x, y, ch] of stamp.px) {
        if (y >= 0 && y < rows.length && x >= 0 && x < rows[y].length) rows[y][x] = ch;
      }
    }
  }
  return rows.map(r => r.join(''));
}

/* ── 20×20 silhouettes (user-sketch layout, facing the card) ────────────────
   Bottom: wide plinth. Mid: box housing. Right/front: barrel + muzzle ring.
   Legend: k outline · d dark · m mid · l light · w white · a/A/b accent
           c ice · p purple · g green · o orange · y yellow · r pink · f fire
   Every string is exactly 20 chars × 20 rows. */

const BASE = {
  // Starter single-barrel — barrel aimed at the viewer
  pulse: [
    '....................',
    '....kkkkkkkkkk......',
    '...kmmmmmmmAAkk.....',
    '...kmmlwwwmAAkkk....',
    '...kmmmmmmAAkkAAk...',
    '...kmmmmmmAkkk.Ak...',
    '...kmmmmmmkk...k....',
    '...kmmmmmmk.........',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kdddddddddddddk....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Twin shotgun muzzles looking at you
  flak: [
    '....................',
    '...kkkk....kkkk.....',
    '..kAAkk....kAAkk....',
    '..kAAkkkkkkAAkkk....',
    '..kAAmmmmmmAAkAAk...',
    '...kmmmmmmmmmk.Ak...',
    '...kmmlwwwwmmk.k....',
    '...kmmmmmmmmmk......',
    '...kmmmmmmmmk.......',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kdddddddddddddk....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Cryo freeze dish (aura — no long barrel)
  cryo: [
    '....................',
    '......kkkkkk........',
    '....kkcAAAAckk......',
    '...kcAAwwwwAAck.....',
    '..kcAAwwwwwwAAck....',
    '...kcAAwwwwAAck.....',
    '....kkcAAAAckk......',
    '......kcAAck........',
    '.....kkcAAckk.......',
    '....kcAkkkkAck......',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Fat howitzer, open muzzle ring
  cannon: [
    '....................',
    '...kkkkkkkkkkk......',
    '..kmmmmmmmAAAkk.....',
    '..kmmlwwwmAAAAkk....',
    '..kmmmmmmAAAAkkk....',
    '..kmmmmmmAAAkkAAk...',
    '..kmmmmmmAAkkk.Ak...',
    '..kmmmmmmAkk...k....',
    '..kmmmmmmkk.........',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kdddddddddddddk....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Venom spitter + drip
  venom: [
    '....................',
    '....kkkkkkkkkk......',
    '...kmmmmmmgAAkk.....',
    '...kmmgwwwgAAkkk....',
    '...kmmggggAAkkAAk...',
    '...kmmggggAkkk.Ak...',
    '...kmmmmmmkk...k....',
    '...kmmmmmmk.........',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kdddddddddddddk....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk....gg....kk....',
    '........kk..........',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Tesla coil orb on a post
  tesla: [
    '....................',
    '...k..k..k..k.......',
    '....k.ppppk.k.......',
    '.....kppAAppk.......',
    '....kppAAAAppk......',
    '...kpAAwwwwAApk.....',
    '....kppAAAAppk......',
    '.....kppAAppk.......',
    '......kppppk........',
    '.....kkddddkk.......',
    '....kddddddddk......',
    '...kddddddddddk.....',
    '..kddbbbbbbbbddk....',
    '..kddddddddddddk....',
    '..kmmkkkkkkkkmmk....',
    '...kk........kk.....',
    '..kp..........pk....',
    '....................',
    '....................',
    '....................',
  ],
  // Slim pink beam emitter
  laser: [
    '....................',
    '....kkkkkkkkkk......',
    '...kmmmmmmrrAkk.....',
    '...kmmrwwwrAAkkk....',
    '...kmmrrrrAAkkAAk...',
    '...kmmrrrrAkkk.Ak...',
    '...kmmmmmmkk...k....',
    '...kmmmmmmk.........',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kdddddddddddddk....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Twin rocket pods
  missile: [
    '....................',
    '...kkkk....kkkk.....',
    '..kAAkk....kAAkk....',
    '..kommkkkkkkommok...',
    '..kommAAAAAAmmok....',
    '..kmmkAAwwAAkmmk....',
    '..kmmkAAAAAAkmmk....',
    '...kmmkkkkkkmmk.....',
    '...kmmmmmmmmk.......',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kdddddddddddddk....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Long rail with glowing bore + collar
  rail: [
    '....................',
    '...kkkkkkkkkkkkk....',
    '..kmmmmmmcAAAkkk....',
    '..kmmlwwcAAAAAkkk...',
    '..kmmmmmcAAAAAkkAk..',
    '..kmmmmmcAAAkkk.Ak..',
    '..kmmmmmcAAkk...k...',
    '..kmmmmmcAkk........',
    '..kmmmmmckk.........',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kdddddddddddddk....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Mortar tube mouth open upward / toward camera
  mortar: [
    '....................',
    '.....kkkkkkk........',
    '....kmmAAAAmmk......',
    '...kmmAAwwAAmmk.....',
    '...kmmAAAAAAmmk.....',
    '....kmmAAAAmmk......',
    '.....kmmmmmmk.......',
    '......kkmmkk........',
    '.......kmmk.........',
    '......kkddkk........',
    '.....kddddddk.......',
    '....kddddddddk......',
    '...kddbbbbbbddk.....',
    '...kddddddddddk.....',
    '...kmmkkkkkkmmk.....',
    '....kk......kk......',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Amplifier beacon ring (buff, no gun)
  amp: [
    '....................',
    '.......kkk..........',
    '......kAAAk.........',
    '.....kAwwwAk........',
    '....kAAkkkAAk.......',
    '...kAAk...kAAk......',
    '..kAAk.....kAAk.....',
    '...kAAk...kAAk......',
    '....kAAkkkAAk.......',
    '.....kAwwwAk........',
    '......kAAAk.........',
    '.....kkdddkk........',
    '....kdddddddk.......',
    '...kddbbbbbddk......',
    '...kdddddddddk......',
    '...kmmkkkkkmmk......',
    '....kk.....kk.......',
    '....................',
    '....................',
    '....................',
  ],
  // Flamethrower nozzle + pilot flame
  flame: [
    '....................',
    '...........kyyk.....',
    '....kkkkkkkkyyyk....',
    '...kmmmmmmfAAfyk....',
    '...kmmfwwwfAAfyk....',
    '...kmmffffAAkkk.....',
    '...kmmffffAkk.......',
    '...kmmmmmmk.........',
    '...kmmmmmmk.........',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kdddddddddddddk....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Long sniper barrel + scope cheek
  sniper: [
    '....................',
    '...kkkkkkkkkkkkk....',
    '..kmmmmmmmAAAkkk....',
    '..kmmllwwmAAAAAkk...',
    '..kmmmmmmAAAAAkkk...',
    '..kmmmmmmAAAkkkAk...',
    '..kmmmmmmAAkk..Ak...',
    '..kmmwmmmmkk...k....',
    '..kmmmmmmk..........',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kdddddddddddddk....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Nova omni orb
  nova: [
    '....................',
    '......kpAApk........',
    '.....kpAAAApk.......',
    '....kpAAwwAApk......',
    '...kpAAwwwwAApk.....',
    '....kpAAwwAApk......',
    '.....kpAAAApk.......',
    '......kpAApk........',
    '.....kkpAApkk.......',
    '....kpAkkkkApk......',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  // Gatling multi-bore face at the camera
  gatling: [
    '....................',
    '...kkkkkkkkkkk......',
    '..kmAAmAAmAAkkk.....',
    '..kmmmmmmmmAAkkk....',
    '..kmAAmAAmAAkkAAk...',
    '..kmmmmmmmmAAkk.k...',
    '..kmAAmAAmAkkk......',
    '..kmmmmmmmmkk.......',
    '..kmmmmmmmk.........',
    '...kkddddddkkk......',
    '..kdddddddddddk.....',
    '.kdddddddddddddk....',
    '.kddbbbbbbbbbbddk...',
    '.kddddddddddddddk...',
    '.kmmkkkkkkkkkkmmk...',
    '..kk..........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  singularity: [
    '....................',
    '.......kkkkk........',
    '.....kkpppppkk......',
    '....kpAApppAApk.....',
    '...kpAAwwwwwAApk....',
    '...kpAAwwkwwAApk....',
    '...kpAAwwwwwAApk....',
    '....kpAApppAApk.....',
    '.....kkpppppkk......',
    '......kkkkk.........',
    '.....kddddddk.......',
    '....kddddddddk......',
    '...kddbbbbbbddk.....',
    '...kddddddddddk.....',
    '...kmmkkkkkkmmk.....',
    '....kk......kk......',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  oblivion: [
    '....................',
    '.........kk.........',
    '........kwwk........',
    '........kAAk........',
    '........kAAk........',
    '........krrk........',
    '.......kkmmkk.......',
    '......kmmmmmmk......',
    '.....kllmmmmllk.....',
    '....kdmmmmmmmmdk....',
    '....kddmmmmmdddk....',
    '.....kddddddddk.....',
    '....kkddbbbdddkk....',
    '...kmmddddddddmmk...',
    '...kmmkkkkkkkkmmk...',
    '....kk........kk....',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
  tempest: [
    '....................',
    '....k..kck..k.......',
    '...kck..k..kck......',
    '....k..kck..k.......',
    '......kccck.........',
    '.....kcAAAck........',
    '....kcAAwwAAck......',
    '....kcAAwwAAck......',
    '.....kcAAAck........',
    '......kccck.........',
    '.....kkddddkk.......',
    '....kmmddddmmk......',
    '....kmmddddmmk......',
    '....kmmkkkkmmk......',
    '.....kk....kk.......',
    '....................',
    '....................',
    '....................',
    '....................',
    '....................',
  ],
};

/** Extra pixels stamped at tier ≥ N. [x, y, char] art coords. */
const EXTRA = {
  pulse: [
    { tier: 1, px: [[2, 3, 'l'], [12, 2, 'A'], [14, 4, 'w'], [4, 11, 'a'], [13, 11, 'a']] },
    { tier: 2, px: [[1, 4, 'm'], [15, 5, 'A'], [13, 1, 'w'], [3, 12, 'A'], [14, 12, 'A']] },
  ],
  flak: [
    { tier: 1, px: [[1, 2, 'A'], [16, 2, 'A'], [8, 6, 'w'], [9, 6, 'w']] },
    { tier: 2, px: [[0, 3, 'a'], [17, 3, 'a'], [5, 1, 'A'], [12, 1, 'A'], [3, 13, 'm'], [14, 13, 'm']] },
  ],
  cryo: [
    { tier: 1, px: [[3, 3, 'c'], [15, 3, 'c'], [9, 2, 'w'], [10, 4, 'w']] },
    { tier: 2, px: [[1, 5, 'A'], [17, 5, 'A'], [4, 12, 'c'], [14, 12, 'c']] },
  ],
  cannon: [
    { tier: 1, px: [[13, 3, 'w'], [14, 4, 'A'], [3, 11, 'o']] },
    { tier: 2, px: [[15, 5, 'w'], [2, 4, 'l'], [14, 12, 'o'], [4, 12, 'o']] },
  ],
  venom: [
    { tier: 1, px: [[5, 3, 'g'], [14, 4, 'g'], [7, 14, 'g'], [10, 14, 'g']] },
    { tier: 2, px: [[3, 4, 'g'], [15, 5, 'A'], [8, 15, 'g'], [4, 12, 'g']] },
  ],
  tesla: [
    { tier: 1, px: [[1, 2, 'p'], [15, 2, 'p'], [9, 5, 'w']] },
    { tier: 2, px: [[0, 6, 'A'], [17, 6, 'A'], [3, 14, 'p'], [14, 14, 'p']] },
  ],
  laser: [
    { tier: 1, px: [[5, 3, 'r'], [14, 4, 'w'], [4, 11, 'r']] },
    { tier: 2, px: [[15, 5, 'w'], [3, 4, 'A'], [13, 11, 'r'], [12, 2, 'A']] },
  ],
  missile: [
    { tier: 1, px: [[3, 2, 'o'], [14, 2, 'o'], [8, 5, 'w'], [9, 5, 'w']] },
    { tier: 2, px: [[2, 1, 'A'], [15, 1, 'A'], [6, 0, 'y'], [11, 0, 'y'], [4, 12, 'o']] },
  ],
  rail: [
    { tier: 1, px: [[6, 3, 'c'], [14, 3, 'w'], [15, 4, 'c']] },
    { tier: 2, px: [[16, 5, 'A'], [4, 4, 'c'], [5, 2, 'w'], [3, 12, 'c'], [14, 12, 'c']] },
  ],
  mortar: [
    { tier: 1, px: [[5, 2, 'A'], [13, 2, 'A'], [9, 3, 'w']] },
    { tier: 2, px: [[4, 1, 'w'], [14, 1, 'w'], [2, 13, 'm'], [15, 13, 'm']] },
  ],
  amp: [
    { tier: 1, px: [[4, 5, 'A'], [13, 5, 'A'], [9, 3, 'w']] },
    { tier: 2, px: [[2, 7, 'y'], [15, 7, 'y'], [8, 1, 'A'], [9, 1, 'A']] },
  ],
  flame: [
    { tier: 1, px: [[12, 1, 'y'], [14, 2, 'f'], [5, 4, 'f']] },
    { tier: 2, px: [[13, 0, 'y'], [15, 1, 'f'], [3, 5, 'A'], [13, 11, 'f']] },
  ],
  sniper: [
    { tier: 1, px: [[6, 7, 'w'], [14, 3, 'w'], [15, 4, 'A']] },
    { tier: 2, px: [[16, 5, 'w'], [4, 3, 'l'], [5, 11, 'd'], [13, 2, 'A']] },
  ],
  nova: [
    { tier: 1, px: [[4, 4, 'p'], [14, 4, 'p'], [9, 2, 'w']] },
    { tier: 2, px: [[2, 6, 'A'], [16, 6, 'A'], [7, 1, 'p'], [11, 1, 'p']] },
  ],
  gatling: [
    { tier: 1, px: [[3, 2, 'A'], [11, 2, 'A'], [14, 3, 'w']] },
    { tier: 2, px: [[2, 4, 'a'], [15, 5, 'A'], [4, 1, 'l'], [12, 1, 'l'], [3, 12, 'd']] },
  ],
  singularity: [
    { tier: 1, px: [[5, 4, 'p'], [14, 4, 'p'], [9, 3, 'w']] },
    { tier: 2, px: [[4, 5, 'A'], [15, 5, 'A'], [8, 2, 'p'], [11, 2, 'p']] },
  ],
  oblivion: [
    { tier: 1, px: [[8, 2, 'w'], [11, 2, 'r'], [6, 7, 'r']] },
    { tier: 2, px: [[9, 1, 'w'], [10, 1, 'w'], [5, 10, 'A'], [14, 10, 'A']] },
  ],
  tempest: [
    { tier: 1, px: [[3, 2, 'c'], [15, 2, 'c'], [9, 5, 'w']] },
    { tier: 2, px: [[2, 6, 'A'], [16, 6, 'A'], [5, 1, 'c'], [13, 1, 'c']] },
  ],
};

/** Format big damage numbers for the inspect card. */
export function formatDamage(n) {
  n = Math.floor(n || 0);
  if (n >= 1e6) return (n / 1e6).toFixed(2) + 'M';
  if (n >= 1e4) return (n / 1e3).toFixed(1) + 'k';
  if (n >= 1e3) return (n / 1e3).toFixed(2) + 'k';
  return String(n);
}
