import fs from 'fs';

const path = new URL('../src/state.js', import.meta.url);
let src = fs.readFileSync(path, 'utf8');
const start = src.indexOf('/** Flavour line for the next upgrade');
if (start < 0) throw new Error('start marker not found');

const newBlock = `/** Normalize upgrade entry (string legacy or { name, desc }). */
function upgradeEntry(list, index) {
  if (!list || !list.length) return null;
  const raw = list[Math.min(list.length - 1, Math.max(0, index))];
  if (raw == null) return null;
  if (typeof raw === 'string') return { name: raw, desc: '' };
  return { name: raw.name || \`Level \${index + 2}\`, desc: raw.desc || '' };
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
  return e ? e.name : \`Level \${t.level + 1}\`;
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
  const name = nextUpgradeName(t) || \`Level \${t.level + 1}\`;
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
    return p > 0 ? \` (+\${p}%)\` : p < 0 ? \` (\${p}%)\` : '';
  };
  const pushRange = () => {
    const a = Math.round(statRange(now)), b = Math.round(statRange(next));
    if (a !== b) lines.push(\`Range \${a} → \${b}\${pct(a, b)}\`);
  };
  const pushShotDmg = (label = 'Shot damage') => {
    const a = Math.round(statDmg(now)), b = Math.round(statDmg(next));
    if (a !== b) lines.push(\`\${label} \${a} → \${b}\${pct(a, b)}\`);
  };
  const pushDps = (label = 'Damage/s') => {
    const a = Math.round(statDps(now)), b = Math.round(statDps(next));
    if (a !== b) lines.push(\`\${label} \${a} → \${b}\${pct(a, b)}\`);
  };
  const pushRate = () => {
    if (!t.def.rate) return;
    const a = 1 / statRate(now), b = 1 / statRate(next);
    if (Math.abs(a - b) > 0.05) {
      lines.push(\`Fire rate \${a.toFixed(1)} → \${b.toFixed(1)}/s\${pct(a, b)}\`);
    }
  };
  const pushSplash = (label = 'Splash') => {
    if (!t.def.splash && kind !== 'nova') return;
    const base = t.def.splash || t.def.range || 0;
    const a = Math.round(base * splashScale(cur));
    const b = Math.round(base * splashScale(nxt));
    if (a !== b) lines.push(\`\${label} \${a} → \${b}\${pct(a, b)}\`);
  };

  if (kind === 'aura') {
    pushRange();
    const a = Math.round(t.def.slow * slowScale(cur) * 100);
    const b = Math.round(t.def.slow * slowScale(nxt) * 100);
    if (a !== b) lines.push(\`Slow \${a}% → \${b}% of enemy speed\`);
  } else if (kind === 'buff') {
    pushRange();
    const bsA = buffScale(cur), bsB = buffScale(nxt);
    const da = Math.round(t.def.dmgMul * bsA * 100);
    const db = Math.round(t.def.dmgMul * bsB * 100);
    const ra = Math.round(t.def.rateMul * bsA * 100);
    const rb = Math.round(t.def.rateMul * bsB * 100);
    if (da !== db) lines.push(\`Damage boost +\${da}% → +\${db}%\`);
    if (ra !== rb) lines.push(\`Fire-rate boost +\${ra}% → +\${rb}%\`);
  } else if (kind === 'beam') {
    pushRange();
    pushDps('Beam DPS');
  } else if (kind === 'flame') {
    pushRange();
    pushDps('Cone DPS');
    const ca = Math.round((t.def.cone || 0.6) * coneScale(cur) * (180 / Math.PI));
    const cb = Math.round((t.def.cone || 0.6) * coneScale(nxt) * (180 / Math.PI));
    if (ca !== cb) lines.push(\`Cone width ~\${ca}° → ~\${cb}°\`);
    if (cur < 3 && nxt >= 3) {
      const burn = statBurn(next);
      if (burn) lines.push(\`Napalm unlock: \${Math.round(burn.dps)}/s for \${burn.dur.toFixed(0)}s\`);
    } else if (nxt >= 3) {
      const a = statBurn(now), b = statBurn(next);
      if (a && b) {
        lines.push(\`Napalm burn \${Math.round(a.dps)} → \${Math.round(b.dps)}/s\`);
        if (a.dur !== b.dur) lines.push(\`Burn duration \${a.dur.toFixed(0)}s → \${b.dur.toFixed(0)}s\`);
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
    const a = Math.round(Math.min(0.88, (t.def.slow || 0.5) * (1 + (cur - 1) * 0.08)) * 100);
    const b = Math.round(Math.min(0.88, (t.def.slow || 0.5) * (1 + (nxt - 1) * 0.08)) * 100);
    if (a !== b) lines.push(\`Slow \${a}% → \${b}%\`);
    if (cur < 4 && nxt >= 4) lines.push('UNLOCK armour ignore on melt');
  } else if (kind === 'tempest') {
    pushRange();
    pushShotDmg('Bolt damage');
    pushRate();
    const a = (t.def.strikes || 4) + cur - 1;
    const b = (t.def.strikes || 4) + nxt - 1;
    if (a !== b) lines.push(\`Lightning bolts \${a} → \${b}\`);
    if (cur < 4 && nxt >= 4) lines.push('UNLOCK bolts ignore armour');
  } else if (kind === 'oblivion') {
    pushRange();
    pushShotDmg('Ray damage');
    pushRate();
    lines.push('Always ignores armour');
    if (cur < 3 && nxt >= 3) lines.push('UNLOCK execute on low-HP targets');
    else if (nxt >= 3) {
      const a = Math.round((0.18 + Math.max(0, cur - 3) * 0.04) * 100);
      const b = Math.round((0.18 + Math.max(0, nxt - 3) * 0.04) * 100);
      if (a !== b) lines.push(\`Execute threshold \${a}% → \${b}% HP\`);
    }
  } else {
    pushRange();
    pushShotDmg();
    pushRate();
    pushSplash();
    if (t.def.pellets) {
      const a = t.def.pellets + cur - 1;
      const b = t.def.pellets + nxt - 1;
      if (a !== b) lines.push(\`Pellets \${a} → \${b}\`);
    }
    if (t.def.pierce) {
      const a = t.def.pierce + cur - 1;
      const b = t.def.pierce + nxt - 1;
      if (a !== b) lines.push(\`Pierce \${a} → \${b} enemies\`);
    }
    if (t.def.chains) {
      const a = t.def.chains + cur - 1;
      const b = t.def.chains + nxt - 1;
      if (a !== b) lines.push(\`Chain jumps \${a} → \${b}\`);
    }
    if (t.def.poison) {
      const a = statPoison(now), b = statPoison(next);
      if (a && b) {
        lines.push(\`Poison \${Math.round(a.dps)} → \${Math.round(b.dps)}/s (ignores armour)\`);
        if (Math.abs(a.dur - b.dur) > 0.05) {
          lines.push(\`Poison duration \${a.dur.toFixed(1)}s → \${b.dur.toFixed(1)}s\`);
        }
      }
    }
    if (kind === 'sniper' && cur < 3 && nxt >= 3) {
      lines.push('Armor ignore unlocks');
    }
  }

  return { name, cost, desc, lines };
}
`;

src = src.slice(0, start) + newBlock;
fs.writeFileSync(path, src);
console.log('patched state.js, new length', src.length);
