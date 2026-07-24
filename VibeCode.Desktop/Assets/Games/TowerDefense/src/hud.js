// The DOM heads-up layer: stat chips, the turret shop, and the inspector for a
// selected turret. It polls run state once a frame and only touches the DOM
// when something it displays actually changed.
import { TURRETS, MAX_LEVEL } from './config.js';
import {
  S, upgradeCost, sellValue, statRange, statDmg, statDps, statRate, TARGET_LABEL,
  nextUpgradeName, currentUpgradeName, upgradePreview, statPoison, statBurn, splashScale, slowScale, buffScale,
} from './state.js';
import { drawTurretBody } from './turret-art.js';
import { bakePortrait, formatDamage } from './portraits.js';
import { setMuted, isMuted, unlockAudio, sfxUi } from './audio.js';

const $ = id => document.getElementById(id);
const cards = new Map();
let handlers = {};
let prev = { lives: -1, gold: -1, wave: -1, count: -1, send: '', sig: '' };
/** Stats dropdown starts closed; user opens it with the Stats button. */
let statsOpen = false;
let lastPortraitKey = '';

export function initHud(h) {
  handlers = h;
  buildShop();

  $('sendBtn').addEventListener('click', () => { unlockAudio(); handlers.onSendWave(); });
  $('speedBtn').addEventListener('click', () => handlers.onSpeed());
  $('pauseBtn').addEventListener('click', () => handlers.onPause());
  $('upBtn').addEventListener('click', () => { unlockAudio(); handlers.onUpgrade(); });
  $('sellBtn').addEventListener('click', () => { unlockAudio(); handlers.onSell(); });
  $('targBtn').addEventListener('click', () => handlers.onCycleTarget());

  // Hover the upgrade button → plain-English "what does this rank do?" tip.
  const upBtn = $('upBtn');
  if (upBtn) {
    upBtn.addEventListener('mouseenter', () => showUpgradeTip(true));
    upBtn.addEventListener('mouseleave', () => showUpgradeTip(false));
    upBtn.addEventListener('focus', () => showUpgradeTip(true));
    upBtn.addEventListener('blur', () => showUpgradeTip(false));
  }

  const statsBtn = $('statsToggle');
  if (statsBtn) {
    statsBtn.addEventListener('click', () => {
      statsOpen = !statsOpen;
      applyStatsOpen();
      prev.sig = ''; // force rows refresh if opening
      syncInspect();
    });
  }

  const mute = $('muteBtn');
  if (mute) {
    mute.addEventListener('click', () => {
      setMuted(!isMuted());
      mute.textContent = isMuted() ? 'Muted' : 'Sound';
      mute.classList.toggle('on', !isMuted());
      if (!isMuted()) { unlockAudio(); sfxUi(); }
    });
    mute.classList.toggle('on', !isMuted());
  }

  const closeInfo = $('infoClose');
  if (closeInfo) closeInfo.addEventListener('click', hideTurretInfo);

  const pop = $('infoPop');
  if (pop) {
    // Left-click dim backdrop closes; clicks inside the card do not.
    pop.addEventListener('click', e => {
      if (e.target === pop) hideTurretInfo();
    });
    // Right-click anywhere outside the description card closes it (X stays).
    pop.addEventListener('contextmenu', e => {
      if (e.target.closest('.info-card')) {
        e.preventDefault();
        return;
      }
      e.preventDefault();
      hideTurretInfo();
    });
  }

  // Right-click anywhere else in the UI (HUD, shop empty space, etc.) also dismisses
  // the description — unless the cursor is on the card itself.
  document.addEventListener('contextmenu', e => {
    if (!isTurretInfoOpen()) return;
    if (e.target.closest && e.target.closest('.info-card')) return;
    // Shop tiles open their own description (handled on the card); let that through.
    if (e.target.closest && e.target.closest('#shop .card')) return;
    e.preventDefault();
    hideTurretInfo();
  }, true);
}

function buildShop() {
  const shop = $('shop');
  shop.innerHTML = '';

  for (const def of TURRETS) {
    const card = document.createElement('div');
    card.className = 'card';
    card.style.setProperty('--accent', def.color);
    card.title = `Left-click to build · right-click for what it does (shop only)`;

    const icon = document.createElement('canvas');
    icon.className = 'ic';
    icon.width = 68; icon.height = 68;
    icon.style.width = '34px'; icon.style.height = '34px';
    const ig = icon.getContext('2d');
    ig.scale(2, 2);
    ig.translate(17, 17);
    drawTurretBody(ig, { def, level: 1, angle: -Math.PI / 2, heat: 0.35, pulse: 0.6 }, 0, 0);

    const nm = document.createElement('div');
    nm.className = 'nm';
    nm.textContent = def.name;

    const cost = document.createElement('div');
    cost.className = 'cost';
    cost.textContent = def.cost + 'g';

    card.append(icon, nm, cost);
    card.addEventListener('click', () => handlers.onPickType(def));
    // Right-click a shop tile → what does this tower do?
    card.addEventListener('contextmenu', e => {
      e.preventDefault();
      unlockAudio();
      showTurretInfo(def);
    });
    shop.appendChild(card);
    cards.set(def.id, card);
  }
}

/** Plain-English "what does this do?" card. Works for a def from the shop or a placed tower. */
export function showTurretInfo(def, opts = {}) {
  const pop = $('infoPop');
  if (!pop || !def) return;
  const name = $('infoName');
  const role = $('infoRole');
  const body = $('infoBody');
  const extras = $('infoExtras');
  if (name) {
    name.textContent = def.name;
    name.style.color = def.color;
  }
  if (role) role.textContent = def.role || def.kind;
  if (body) body.textContent = def.blurb || '';

  if (extras) {
    const bits = [];
    bits.push(`Cost ${def.cost}g`);
    if (def.range) bits.push(`Range ${def.range}`);
    if (def.dmg) bits.push(`Damage ${def.dmg}`);
    if (def.dps) bits.push(`DPS ${def.dps}`);
    if (def.rate) bits.push(`${(1 / def.rate).toFixed(1)}/s`);
    if (def.splash) bits.push(`Splash ${def.splash}`);
    if (def.slow) bits.push(`Slow ${Math.round(def.slow * 100)}%`);
    if (def.poison) bits.push(`Poison ${def.poison.dps}/s · ${def.poison.dur}s`);
    if (def.kind === 'flame') bits.push('Napalm burn @ L3');
    if (def.chains) bits.push(`${def.chains} jumps`);
    if (def.pierce) bits.push(`Pierce ${def.pierce}`);
    if (def.pellets) bits.push(`${def.pellets} pellets`);
    if (opts.level) bits.push(`Your level ${opts.level}`);
    extras.textContent = bits.join(' · ');
  }

  pop.classList.remove('hidden');
}

export function hideTurretInfo() {
  const pop = $('infoPop');
  if (pop) pop.classList.add('hidden');
}

export function isTurretInfoOpen() {
  const pop = $('infoPop');
  return !!(pop && !pop.classList.contains('hidden'));
}

export function setSpeedLabel(speed) {
  const b = $('speedBtn');
  b.textContent = speed + '×';
  b.classList.toggle('on', speed > 1);
}

/** Called once a frame. Cheap when nothing changed. */
export function flushHud() {
  if (S.lives !== prev.lives) {
    $('lives').lastElementChild.textContent = S.lives;
    if (S.lives < prev.lives && prev.lives >= 0) replay($('lives'), 'hurt');
    prev.lives = S.lives;
  }
  if (S.gold !== prev.gold) {
    $('gold').lastElementChild.textContent = S.gold;
    if (S.gold > prev.gold + 14) replay($('gold'), 'bump');
    prev.gold = S.gold;
    refreshAffordability();
  }
  if (S.wave !== prev.wave) {
    $('wave').lastElementChild.textContent = S.wave;
    prev.wave = S.wave;
  }
  if (S.enemies.length !== prev.count) {
    $('count').lastElementChild.textContent = S.enemies.length;
    prev.count = S.enemies.length;
  }

  const label = S.inWave
    ? `Wave ${S.wave} incoming`
    : `Send wave &nbsp;<b>${Math.max(0, Math.ceil(S.breakT))}s</b>`;
  if (label !== prev.send) {
    const send = $('sendBtn');
    send.innerHTML = label;
    send.disabled = S.inWave;
    send.style.opacity = S.inWave ? '.5' : '1';
    prev.send = label;
  }

  syncInspect();
}

function refreshAffordability() {
  for (const [id, card] of cards) {
    const def = TURRETS.find(t => t.id === id);
    card.classList.toggle('poor', S.gold < def.cost);
  }
}

export function refreshSelection() {
  for (const [id, card] of cards) {
    card.classList.toggle('sel', S.selectedType && S.selectedType.id === id);
  }
  prev.sig = '';        // force the inspector to redraw for the new selection
  syncInspect();
}

function replay(el, cls) {
  el.classList.remove(cls);
  void el.offsetWidth;
  el.classList.add(cls);
}

function applyStatsOpen() {
  const body = $('statsBody');
  const btn = $('statsToggle');
  if (!body || !btn) return;
  body.classList.toggle('collapsed', !statsOpen);
  btn.classList.toggle('open', statsOpen);
  btn.setAttribute('aria-expanded', statsOpen ? 'true' : 'false');
  const chev = $('statsChev');
  if (chev) chev.textContent = statsOpen ? '▾' : '▸';
}

function paintPortrait(t) {
  const cv = $('insPortrait');
  if (!cv) return;
  const key = `${t.def.id}|${t.level}|${t.def.color}`;
  if (key === lastPortraitKey) return;
  lastPortraitKey = key;
  const img = bakePortrait(t.def.id, t.def.color, t.level, 4);
  const g = cv.getContext('2d');
  g.imageSmoothingEnabled = false;
  g.clearRect(0, 0, cv.width, cv.height);
  g.drawImage(img, 0, 0, cv.width, cv.height);
}

export function syncInspect() {
  const t = S.selected;
  const panel = $('inspect');

  if (!t) {
    if (prev.sig !== 'none') {
      panel.classList.add('hidden');
      prev.sig = 'none';
      lastPortraitKey = '';
      showUpgradeTip(false);
      fillUpgradeTip(null);
    }
    return;
  }

  // Damage dealt ticks every frame while fighting — include a coarse bucket so
  // the number updates without rewriting the whole panel every ms.
  const dealt = Math.floor(t.damageDealt || 0);
  const dealtBucket = dealt < 100 ? dealt : dealt < 1000 ? (dealt / 10 | 0) : (dealt / 100 | 0);
  const sig = `${t.i}|${t.level}|${t.targeting}|${t.dmgMul.toFixed(2)}|${S.gold >= upgradeCost(t)}|${sellValue(t)}|${dealtBucket}|${statsOpen ? 1 : 0}`;
  if (sig === prev.sig) return;
  prev.sig = sig;

  panel.classList.remove('hidden');
  applyStatsOpen();

  $('insName').textContent = t.def.name;
  $('insName').style.color = t.def.color;

  const tier = currentUpgradeName(t);
  const role = t.def.role ? t.def.role + ' · ' : '';
  $('insLvl').textContent = t.level >= MAX_LEVEL
    ? `${role}Level ${t.level} · maxed`
    : tier
      ? `${role}Level ${t.level} · ${tier}`
      : `${role}Level ${t.level}`;

  const dealtEl = $('insDealtVal');
  if (dealtEl) dealtEl.textContent = formatDamage(dealt);

  paintPortrait(t);

  const tip = $('insTip');
  if (tip) tip.textContent = t.def.blurb || '';

  if (statsOpen) {
    const rows = [];
    const push = (k, v) => rows.push(`<span>${k}</span><b>${v}</b>`);
    push('Damage dealt', formatDamage(dealt));
    push('Range', Math.round(statRange(t)));

    if (t.def.kind === 'beam' || t.def.kind === 'flame') {
      push('Damage/s', t.def.kind === 'beam'
        ? `${Math.round(statDps(t))}–${Math.round(statDps(t) * 2)}`
        : Math.round(statDps(t)));
    } else if (t.def.kind === 'aura') {
      push('Slow', Math.round(t.def.slow * slowScale(t.level) * 100) + '%');
      push('Note', 'Utility — no direct damage');
    } else if (t.def.kind === 'buff') {
      const bs = buffScale(t.level);
      push('Boost', '+' + Math.round(t.def.dmgMul * bs * 100) + '% dmg');
      push('Rate boost', '+' + Math.round(t.def.rateMul * bs * 100) + '%');
    } else if (t.def.kind === 'nova') {
      push('Pulse dmg', Math.round(statDmg(t)));
      push('Rate', (1 / statRate(t)).toFixed(1) + '/s');
      push('Blast', Math.round((t.def.splash || statRange(t)) * splashScale(t.level)));
    } else if (t.def.kind === 'singularity') {
      push('Melt DPS', Math.round(statDps(t)));
      push('Slow', Math.round(Math.min(0.88, (t.def.slow || 0.5) * (1 + (t.level - 1) * 0.08)) * 100) + '%');
      if (t.level >= 4) push('Special', 'Ignores armour near core');
    } else if (t.def.kind === 'tempest') {
      push('Bolt damage', Math.round(statDmg(t)));
      push('Bolts', (t.def.strikes || 4) + t.level - 1);
      push('Rate', (1 / statRate(t)).toFixed(1) + '/s');
      if (t.level >= 4) push('Special', 'Bolts ignore armour');
    } else if (t.def.kind === 'oblivion') {
      push('Ray damage', Math.round(statDmg(t)));
      push('Rate', (1 / statRate(t)).toFixed(1) + '/s');
      push('Special', t.level >= 3
        ? `Execute under ${Math.round((0.18 + (t.level - 3) * 0.04) * 100)}% HP`
        : 'Always ignores armour');
    } else {
      const shots = t.def.pellets ? t.def.pellets + t.level - 1 : 1;
      push('Shot damage', Math.round(statDmg(t)));
      if (t.def.rate) {
        push('Rate', (1 / statRate(t)).toFixed(1) + '/s');
        push('DPS', Math.round(statDmg(t) / statRate(t) * shots));
      }
    }
    if (t.def.splash && t.def.kind !== 'nova') {
      push('Splash', Math.round(t.def.splash * splashScale(t.level)));
    }
    const poison = statPoison(t);
    if (poison) {
      push('Poison', `${Math.round(poison.dps)}/s · ${poison.dur.toFixed(1)}s`);
      push('Poison total', `~${Math.round(poison.dps * poison.dur)} (ignores armour)`);
    }
    const burn = statBurn(t);
    if (burn) {
      push('Napalm burn', `${Math.round(burn.dps)}/s · ${burn.dur.toFixed(1)}s`);
    } else if (t.def.kind === 'flame' && t.level < 3) {
      push('Napalm', 'Unlocks at level 3');
    }
    if (t.def.pierce) push('Pierce', t.def.pierce + t.level - 1);
    if (t.def.chains) push('Jumps', t.def.chains + t.level - 1);
    if (t.def.kind === 'sniper' && t.level >= 3) push('Special', 'Ignores armour');
    if (t.def.kind === 'gatling') push('Spin-up', (t.def.spinUp || 1.2).toFixed(1) + 's');
    if (t.dmgMul > 1) push('Amplified', '+' + Math.round((t.dmgMul - 1) * 100) + '%');
    push('Invested', t.invested + 'g');
    push('Sell value', sellValue(t) + 'g');
    const next = nextUpgradeName(t);
    if (next) push('Next rank', next);
    $('insRows').innerHTML = rows.join('');
  }

  const up = $('upBtn');
  if (t.level >= MAX_LEVEL) {
    up.textContent = 'Fully upgraded';
    up.disabled = true;
    up.style.opacity = '.5';
    up.removeAttribute('aria-describedby');
    fillUpgradeTip(null);
  } else {
    const cost = upgradeCost(t);
    const name = nextUpgradeName(t) || 'Upgrade';
    up.textContent = `${name} · ${cost}g`;
    up.disabled = S.gold < cost;
    up.style.opacity = S.gold < cost ? '.5' : '1';
    up.setAttribute('aria-describedby', 'upTip');
    fillUpgradeTip(upgradePreview(t));
  }

  const sell = $('sellBtn');
  sell.textContent = `Sell · ${sellValue(t)}g`;

  const tb = $('targBtn');
  tb.textContent = TARGET_LABEL[t.targeting];
  const noTarget = t.def.kind === 'aura' || t.def.kind === 'buff' || t.def.kind === 'nova'
    || t.def.kind === 'singularity' || t.def.kind === 'tempest';
  tb.style.display = noTarget ? 'none' : '';
}

/** Paint the upgrade hover tip from a preview object (or clear it when maxed). */
function fillUpgradeTip(preview) {
  const tip = $('upTip');
  if (!tip) return;
  if (!preview) {
    tip.innerHTML = '';
    tip.classList.add('empty');
    return;
  }
  tip.classList.remove('empty');
  const desc = preview.desc
    ? `<p class="up-tip-desc">${preview.desc}</p>`
    : '';
  const lines = (preview.lines && preview.lines.length)
    ? `<ul class="up-tip-list">${preview.lines.map(l => `<li>${l}</li>`).join('')}</ul>`
    : '';
  tip.innerHTML =
    `<div class="up-tip-head"><b>${preview.name}</b><span>${preview.cost}g</span></div>` +
    desc +
    lines +
    `<div class="up-tip-foot">Hover only — click the button to buy</div>`;
}

function showUpgradeTip(on) {
  const tip = $('upTip');
  if (!tip || tip.classList.contains('empty')) return;
  tip.classList.toggle('show', !!on);
}

/** A fresh run must not inherit the previous run's diff cache. */
export function resetHud() {
  prev = { lives: -1, gold: -1, wave: -1, count: -1, send: '', sig: '' };
  statsOpen = false;
  lastPortraitKey = '';
  applyStatsOpen();
  hideTurretInfo();
  showUpgradeTip(false);
  fillUpgradeTip(null);
  refreshSelection();
}
