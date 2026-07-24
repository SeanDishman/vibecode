// ui.js — every DOM panel: the top bar, the build/train rail, the selection
// panel (including the city readout the player clicks for), and the tech tree.
// Reads game state and writes HTML; it never advances the simulation itself.

import { W, clamp, shortNum, signed, formatYear } from './core.js';
import { world, game, camera, markDirty, cityAt } from './store.js';
import {
  BUILDINGS, BLD, UNITS, UNI, TECHS, TECH, ERAS, TERRAIN, RESOURCES,
} from './data.js';
import {
  canPlace, canBuildInCity, canTrain, cityHas, growthCost, foodSurplus,
  recomputeCity, recomputeEmpire, hasTech, techAvailable, startResearch,
  tileBuildings, cityBuildings, cityCoveringTile, seesOil,
} from './economy.js';
import { addBuilding, addUnit, cityRoles } from './entities.js';
import { orderFortify } from './military.js';
import {
  fortifyBorder, borderTilesVs, foeFacingTile, isEnemyBorderTile,
} from './construction.js';
import { researchEta, CYCLE } from './turn.js';

/** Yields are authored per cycle; the player sees a plain per-second rate. */
function rateStr(perCycle) {
  const r = perCycle / CYCLE;
  const a = Math.abs(r);
  const num = a >= 10 ? String(Math.round(r)) : r.toFixed(1);
  return (r > 0 ? '+' : '') + num;
}
import { bakeBld, bakeUnit } from './sprites.js';
import { logEvent } from './log.js';

const $ = id => document.getElementById(id);
let el = {};

/* ── setup ────────────────────────────────────────────────────────────────── */

export function initUI() {
  el = {
    hud: $('hud'), crest: $('crest'), empireName: $('empireName'),
    rGold: $('rGold'), rGoldRate: $('rGoldRate'),
    rFood: $('rFood'), rFoodRate: $('rFoodRate'),
    rSci: $('rSci'), rSciRate: $('rSciRate'),
    oilRes: $('oilRes'), rOil: $('rOil'), rOilRate: $('rOilRate'),
    rCities: $('rCities'), rPop: $('rPop'), rUnits: $('rUnits'),
    era: $('eraLabel'),
    // dock
    btnBuild: $('btnBuild'), btnCity: $('btnCity'), btnResearch: $('btnResearch'),
    btnCityLabel: $('btnCityLabel'),
    researchName: $('dockResName'), researchBar: $('dockResBar'), researchEta: $('dockResEta'),
    buildPanel: $('buildPanel'), cityPanel: $('cityPanel'),
    buildList: $('buildList'), trainHead: $('trainHead'), trainList: $('trainList'),
    railHint: $('railHint'), trainHint: $('trainHint'),
    selPanel: $('selPanel'), selTitle: $('selTitle'), selBody: $('selBody'),
    tech: $('tech'), techGrid: $('techGrid'), techEra: $('techEra'),
    techSci: $('techSci'), techRate: $('techRate'),
    buildHint: $('buildHint'),
  };
  buildTechTree();
  wireDock();
}

/* ── the dock ─────────────────────────────────────────────────────────────── */

/** Which popover is open, if any: 'buildPanel' | 'cityPanel' | null. */
let openPanel = null;

export function togglePanel(id) {
  // Opening a panel closes the other — two 300px popovers side by side would
  // cover half the map.
  openPanel = openPanel === id ? null : id;
  syncPanels();
}
export function closePanels() { openPanel = null; syncPanels(); }

function syncPanels() {
  el.buildPanel.classList.toggle('hidden', openPanel !== 'buildPanel');
  el.cityPanel.classList.toggle('hidden', openPanel !== 'cityPanel');
  el.btnBuild.classList.toggle('on', openPanel === 'buildPanel');
  el.btnCity.classList.toggle('on', openPanel === 'cityPanel');
  if (openPanel) refreshRails();
}

function wireDock() {
  el.btnBuild.addEventListener('click', () => togglePanel('buildPanel'));
  el.btnCity.addEventListener('click', () => {
    if (el.btnCity.disabled) return;
    togglePanel('cityPanel');
  });
  el.btnResearch.addEventListener('click', () => { closePanels(); toggleTech(); });
  document.querySelectorAll('.popx').forEach(b =>
    b.addEventListener('click', () => { openPanel = null; syncPanels(); }));
}

/** The City button only makes sense with one of your own cities selected. */
function syncCityButton() {
  const e = game.empires[game.player];
  const c = game.sel.kind === 'city' ? game.cities[game.sel.idx] : null;
  const mine = !!(c && !c.dead && e && c.owner === e.i);
  el.btnCity.disabled = !mine;
  el.btnCityLabel.textContent = mine ? c.name : 'City';
  if (!mine && openPanel === 'cityPanel') { openPanel = null; syncPanels(); }
}

export const showHUD = on => { if (el.hud) el.hud.style.display = on ? '' : 'none'; };

/* ── top bar ──────────────────────────────────────────────────────────────── */

export function refreshHUD() {
  const e = game.empires[game.player];
  if (!e || !el.rGold) return;

  el.crest.style.background = e.col;
  el.crest.style.color = e.col;
  el.empireName.textContent = e.name;
  el.empireName.style.color = e.col;

  el.rGold.textContent = shortNum(e.gold);
  el.rGoldRate.textContent = rateStr(e.incGold);
  el.rGoldRate.className = e.incGold >= 0 ? 'up' : 'down';

  el.rFood.textContent = rateStr(e.incFood);
  el.rFoodRate.textContent = 'food';
  el.rFoodRate.className = e.incFood >= 0 ? 'up' : 'down';

  el.rSci.textContent = shortNum(e.sci);
  el.rSciRate.textContent = rateStr(e.incSci);
  el.rSciRate.className = 'up';

  if (seesOil(e)) {
    el.oilRes.style.display = '';
    el.rOil.textContent = shortNum(e.oil || 0);
    el.rOilRate.textContent = e.dry ? 'DRY' : rateStr(e.incOil || 0);
    el.rOilRate.className = e.dry ? 'down' : (e.incOil >= 0 ? 'up' : 'down');
  } else {
    el.oilRes.style.display = 'none';
  }

  let cities = 0, pop = 0, units = 0;
  for (const c of game.cities) if (!c.dead && c.owner === e.i) { cities++; pop += cityRoles(c).total; }
  for (const u of game.units) if (!u.dead && u.owner === e.i) units++;
  el.rCities.textContent = cities;
  el.rPop.textContent = pop;
  el.rUnits.textContent = `${units}/${e.unitCap}`;

  el.era.textContent = `${ERAS[e.era]} · ${formatYear(game.year)}`;
  syncCityButton();

  if (e.researching) {
    const t = TECH[e.researching];
    const pct = clamp(e.sciInto / t.cost, 0, 1) * 100;
    el.researchName.textContent = t.name;
    el.researchBar.style.width = pct.toFixed(1) + '%';
    const eta = researchEta(e);
    el.researchEta.textContent = eta === Infinity ? '—' : `${eta}s`;
  } else {
    el.researchName.textContent = 'Choose research';
    el.researchBar.style.width = '0%';
    el.researchEta.textContent = '';
  }
  // Nudge the button when nothing is being researched — this is exactly the
  // thing players were missing when it lived as a pill in the top corner.
  el.btnResearch.classList.toggle('idle', !e.researching);
}

/* ── build / train rail ───────────────────────────────────────────────────── */

function glyph(canvas) {
  const d = document.createElement('div');
  d.className = 'glyph';
  canvas.style.width = canvas.width + 'px';
  canvas.style.height = canvas.height + 'px';
  d.appendChild(canvas);
  return d;
}

function itemRow({ img, name, sub, cost, ok, why, selected, onClick }) {
  const b = document.createElement('button');
  b.className = 'item' + (ok ? '' : ' locked') + (selected ? ' sel' : '');
  b.appendChild(glyph(img));

  const nm = document.createElement('div');
  nm.className = 'nm';
  const t = document.createElement('b'); t.textContent = name;
  const s = document.createElement('span'); s.textContent = ok ? sub : why;
  nm.append(t, s);
  b.appendChild(nm);

  if (cost != null) {
    const c = document.createElement('span');
    c.className = 'cost';
    c.textContent = cost + 'g';
    b.appendChild(c);
  }
  b.title = ok ? sub : why;
  if (ok) b.addEventListener('click', onClick);
  return b;
}

export function refreshRails() {
  const e = game.empires[game.player];
  if (!e || !el.buildList) return;

  /* tile buildings — placed by clicking the map, grouped by era.
     Locked entries are listed too, showing the tech that opens them: the
     buildings list is where most players will discover what research is for. */
  el.buildList.innerHTML = '';
  let lastEra = -1;
  for (const d of tileBuildings(e)) {
    if (d.era !== lastEra) {
      lastEra = d.era;
      const h = document.createElement('div');
      h.className = 'grouphead';
      h.textContent = ERAS[d.era] || 'Other';
      el.buildList.appendChild(h);
    }
    const unlocked = hasTech(e, d.tech);
    el.buildList.appendChild(itemRow({
      img: bakeBld(d.id, unlocked ? e.col : '#5b6376', 1),
      name: d.name,
      sub: d.desc,
      cost: d.cost,
      ok: unlocked,
      why: unlocked ? '' : 'Research ' + TECH[d.tech].name,
      selected: game.place === d.id,
      onClick: () => setPlace(game.place === d.id ? null : d.id),
    }));
    if (unlocked && e.gold < d.cost) el.buildList.lastChild.classList.add('poor');
  }
  el.railHint.textContent = game.place ? 'click a tile · Esc cancels' : 'pick one, then click a tile';

  /* the selected city's own buildings and units */
  el.trainList.innerHTML = '';
  const c = game.sel.kind === 'city' ? game.cities[game.sel.idx] : null;
  const mine = c && !c.dead && c.owner === e.i;

  if (!mine) {
    el.trainHint.textContent = 'select a city';
    el.trainHead.textContent = 'City';
    const hint = document.createElement('div');
    hint.style.cssText = 'padding:8px 7px;color:var(--faint);font-size:10px;line-height:1.45';
    hint.textContent = 'Click one of your cities on the map to build inside it and train units.';
    el.trainList.appendChild(hint);
    return;
  }

  el.trainHead.textContent = c.name;
  el.trainHint.textContent = `pop ${c.pop}`;

  for (const d of cityBuildings()) {
    if (!hasTech(e, d.tech)) continue;
    if (cityHas(c, d.id)) continue;
    const chk = canBuildInCity(e, c, d.id);
    el.trainList.appendChild(itemRow({
      img: bakeBld(d.id, chk.ok ? e.col : '#5b6376', 1),
      name: d.name, sub: d.desc, cost: d.cost,
      ok: chk.ok, why: chk.why,
      onClick: () => {
        const again = canBuildInCity(e, c, d.id);
        if (!again.ok) { logEvent(again.why, 'info'); return; }
        e.gold -= d.cost;
        addBuilding(e, d.id, c.x, c.y, c);
        logEvent(`${d.name} ordered in ${c.name} — labourers are raising it.`, 'good');
        refreshRails(); refreshSelection();
      },
    }));
  }

  const sep = document.createElement('div');
  sep.style.cssText = 'margin:5px 6px 3px;font:700 8.5px/1 var(--mono);letter-spacing:.14em;' +
                      'text-transform:uppercase;color:var(--faint)';
  sep.textContent = 'Train';
  el.trainList.appendChild(sep);

  for (const d of UNITS) {
    if (!hasTech(e, d.tech)) continue;
    if (d.sea && !c.canShips) continue;
    const chk = canTrain(e, c, d.id);
    const sub = d.role === 'settler' ? d.desc
      : `${d.atk} atk · ${d.hp} hp${d.range ? ' · range ' + d.range : ''} · ${d.pop} pop`;
    el.trainList.appendChild(itemRow({
      img: bakeUnit(d.id, chk.ok ? e.col : '#5b6376', 1),
      name: d.name, sub, cost: d.cost,
      ok: chk.ok, why: chk.why,
      onClick: () => {
        const again = canTrain(e, c, d.id);
        if (!again.ok) { logEvent(again.why, 'info'); return; }
        e.gold -= d.cost;
        if (d.oil) e.oil = Math.max(0, (e.oil || 0) - d.oil);
        c.pop -= d.pop;
        addUnit(e, d.id, c.x, c.y, c);
        recomputeCity(c); recomputeEmpire(e);
        logEvent(`${d.name} trained in ${c.name}.`, 'good');
        refreshRails(); refreshSelection();
      },
    }));
  }
}

export function setPlace(id) {
  game.place = id;
  const hint = el.buildHint;
  if (id) {
    hint.innerHTML = `Placing <b style="color:var(--cyan)">${BLD[id].name}</b> — click a tile inside your borders · <kbd>Esc</kbd> cancels`;
    hint.classList.add('show');
  } else {
    hint.classList.remove('show');
  }
  refreshRails();
}

/* ── selection panel ──────────────────────────────────────────────────────── */

const kv = (k, v, cls = '') => `<div class="kv"><span>${k}</span><b class="${cls}">${v}</b></div>`;

/** How dangerous a rival looks, from its power tier and martial appetite. */
function threatWord(e) {
  const s = e.power.mult * e.mult.army;
  if (s < 0.5) return { label: 'Easy prey', cls: 'low' };
  if (s < 0.85) return { label: 'Weak', cls: 'low' };
  if (s < 1.3) return { label: 'Evenly matched', cls: 'mid' };
  if (s < 1.9) return { label: 'Dangerous', cls: 'high' };
  return { label: 'Formidable', cls: 'high' };
}

export function refreshSelection() {
  const sel = game.sel;
  if (!el.selPanel) return;
  const e = game.empires[game.player];

  if (sel.units.length > 1) { renderUnitStack(sel); return; }
  if (sel.units.length === 1) { renderUnit(game.units[sel.units[0]]); return; }
  if (sel.kind === 'city') { renderCity(game.cities[sel.idx]); return; }
  if (sel.kind === 'tile') { renderTile(sel.idx); return; }
  el.selPanel.classList.remove('show');
}

function panel(titleHTML, bodyHTML) {
  el.selTitle.innerHTML = titleHTML;
  el.selBody.innerHTML = bodyHTML;
  el.selPanel.classList.add('show');
}

function renderCity(c) {
  if (!c || c.dead) { el.selPanel.classList.remove('show'); return; }
  const emp = game.empires[c.owner];
  const r = cityRoles(c);
  const need = growthCost(c.pop);
  const surplus = foodSurplus(c);
  const growPct = clamp(c.food / need, 0, 1) * 100;
  const capped = c.pop >= c.housing;

  const dot = `<span style="width:8px;height:8px;border-radius:2px;background:${emp.col};display:inline-block"></span>`;
  const title = `${dot}<span>${c.name}</span>` +
    `<span class="tag">${c.capital ? 'Capital' : ['Hamlet', 'Town', 'City', 'Metropolis'][c.stage | 0]}</span>`;

  // The population breakdown is the headline number for this panel.
  let body = `
    <div class="kv" style="gap:4px">
      <span>Population</span>
      <b style="font-size:13px;color:var(--cyan)">${r.total}</b>
      <span style="margin-left:auto;font-size:9.5px;color:var(--faint)">
        housing ${c.pop}/${c.housing}</span>
    </div>
    <div class="rolerow">
      <div class="role"><b>${r.civilians}</b><span>civilians</span></div>
      <div class="role"><b>${r.fighters}</b><span>fighters</span></div>
      <div class="role"><b>${r.settlers}</b><span>settlers</span></div>
    </div>`;

  body += `<div class="kv"><span>Growth</span><b>${capped ? 'housing full' : signed(Math.round(surplus)) + ' food'}</b></div>`;
  body += `<div class="hpbar"><span style="width:${growPct}%"></span></div>`;
  body += `<div style="font-size:9.5px;color:var(--faint);margin-top:-2px">${Math.round(c.food)} / ${need} to the next citizen</div>`;

  body += `<div class="kv" style="margin-top:2px">
      <span>Yield</span>
      <b>🪙 ${Math.round(c.yields.gold)}</b><b>🌾 ${Math.round(c.yields.food)}</b><b>🔬 ${Math.round(c.yields.sci)}</b>
    </div>`;

  const hpPct = clamp(c.hp / c.maxHp, 0, 1) * 100;
  body += `<div class="kv"><span>Defence</span><b>${Math.round(c.hp)}/${Math.round(c.maxHp)}</b>
      <span style="margin-left:auto;font-size:9.5px;color:var(--faint)">+${Math.round(c.defBonus * 100)}%</span></div>`;
  body += `<div class="hpbar${hpPct < 60 ? ' hurt' : ''}"><span style="width:${hpPct}%"></span></div>`;

  const names = c.blds.map(i => game.buildings[i]).filter(b => b && !b.dead).map(b => {
    const n = BLD[b.id].name;
    if (b.phase === 'strip') return `${n} (stripping)`;
    if ((b.progress ?? 1) < 1) return `${n} (${Math.round(b.progress * 100)}%)`;
    return n;
  });
  body += `<div style="font-size:9.5px;color:var(--faint);line-height:1.4;margin-top:3px">
      ${names.length ? names.join(' · ') : 'No buildings yet'}</div>`;

  if (c.owner !== game.player) {
    // Who am I dealing with? This is how the player picks a soft first target.
    if (emp.trait && emp.power) {
      const strength = threatWord(emp);
      body += `<div class="foreign">
          <b style="color:${emp.col}">${emp.name}</b>
          <span>${emp.power.name} · ${emp.trait.name}</span>
          <span class="blurb">${emp.trait.blurb}</span>
          <span class="threat ${strength.cls}">${strength.label}</span>
        </div>`;
    }
    body += `<div style="font-size:10px;color:var(--pink);margin-top:4px">
      Right-click with troops selected to attack.</div>`;
  }
  panel(title, body);
}

function renderUnit(u) {
  if (!u || u.dead) { el.selPanel.classList.remove('show'); return; }
  const emp = game.empires[u.owner];
  const d = u.def;
  const home = u.home >= 0 && game.cities[u.home] ? game.cities[u.home].name : '—';
  const dot = `<span style="width:8px;height:8px;border-radius:2px;background:${emp.col};display:inline-block"></span>`;
  const state = u.embarked ? 'at sea' : u.state === 'attack' ? 'fighting'
    : u.state === 'move' ? 'marching' : u.state === 'fortify' ? 'fortified' : 'idle';

  let body = `<div class="kv"><span>Health</span><b>${Math.round(u.hp)}/${u.maxHp}</b>
      <span style="margin-left:auto;font-size:9.5px;color:var(--faint)">${state}</span></div>`;
  body += `<div class="hpbar${u.hp < u.maxHp * 0.4 ? ' hurt' : ''}"><span style="width:${clamp(u.hp / u.maxHp, 0, 1) * 100}%"></span></div>`;
  if (d.atk > 0) {
    body += kv('Strength', Math.round(u.atk) + (d.range ? ` · range ${d.range}` : ' · melee'));
  }
  body += kv('From', home);
  if (d.desc) body += `<div style="font-size:9.5px;color:var(--faint);line-height:1.4">${d.desc}</div>`;

  if (u.owner === game.player) {
    body += `<div class="actions">`;
    if (d.role === 'settler') body += `<button class="abtn" data-act="found">Found city here</button>`;
    if (d.atk > 0) body += `<button class="abtn" data-act="fortify">Fortify</button>`;
    body += `<button class="abtn" data-act="center">Centre view</button></div>`;
  }
  panel(`${dot}<span>${d.name}</span><span class="tag">${emp.name}</span>`, body);
  wireUnitActions(u);
}

function wireUnitActions(u) {
  el.selBody.querySelectorAll('[data-act]').forEach(b => {
    b.addEventListener('click', () => {
      const act = b.dataset.act;
      if (act === 'fortify') { orderFortify(u); refreshSelection(); }
      else if (act === 'center') { camera.x = u.x; camera.y = u.y; }
      else if (act === 'found') {
        // Handled by military.js next tick: standing order to settle right here.
        u.order = { kind: 'found', x: Math.floor(u.x), y: Math.floor(u.y) };
        u.path = null; u.state = 'idle';
      }
    });
  });
}

function renderUnitStack(sel) {
  const units = sel.units.map(i => game.units[i]).filter(u => u && !u.dead);
  if (!units.length) { el.selPanel.classList.remove('show'); return; }
  const emp = game.empires[units[0].owner];
  const counts = new Map();
  let hp = 0, maxHp = 0, atk = 0;
  for (const u of units) {
    counts.set(u.def.name, (counts.get(u.def.name) || 0) + 1);
    hp += u.hp; maxHp += u.maxHp; atk += u.atk;
  }
  const list = [...counts].map(([n, c]) => `${c}× ${n}`).join(' · ');
  let body = `<div class="kv"><span>Army health</span><b>${Math.round(hp)}/${Math.round(maxHp)}</b></div>`;
  body += `<div class="hpbar"><span style="width:${clamp(hp / maxHp, 0, 1) * 100}%"></span></div>`;
  body += kv('Combined strength', Math.round(atk));
  body += `<div style="font-size:9.5px;color:var(--faint);line-height:1.45;margin-top:2px">${list}</div>`;
  body += `<div style="font-size:10px;color:var(--muted);margin-top:3px">Right-click a target to send them all.</div>`;
  const dot = `<span style="width:8px;height:8px;border-radius:2px;background:${emp.col};display:inline-block"></span>`;
  panel(`${dot}<span>${units.length} units</span><span class="tag">Army</span>`, body);
}

function renderTile(ti) {
  const tx = ti % W, ty = (ti / W) | 0;
  const t = TERRAIN[world.terr[ti]];
  const res = RESOURCES[world.res[ti]];
  const own = world.owner[ti];
  const b = world.bld[ti] >= 0 ? game.buildings[world.bld[ti]] : null;
  const me = game.empires[game.player];

  let body = kv('Terrain', t.name);
  if (world.river[ti]) body += kv('Feature', 'River (+1 food)');
  if (res && (!res.strategic || seesOil(me))) {
    body += kv('Resource', res.strategic ? `${res.name} — build an Oil Well here`
                                         : `${res.name} (+${res.food}🌾 +${res.gold}🪙)`);
  }
  body += kv('Yield', `${t.food}🌾 ${t.gold}🪙`);
  body += kv('Owner', own >= 0 ? game.empires[own].name : 'Unclaimed');
  if (b && !b.dead) {
    let label = BLD[b.id].name;
    if (b.phase === 'strip') label += ' · being stripped';
    else if ((b.progress ?? 1) < 1) label += ` · building ${Math.round(b.progress * 100)}%`;
    body += kv('Building', label);
  }

  // On your own frontier tiles: one-click wall the whole shared border.
  if (own === game.player && hasTech(me, 'masonry') && isEnemyBorderTile(me.i, tx, ty)) {
    const foe = foeFacingTile(me.i, tx, ty);
    if (foe) {
      const open = borderTilesVs(me, foe.i).length;
      const cost = BLD.borderwall.cost;
      const canPay = Math.floor(me.gold / cost);
      const n = Math.min(open, canPay);
      body += `<div style="font-size:9.5px;color:var(--faint);line-height:1.4;margin-top:4px">
        Borders <b style="color:${foe.col}">${foe.name}</b> · ${open} open wall site${open === 1 ? '' : 's'}</div>`;
      if (open > 0) {
        body += `<div class="actions" style="margin-top:4px">
          <button class="abtn" data-act="fortify" data-foe="${foe.i}"
            ${n <= 0 ? 'disabled' : ''}>
            Wall frontier vs ${foe.name}
            <span style="opacity:.7">(${n || open}×${cost}g)</span>
          </button>
        </div>`;
      }
    }
  }

  panel(`<span>Tile ${tx}, ${ty}</span><span class="tag">Land</span>`, body);

  el.selBody.querySelectorAll('[data-act="fortify"]').forEach(btn => {
    btn.addEventListener('click', () => {
      const foeI = +btn.dataset.foe;
      const res2 = fortifyBorder(me, foeI);
      if (!res2.ok) logEvent(res2.why, 'info');
      refreshSelection(); refreshRails(); refreshHUD();
    });
  });
}

/* ── research tree ────────────────────────────────────────────────────────── */

function buildTechTree() {
  if (!el.techGrid) return;
  el.techGrid.innerHTML = '';
  for (let era = 0; era < ERAS.length; era++) {
    const col = document.createElement('div');
    col.className = 'eracol';
    const h = document.createElement('h3');
    h.textContent = ERAS[era];
    col.appendChild(h);
    for (const t of TECHS.filter(t => t.era === era)) {
      const b = document.createElement('button');
      b.className = 'tech';
      b.dataset.tech = t.id;
      b.innerHTML =
        `<b>${t.name}</b><span class="eff">${t.eff}</span>` +
        `<span class="meta"><span>${t.cost}🔬</span>` +
        (t.req.length ? `<span class="req">after ${t.req.map(r => TECH[r].name).join(', ')}</span>` : '') +
        `</span>`;
      b.addEventListener('click', () => {
        const e = game.empires[game.player];
        if (!techAvailable(e, t.id)) return;
        startResearch(e, t.id);
        logEvent(`Researching ${t.name}.`, 'tech');
        refreshTechTree(); refreshHUD();
      });
      col.appendChild(b);
    }
    el.techGrid.appendChild(col);
  }
}

export function refreshTechTree() {
  const e = game.empires[game.player];
  if (!e || !el.techGrid) return;
  el.techEra.textContent = ERAS[e.era] + ' era';
  el.techSci.textContent = shortNum(e.sci);
  el.techRate.textContent = rateStr(e.incSci) + '/s';

  el.techGrid.querySelectorAll('.tech').forEach(b => {
    const id = b.dataset.tech;
    b.classList.toggle('done', e.techs.has(id));
    b.classList.toggle('active', e.researching === id);
    const open = techAvailable(e, id);
    b.classList.toggle('avail', open);
    b.classList.toggle('locked', !open && !e.techs.has(id));
  });
}

export function openTech() {
  refreshTechTree();
  el.tech.classList.remove('hidden');
}
export function closeTech() { el.tech.classList.add('hidden'); }
export const techOpen = () => el.tech && !el.tech.classList.contains('hidden');
export const toggleTech = () => (techOpen() ? closeTech() : openTech());
