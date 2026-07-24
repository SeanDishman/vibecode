// The simulation: one fixed-timestep tick of the whole board. Pure logic —
// it never draws and never touches the DOM, so it can run headlessly.
import { W, H } from './config.js';
import { PATH_LEN, pathAt } from './path.js';
import { buildQueue, waveBonus } from './waves.js';
import { burst, ring, beamFx, muzzleFlash, updateFx } from './fx.js';
import {
  S, spawn, rehash, nearby, pickTarget, damage, kill, leak,
  statRange, statDmg, statDps, statRate, statPoison, statBurn,
  splashScale, coneScale, slowScale,
} from './state.js';
import { sfxFire, sfxWave } from './audio.js';

const tmp = { x: 0, y: 0, ux: 0, uy: 0 };

export function startWave() {
  S.wave++;
  S.inWave = true;
  S.breakT = 0;
  S.queue = buildQueue(S.wave, S.time);
  sfxWave();
}

/** Calling the next wave in early pays out the time you skipped. */
export function sendWaveNow() {
  if (S.inWave) return 0;
  const bonus = Math.max(0, Math.round(S.breakT * 4));
  S.gold += bonus;
  startWave();
  return bonus;
}

export function step(dt) {
  S.time += dt;
  if (S.shake > 0) S.shake = Math.max(0, S.shake - dt * 22);
  if (S.hurt > 0) S.hurt = Math.max(0, S.hurt - dt * 1.7);

  while (S.queue.length && S.queue[0].at <= S.time) spawn(S.queue.shift().type);

  // A wave "ends" once it has finished spawning, not once the board is clear —
  // the road is long, so waves are meant to overlap and stack up on it.
  if (S.inWave && !S.queue.length) {
    S.inWave = false;
    S.gold += waveBonus(S.wave);
    S.breakT = 8;
  }
  if (!S.inWave) {
    S.breakT -= dt;
    if (S.breakT <= 0) startWave();
  }

  updateEnemies(dt);
  rehash();
  updateTowers(dt);
  updateShots(dt);
  updateFx(dt);
}

// ======================================================================
//  Circles
// ======================================================================
function updateEnemies(dt) {
  const list = S.enemies;
  for (let i = 0; i < list.length; i++) {
    const e = list[i];
    if (e.dead) continue;

    if (e.flash > 0) e.flash -= dt;
    if (e.chillT > 0) { e.chillT -= dt; if (e.chillT <= 0) e.chill = 0; }
    if (e.stunT > 0) { e.stunT -= dt; if (e.stunT < 0) e.stunT = 0; }
    if (e.immune > 0) e.immune -= dt;

    // DoTs bypass armour/shield — that's their niche vs tanks.
    if (e.poisonT > 0) {
      e.poisonT -= dt;
      e.hp -= e.poison * dt;
      if (e.hp <= 0) { kill(e); continue; }
      if (e.poisonT <= 0) e.poison = 0;
    }
    if (e.burnT > 0) {
      e.burnT -= dt;
      e.hp -= e.burn * dt;
      if (e.hp <= 0) { kill(e); continue; }
      if (e.burnT <= 0) e.burn = 0;
    }

    if (e.shieldMax && e.shield < e.shieldMax) {
      e.shieldT += dt;
      if (e.shieldT > 3.2) e.shield = Math.min(e.shieldMax, e.shield + e.shieldMax * 0.45 * dt);
    }

    if (e.def.phases) {
      e.phaseT += dt;
      if (e.immune <= 0 && e.phaseT > 4.2) { e.immune = 0.9; e.phaseT = 0; }
    }

    if (e.def.heals) {
      e.healT += dt;
      if (e.healT > 2.2) {
        e.healT = 0;
        ring(e.x, e.y, '#8affc1', 92, 0.42);
        nearby(e.x, e.y, 92, o => {
          if (o.dead || o === e) return;
          if (Math.hypot(o.x - e.x, o.y - e.y) > 92) return;
          o.hp = Math.min(o.max, o.hp + o.max * 0.10);
        });
      }
    }

    // Stun freezes progress entirely (Tempest L5 shock).
    if (!(e.stunT > 0)) e.d += e.spd * (1 - e.chill) * dt;
    if (e.d >= PATH_LEN) { leak(e); continue; }
    pathAt(e.d, e.lat, tmp);
    e.x = tmp.x; e.y = tmp.y; e.ux = tmp.ux; e.uy = tmp.uy;
  }

  // compact once per tick rather than splicing mid-iteration
  let w = 0;
  for (let i = 0; i < list.length; i++) if (!list[i].dead) list[w++] = list[i];
  list.length = w;
}

// ======================================================================
//  Turrets
// ======================================================================
function updateTowers(dt) {
  for (const t of S.towers) {
    t.pulse += dt;
    if (t.heat > 0) t.heat -= dt * 4;
    const range = statRange(t);
    const kind = t.def.kind;

    if (kind === 'buff') continue;    // amplifiers act through recomputeBuffs

    if (kind === 'aura') {
      const slow = t.def.slow * slowScale(t.level);
      const r2 = range * range;
      let any = false;
      nearby(t.x, t.y, range, e => {
        if (e.dead) return;
        const dx = e.x - t.x, dy = e.y - t.y;
        if (dx * dx + dy * dy > r2) return;
        if (slow > e.chill) e.chill = Math.min(0.85, slow);
        e.chillT = Math.max(e.chillT, 0.2);
        any = true;
      });
      if (any) { t.heat = Math.max(t.heat, 0.4); sfxFire(t.def.id); }
      continue;
    }

    if (kind === 'flame') {
      const tgt = pickTarget(t, range);
      if (!tgt) { t.target = null; t.spin = Math.max(0, (t.spin || 0) - dt); continue; }
      t.target = tgt;
      t.angle = Math.atan2(tgt.y - t.y, tgt.x - t.x);
      t.heat = 1;
      const half = (t.def.cone || 0.55) * coneScale(t.level);
      const dps = statDps(t);
      const burn = statBurn(t); // null below level 3 (Napalm rank)
      const cosA = Math.cos(t.angle), sinA = Math.sin(t.angle);
      nearby(t.x, t.y, range, e => {
        if (e.dead || e.immune > 0) return;
        const dx = e.x - t.x, dy = e.y - t.y;
        const d = Math.hypot(dx, dy);
        if (d > range || d < 6) return;
        // cone test via dot product
        const ndx = dx / d, ndy = dy / d;
        if (ndx * cosA + ndy * sinA < Math.cos(half)) return;
        damage(e, dps * dt * (1 - d / range * 0.25), { from: t });
        // Napalm: enemies keep burning after they walk out of the jet.
        if (burn) {
          e.burn = Math.max(e.burn, burn.dps);
          e.burnT = Math.max(e.burnT, burn.dur);
        }
      });
      sfxFire(t.def.id);
      continue;
    }

    if (kind === 'beam') {
      const held = t.target && !t.target.dead && t.target.immune <= 0 &&
        Math.hypot(t.target.x - t.x, t.target.y - t.y) <= range;
      const tgt = held ? t.target : pickTarget(t, range);
      if (!tgt) { t.target = null; t.charge = Math.max(0, t.charge - dt * 2); continue; }
      if (tgt !== t.target) { t.target = tgt; t.charge = 0; }
      t.charge = Math.min(t.def.ramp, t.charge + dt);
      t.angle = Math.atan2(tgt.y - t.y, tgt.x - t.x);
      damage(tgt, statDps(t) * (1 + t.charge / t.def.ramp) * dt, { ignoreArmor: true, from: t });
      t.heat = 1;
      sfxFire(t.def.id);
      continue;
    }

    if (kind === 'nova') {
      t.cool -= dt;
      if (t.cool > 0) continue;
      // Fire whenever anything is in range — no single target needed.
      let any = false;
      nearby(t.x, t.y, range, e => { if (!e.dead && e.immune <= 0) any = true; });
      if (!any) continue;
      t.cool = statRate(t);
      t.heat = 1;
      const dmg = statDmg(t);
      const splash = (t.def.splash || range) * splashScale(t.level);
      ring(t.x, t.y, t.def.color, splash, 0.32);
      burst(t.x, t.y, t.def.color, 10);
      const r2 = splash * splash;
      nearby(t.x, t.y, splash, e => {
        if (e.dead) return;
        const d2 = (e.x - t.x) ** 2 + (e.y - t.y) ** 2;
        if (d2 > r2) return;
        damage(e, dmg * (1 - 0.35 * Math.sqrt(d2) / splash), { from: t });
      });
      sfxFire(t.def.id);
      continue;
    }

    // Black hole: continuous AoE melt + hard slow; L3 pull, L4 armour-ignore, L5 core pulse.
    if (kind === 'singularity') {
      const slow = Math.min(0.90, (t.def.slow || 0.5) * (1 + (t.level - 1) * 0.09));
      const dps = statDps(t);
      const r2 = range * range;
      let any = false;
      nearby(t.x, t.y, range, e => {
        if (e.dead || e.immune > 0) return;
        const dx = e.x - t.x, dy = e.y - t.y;
        const d2 = dx * dx + dy * dy;
        if (d2 > r2) return;
        any = true;
        const dist = Math.sqrt(d2);
        const fall = 1 - 0.35 * dist / range;
        damage(e, dps * dt * fall, { from: t, ignoreArmor: t.level >= 4 });
        if (slow > e.chill) e.chill = slow;
        e.chillT = Math.max(e.chillT, 0.25);
        // L3+ tidal pull: drag them backward along the path (can't escape the well).
        if (t.level >= 3 && e.d > 0) {
          e.d = Math.max(0, e.d - (18 + t.level * 4) * dt * fall);
        }
      });
      // L5 collapse pulse — big inner spike every ~2.4s
      if (t.level >= 5) {
        t.collapseT = (t.collapseT || 0) - dt;
        if (t.collapseT <= 0 && any) {
          t.collapseT = 2.4;
          const pulseR = range * 0.55;
          const pr2 = pulseR * pulseR;
          const spike = statDps(t) * 1.8;
          ring(t.x, t.y, t.def.color, pulseR, 0.35);
          nearby(t.x, t.y, pulseR, e => {
            if (e.dead) return;
            const d2 = (e.x - t.x) ** 2 + (e.y - t.y) ** 2;
            if (d2 > pr2) return;
            damage(e, spike, { from: t, ignoreArmor: true });
          });
        }
      }
      if (any) {
        t.heat = 1;
        sfxFire(t.def.id);
      }
      continue;
    }

    // Storm: multi-target random lightning; L3 fork, L4 armour-ignore, L5 stun.
    if (kind === 'tempest') {
      t.cool -= dt;
      if (t.cool > 0) continue;
      const candidates = [];
      nearby(t.x, t.y, range, e => {
        if (!e.dead && e.immune <= 0) candidates.push(e);
      });
      if (!candidates.length) continue;
      t.cool = statRate(t);
      t.heat = 1;
      const n = (t.def.strikes || 4) + t.level - 1;
      const dmg = statDmg(t);
      const ignore = t.level >= 4;
      for (let s = 0; s < n && candidates.length; s++) {
        const i = (Math.random() * candidates.length) | 0;
        const e = candidates[i];
        candidates.splice(i, 1);
        beamFx(t.x, t.y, e.x, e.y, t.def.color, 0.14, 2.2);
        damage(e, dmg, { from: t, ignoreArmor: ignore });
        burst(e.x, e.y, t.def.color, 4);
        // L5 shock: brief progress freeze.
        if (t.level >= 5) {
          e.stunT = Math.max(e.stunT || 0, 0.45);
        }
        // L3+ fork to a nearby friend.
        if (t.level >= 3) {
          let fork = null, bd = 70 * 70;
          nearby(e.x, e.y, 70, o => {
            if (o.dead || o === e || o.immune > 0) return;
            const d2 = (o.x - e.x) ** 2 + (o.y - e.y) ** 2;
            if (d2 < bd) { bd = d2; fork = o; }
          });
          if (fork) {
            beamFx(e.x, e.y, fork.x, fork.y, t.def.color, 0.1, 1.4);
            damage(fork, dmg * 0.6, { from: t, ignoreArmor: ignore });
          }
        }
      }
      ring(t.x, t.y, t.def.color, range * 0.35, 0.28);
      sfxFire(t.def.id);
      continue;
    }

    t.cool -= dt;
    const tgt = pickTarget(t, range, t.def.minRange);
    if (!tgt) {
      t.target = null;
      if (kind === 'gatling') t.spin = Math.max(0, (t.spin || 0) - dt * 0.7);
      continue;
    }
    t.target = tgt;

    // lead the shot so fast movers still get hit
    const speed = t.def.speed || 0;
    let aimX = tgt.x, aimY = tgt.y;
    if (speed > 0) {
      const flight = Math.hypot(tgt.x - t.x, tgt.y - t.y) / speed;
      aimX += tgt.ux * tgt.spd * (1 - tgt.chill) * flight;
      aimY += tgt.uy * tgt.spd * (1 - tgt.chill) * flight;
    }
    t.angle = Math.atan2(aimY - t.y, aimX - t.x);

    // Gatling needs spin-up before it dumps rounds.
    if (kind === 'gatling') {
      const need = t.def.spinUp || 1.2;
      t.spin = Math.min(need, (t.spin || 0) + dt);
      if (t.spin < need * 0.55) continue;
      // rate scales with spin fraction
      const spinFrac = t.spin / need;
      if (t.cool > 0) continue;
      t.cool = statRate(t) / (0.45 + spinFrac * 0.7);
      t.heat = 1;
      fire(t, tgt, aimX, aimY);
      continue;
    }

    if (t.cool > 0) continue;
    t.cool = statRate(t);
    t.heat = 1;
    fire(t, tgt, aimX, aimY);
  }
}

function fire(t, tgt, aimX, aimY) {
  const def = t.def, dmg = statDmg(t), muzzle = 16;
  const mx = t.x + Math.cos(t.angle) * muzzle, my = t.y + Math.sin(t.angle) * muzzle;
  muzzleFlash(mx, my, t.angle, def.color);
  sfxFire(def.id);

  switch (def.kind) {
    case 'bullet':
    case 'gatling':
      // Venom poison is scaled per level — never ship the raw level-1 def values.
      S.shots.push(shot(t, mx, my, aimX, aimY, def.speed, { dmg, poison: statPoison(t) }));
      break;

    case 'sniper': {
      // Instant hit — tracers are too slow for a true long rifle.
      const opts = { from: t, ignoreArmor: t.level >= 3 };
      beamFx(mx, my, tgt.x, tgt.y, def.color, 0.1, 1.6);
      damage(tgt, dmg, opts);
      burst(tgt.x, tgt.y, def.color, 4);
      break;
    }

    case 'oblivion': {
      // Death ray: fat beam, always ignores armour; L2+ execute; L4+ split beam.
      const fireRay = (enemy, mult) => {
        if (!enemy || enemy.dead) return;
        beamFx(t.x, t.y, enemy.x, enemy.y, def.color, 0.18 * mult, 3.5 * mult);
        let hit = dmg * mult;
        // Execute unlocks at level 2 (was 3) so ranks feel packed.
        if (t.level >= 2) {
          const thr = 0.16 + Math.max(0, t.level - 2) * 0.04;
          const hpFrac = enemy.hp / Math.max(1, enemy.max);
          if (hpFrac <= thr) hit = Math.max(hit, enemy.hp + (enemy.shield || 0) + 1);
        }
        damage(enemy, hit, { from: t, ignoreArmor: true });
        burst(enemy.x, enemy.y, def.color, Math.round(8 * mult));
      };
      fireRay(tgt, 1);
      beamFx(t.x, t.y, tgt.x, tgt.y, '#ffffff', 0.12, 1.6);
      ring(tgt.x, tgt.y, def.color, 28, 0.3);
      // L4+ secondary ray on next-strongest in range.
      if (t.level >= 4) {
        let second = null, best = -1;
        nearby(t.x, t.y, range, e => {
          if (e.dead || e === tgt || e.immune > 0) return;
          const score = e.hp + e.shield;
          if (score > best) { best = score; second = e; }
        });
        if (second) fireRay(second, 0.55);
      }
      S.shake = Math.max(S.shake, 2.2);
      break;
    }

    case 'shotgun': {
      const n = def.pellets + (t.level - 1);
      for (let i = 0; i < n; i++) {
        const a = t.angle + (Math.random() - 0.5) * def.spread;
        S.shots.push(shot(t, mx, my, mx + Math.cos(a) * 200, my + Math.sin(a) * 200,
          def.speed * (0.85 + Math.random() * 0.3), { dmg, life: statRange(t) / def.speed }));
      }
      break;
    }

    case 'shell':
      S.shots.push(shot(t, mx, my, aimX, aimY, def.speed,
        { dmg, splash: def.splash * splashScale(t.level) }));
      break;

    case 'missile': {
      const n = t.level >= 3 ? 2 : 1;
      for (let i = 0; i < n; i++) {
        const s = shot(t, mx, my, aimX, aimY, def.speed, { dmg, splash: def.splash, homing: tgt });
        s.vx += (Math.random() - 0.5) * 120;
        s.vy += (Math.random() - 0.5) * 120;
        S.shots.push(s);
      }
      break;
    }

    case 'mortar': {
      const flight = Math.hypot(aimX - t.x, aimY - t.y) / 420 + 0.35;
      S.shots.push({
        kind: 'mortar', x: t.x, y: t.y, sx: t.x, sy: t.y,
        tx: tgt.x + tgt.ux * tgt.spd * (1 - tgt.chill) * flight,
        ty: tgt.y + tgt.uy * tgt.spd * (1 - tgt.chill) * flight,
        t: 0, dur: flight, dmg, splash: def.splash * splashScale(t.level),
        color: def.color, from: t,
      });
      S.shake = Math.max(S.shake, 2);
      break;
    }

    case 'rail': {
      const dx = Math.cos(t.angle), dy = Math.sin(t.angle);
      const len = statRange(t);
      beamFx(t.x, t.y, t.x + dx * len, t.y + dy * len, def.color, 0.16, 3);
      const seen = [];
      nearby(t.x + dx * len / 2, t.y + dy * len / 2, len / 2 + 40, e => {
        if (e.dead || e.immune > 0) return;
        const rel = (e.x - t.x) * dx + (e.y - t.y) * dy;
        if (rel < 0 || rel > len) return;
        if (Math.abs(-(e.x - t.x) * dy + (e.y - t.y) * dx) > e.r + 5) return;
        seen.push({ rel, e });
      });
      seen.sort((a, b) => a.rel - b.rel);
      const max = def.pierce + t.level - 1;
      for (let i = 0; i < seen.length && i < max; i++) {
        damage(seen[i].e, dmg, { ignoreArmor: true, from: t });
      }
      S.shake = Math.max(S.shake, 1.5);
      break;
    }

    case 'chain': {
      let from = t, cur = tgt, hop = 0;
      const hit = new Set();
      const jumps = def.chains + (t.level - 1);
      while (cur && hop < jumps) {
        beamFx(from.x, from.y, cur.x, cur.y, def.color, 0.12, 2 - hop * 0.15);
        damage(cur, dmg * Math.pow(0.85, hop), { ignoreArmor: true, from: t });
        hit.add(cur);
        const prev = cur;
        let next = null, bd = 110 * 110;
        nearby(prev.x, prev.y, 110, e => {
          if (e.dead || hit.has(e) || e.immune > 0) return;
          const d2 = (e.x - prev.x) ** 2 + (e.y - prev.y) ** 2;
          if (d2 < bd) { bd = d2; next = e; }
        });
        from = prev; cur = next; hop++;
      }
      break;
    }
  }
}

function shot(t, x, y, tx, ty, speed, opts) {
  const a = Math.atan2(ty - y, tx - x);
  return {
    kind: 'shot', x, y, vx: Math.cos(a) * speed, vy: Math.sin(a) * speed,
    dmg: opts.dmg, splash: opts.splash || 0, poison: opts.poison || null,
    homing: opts.homing || null, color: t.def.color,
    life: opts.life || 2.4, r: opts.splash ? 3.2 : 2,
    from: t,
  };
}

// ======================================================================
//  Projectiles
// ======================================================================
function updateShots(dt) {
  for (let i = S.shots.length - 1; i >= 0; i--) {
    const s = S.shots[i];

    if (s.kind === 'mortar') {
      s.t += dt;
      const k = Math.min(1, s.t / s.dur);
      s.x = s.sx + (s.tx - s.sx) * k;
      s.y = s.sy + (s.ty - s.sy) * k;
      if (k >= 1) { explode(s.x, s.y, s.dmg, s.splash, s.color, s.from); S.shots.splice(i, 1); }
      continue;
    }

    s.life -= dt;
    if (s.life <= 0) { S.shots.splice(i, 1); continue; }

    if (s.homing) {
      if (s.homing.dead) s.homing = null;
      else {
        const want = Math.atan2(s.homing.y - s.y, s.homing.x - s.x);
        const sp = Math.hypot(s.vx, s.vy);
        const cur = Math.atan2(s.vy, s.vx);
        let da = want - cur;
        while (da > Math.PI) da -= Math.PI * 2;
        while (da < -Math.PI) da += Math.PI * 2;
        const na = cur + Math.max(-4.5 * dt, Math.min(4.5 * dt, da));
        s.vx = Math.cos(na) * sp; s.vy = Math.sin(na) * sp;
      }
    }

    const px = s.x, py = s.y;
    s.x += s.vx * dt; s.y += s.vy * dt;
    if (s.x < -40 || s.y < -40 || s.x > W + 40 || s.y > H + 40) { S.shots.splice(i, 1); continue; }

    const impact = sweep(s, px, py);
    if (!impact) continue;

    if (s.splash) {
      explode(px + (s.x - px) * impact.t, py + (s.y - py) * impact.t, s.dmg, s.splash, s.color, s.from);
    } else {
      damage(impact.e, s.dmg, { from: s.from });
      if (s.poison) {
        impact.e.poison = Math.max(impact.e.poison, s.poison.dps);
        impact.e.poisonT = Math.max(impact.e.poisonT, s.poison.dur);
      }
      burst(impact.e.x, impact.e.y, s.color, 2);
    }
    S.shots.splice(i, 1);
  }
}

/** Swept circle test: a fast tracer can step straight over a 3px circle. */
function sweep(s, px, py) {
  let hit = null, bestT = 2;
  const mx = s.x - px, my = s.y - py;
  nearby((px + s.x) / 2, (py + s.y) / 2, Math.hypot(mx, my) / 2 + 24, e => {
    if (e.dead || e.immune > 0) return;
    const rr = e.r + s.r + 1.5;
    const fx = px - e.x, fy = py - e.y;
    const A = mx * mx + my * my;
    const B = 2 * (fx * mx + fy * my);
    const C = fx * fx + fy * fy - rr * rr;
    if (A < 1e-6) { if (C <= 0 && bestT > 0) { bestT = 0; hit = e; } return; }
    const disc = B * B - 4 * A * C;
    if (disc < 0) return;
    const sq = Math.sqrt(disc);
    let t0 = (-B - sq) / (2 * A);
    if (t0 < 0) t0 = (-B + sq) / (2 * A);
    if (t0 < 0 || t0 > 1) return;
    if (t0 < bestT) { bestT = t0; hit = e; }
  });
  return hit ? { e: hit, t: bestT } : null;
}

function explode(x, y, dmg, radius, color, from) {
  ring(x, y, color, radius, 0.34);
  burst(x, y, color, 12);
  const r2 = radius * radius;
  nearby(x, y, radius, e => {
    if (e.dead) return;
    const d2 = (e.x - x) ** 2 + (e.y - y) ** 2;
    if (d2 > r2) return;
    damage(e, dmg * (1 - 0.45 * Math.sqrt(d2) / radius), { from });
  });
  S.shake = Math.max(S.shake, 2.5);
}
