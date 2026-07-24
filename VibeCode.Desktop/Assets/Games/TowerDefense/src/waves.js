// Wave composition. Pure: given a wave number, say what shows up and when.
// Early waves teach each enemy type; mid game stacks them; late game piles
// denser gaps, extra elite trains, and more frequent bosses so supers can't
// afk-clear the road forever.

export function waveGroups(w) {
  const g = [];
  // Count scale: linear early, modest late bump. HP carries most of the pain so
  // we don't spawn a thousand circles and melt the frame budget.
  const n = k => {
    const early = 1 + w * 0.12;
    const late = w > 15 ? 1 + (w - 15) * 0.028 : 1;
    return Math.max(1, Math.round(k * early * late));
  };
  // Spawn spacing tightens late — less free shoot time between circles.
  const gap = base => Math.max(0.06, base * (1 - Math.min(0.55, (w - 1) * 0.012)));

  g.push({ type: 'grunt', count: n(9), gap: gap(0.34), delay: 0 });
  if (w >= 3) g.push({ type: 'runner', count: n(4), gap: gap(0.22), delay: 2.2 });
  if (w >= 5) g.push({ type: 'swarm', count: n(14), gap: gap(0.10), delay: 4.0 });
  if (w >= 7) g.push({ type: 'armor', count: n(3), gap: gap(0.50), delay: 5.5 });
  if (w >= 9) g.push({ type: 'shield', count: n(3), gap: gap(0.45), delay: 7.5 });
  if (w >= 11) g.push({ type: 'split', count: n(3), gap: gap(0.55), delay: 5.0 });
  if (w >= 13) g.push({ type: 'medic', count: Math.max(1, n(1.4)), gap: gap(1.0), delay: 6.8 });
  if (w >= 15) g.push({ type: 'phase', count: n(2.8), gap: gap(0.42), delay: 8.5 });

  // Tanks: more often and more of them after mid game.
  if (w >= 8) {
    if (w % 3 === 0 || w >= 20) {
      const tanks = 1 + Math.floor(w / 10) + (w >= 25 ? Math.floor((w - 25) / 8) : 0);
      g.push({ type: 'tank', count: tanks, gap: gap(1.35), delay: 9.5 });
    }
  }

  // Late-game pressure packs — extra elites so gold-rich boards still sweat.
  if (w >= 18) {
    g.push({ type: 'armor', count: n(2.5), gap: gap(0.38), delay: 12.0 });
    g.push({ type: 'runner', count: n(5), gap: gap(0.16), delay: 12.5 });
  }
  if (w >= 22) {
    g.push({ type: 'shield', count: n(3), gap: gap(0.35), delay: 14.0 });
    g.push({ type: 'swarm', count: n(12), gap: gap(0.07), delay: 14.2 });
  }
  if (w >= 28) {
    g.push({ type: 'medic', count: Math.max(2, n(1.4)), gap: gap(0.85), delay: 15.5 });
    g.push({ type: 'phase', count: n(2.8), gap: gap(0.32), delay: 16.0 });
    g.push({ type: 'tank', count: 1 + Math.floor(w / 20), gap: gap(1.1), delay: 16.5 });
  }
  if (w >= 35) {
    // Extra elite pressure without doubling the whole parade again.
    g.push({ type: 'split', count: n(3), gap: gap(0.4), delay: 3.0 });
    g.push({ type: 'armor', count: n(2.5), gap: gap(0.32), delay: 18.0 });
    g.push({ type: 'runner', count: n(5), gap: gap(0.14), delay: 18.2 });
  }

  // Boss cadence: every 10 early, every 8 after 20, every 6 after 30; doubles then triples.
  const bossEvery = w >= 30 ? 6 : w >= 20 ? 8 : 10;
  if (w >= 10 && w % bossEvery === 0) {
    const bosses = w >= 50 ? 3 : w >= 35 ? 2 : 1;
    g.push({ type: 'boss', count: bosses, gap: Math.max(2.2, gap(3.5)), delay: 2.5 });
  }
  // Mini-boss pressure: a tank "herald" wave on boss-1 for late game.
  if (w >= 24 && (w + 1) % bossEvery === 0) {
    g.push({ type: 'tank', count: 2 + Math.floor(w / 20), gap: gap(1.0), delay: 1.0 });
  }

  return g;
}

/** Every circle in a wave, flattened to a spawn schedule. */
export function buildQueue(w, now) {
  const queue = [];
  for (const grp of waveGroups(w)) {
    for (let i = 0; i < grp.count; i++) {
      queue.push({ type: grp.type, at: now + grp.delay + i * grp.gap });
    }
  }
  queue.sort((a, b) => a.at - b.at);
  return queue;
}

/** Gold reward per cleared wave — grows, but slower late so economy doesn't outrun HP forever. */
export const waveBonus = w => {
  const base = 28 + w * 7;
  // Soft-cap extra income after wave 20 so 7k gold by wave 27 is harder.
  if (w <= 20) return base;
  return Math.round(28 + 20 * 7 + (w - 20) * 4.5);
};
