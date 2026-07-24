// main.js — bootstraps the page, owns the frame loop, and wires the overlay
// screens. It also honours the VibeCode host's pause contract: the shell looks
// for #stage.playing, toggles #pause.hidden, presses "p", and clicks #resumeBtn.

import { clamp } from './core.js';
import { world, game, camera, markDirty } from './store.js';
import { DIFFS } from './data.js';
import { newGame } from './setup.js';
import { findPath } from './pathing.js';
import { bake, bakeBld, bakeUnit, bakeCity, SPR_BLD, SPR_UNIT, SPR_CITY } from './sprites.js';
// ?v= forces WebView to drop a stale module graph after art/scale tweaks.
import { initRender, resize, draw, refreshMinimap } from './render.js?v=unit30';
import { initInput, updateCameraKeys } from './input.js';
import {
  initUI, showHUD, refreshHUD, refreshRails, refreshSelection, refreshTechTree,
  openTech, closeTech, techOpen, setPlace,
} from './ui.js';
import { updateUnits, updateCities, orderMove, orderAttack } from './military.js';
import { updateVillagers, updateFx, addUnit, addBuilding } from './entities.js';
import { recomputeCity, canTrain } from './economy.js';
import { updateConstruction } from './construction.js';
import { updateVision } from './vision.js';
import { tickWorld, CYCLE } from './turn.js';
import { logEvent } from './log.js';

const $ = id => document.getElementById(id);
const stage = $('stage');
const screens = { menu: $('menu'), pause: $('pause'), over: $('over'), win: $('win') };

let difficulty = 1;
let rivalCount = 6;
let last = 0, hudTimer = 0, railTimer = 0;
let endShown = false;

/* ── screens ──────────────────────────────────────────────────────────────── */

function showScreen(name) {
  for (const k in screens) screens[k].classList.toggle('hidden', k !== name);
  stage.classList.toggle('playing', name === null);
  showHUD(name === null || name === 'pause');
}

function startGame() {
  newGame(difficulty, rivalCount);
  endShown = false;
  showScreen(null);
  refreshHUD(); refreshRails(); refreshSelection(); refreshTechTree();
  focusGame();
}

export function togglePause() {
  if (game.mode === 'playing') {
    game.mode = 'pause';
    showScreen('pause');
  } else if (game.mode === 'pause') {
    game.mode = 'playing';
    closeTech();
    showScreen(null);
    focusGame();
  }
}

function endScreen() {
  if (game.mode === 'over') {
    $('ovYear').textContent = yearText();
    $('ovCities').textContent = game.stats.founded + 1;
    $('ovTech').textContent = game.empires[game.player].techs.size;
    $('ovKills').textContent = game.stats.kills;
    showScreen('over');
  } else if (game.mode === 'win') {
    const me = game.empires[game.player];
    $('winKind').textContent = game.winKind === 'space' ? 'The Space Race' : 'Total conquest';
    $('winSub').textContent = game.winKind === 'space'
      ? 'From mud huts to orbit. The rest of the world is still looking up.'
      : 'Every rival banner has come down.';
    $('wnYear').textContent = yearText();
    $('wnCities').textContent = game.cities.filter(c => !c.dead && c.owner === me.i).length;
    $('wnTech').textContent = me.techs.size;
    $('wnKills').textContent = game.stats.kills;
    showScreen('win');
  }
}

const yearText = () => (game.year < 0 ? `${Math.abs(Math.round(game.year))} BC` : `AD ${Math.round(game.year)}`);

function focusGame() {
  const cv = $('game');
  try { window.focus(); cv.focus(); } catch { /* focus is a convenience only */ }
}

/* ── frame loop ───────────────────────────────────────────────────────────── */

function frame(now) {
  requestAnimationFrame(frame);
  const raw = last ? (now - last) / 1000 : 0;
  last = now;
  const dt = clamp(raw, 0, 0.1);          // a stalled tab must not fast-forward the world

  if (game.mode === 'playing') {
    const gdt = dt * game.speed;
    game.time += gdt;

    updateUnits(gdt);
    updateCities(gdt);
    updateConstruction(gdt);
    updateVillagers(gdt);
    updateFx(gdt);
    updateVision(false, gdt);

    // No turn boundary any more — the world just accumulates.
    tickWorld(gdt);

    updateCameraKeys(dt);

    hudTimer += dt;
    if (hudTimer > 0.2) {
      hudTimer = 0;
      refreshHUD();
      refreshSelection();
      if (techOpen()) refreshTechTree();
    }
    railTimer += dt;
    if (railTimer > 1.0) { railTimer = 0; refreshRails(); refreshMinimap(); }
  }

  // Checked outside the playing branch so the result screen still appears when
  // the run ends between turns (or from a debug fast-forward).
  if (!endShown && (game.mode === 'over' || game.mode === 'win')) {
    endShown = true;
    endScreen();
  }

  if (game.mode !== 'menu') draw(dt);
}

/* ── wiring ───────────────────────────────────────────────────────────────── */

function wire() {
  $('startBtn').addEventListener('click', startGame);
  $('restartBtn').addEventListener('click', startGame);
  $('winRestartBtn').addEventListener('click', startGame);
  $('resumeBtn').addEventListener('click', e => { e.preventDefault(); if (game.mode === 'pause') togglePause(); });
  $('pauseTechBtn').addEventListener('click', () => (techOpen() ? closeTech() : openTech()));
  $('pauseBtn').addEventListener('click', togglePause);
  // The research button lives in the dock now and wires itself in initUI().
  $('techClose').addEventListener('click', closeTech);

  document.querySelectorAll('#diffRow .diff').forEach(b => {
    b.addEventListener('click', () => {
      document.querySelectorAll('#diffRow .diff').forEach(x => x.classList.remove('on'));
      b.classList.add('on');
      difficulty = +b.dataset.diff;
    });
  });

  document.querySelectorAll('#rivalRow .riv').forEach(b => {
    b.addEventListener('click', () => {
      document.querySelectorAll('#rivalRow .riv').forEach(x => x.classList.remove('on'));
      b.classList.add('on');
      rivalCount = +b.dataset.rivals;
    });
  });

  const speedBtns = [$('spd1'), $('spd2'), $('spd3')];
  const syncSpeed = () => speedBtns.forEach((b, i) => b.classList.toggle('on', game.speed === i + 1));
  speedBtns.forEach((b, i) => b.addEventListener('click', () => { game.speed = i + 1; syncSpeed(); }));
  syncSpeed();

  window.addEventListener('resize', () => { resize(); markDirty('minimap'); });

  // Real backgrounding (the host hid or minimised us) is a safe place to auto-pause.
  document.addEventListener('visibilitychange', () => {
    if (document.hidden && game.mode === 'playing') togglePause();
    else if (!document.hidden && game.mode === 'pause') focusGame();
  });
  window.addEventListener('focus', () => { if (game.mode !== 'menu') focusGame(); });

  initInput($('game'), $('minimap'), { togglePause, speedChanged: syncSpeed });
}

/* ── debug surface ────────────────────────────────────────────────────────
   Exposed for DevTools and for the host's headless smoke test, which needs to
   start a game and fast-forward it without waiting on real frames. */
function installDebugHooks() {
  window.__TE = {
    game, world, camera,
    api: {
      orderMove, orderAttack, addUnit, findPath, addBuilding, recomputeCity, canTrain,
      /** Drop a finished building straight in, skipping the construction site. */
      addBuildingNow(emp, defId, x, y, city) {
        const b = addBuilding(emp, defId, x, y, city);
        b.progress = 1; b.phase = 'build';
        recomputeCity(city);
        return b;
      },
    },
    art: { bake, bakeBld, bakeUnit, bakeCity, SPR_BLD, SPR_UNIT, SPR_CITY },
    start(d = 1, rivals) { difficulty = d; if (rivals) rivalCount = rivals; startGame(); },

    /** Run the simulation for `seconds` of game time with no rendering. */
    simulate(seconds, step = 1 / 30) {
      const errors = [];
      for (let t = 0; t < seconds && game.mode === 'playing'; t += step) {
        try {
          updateUnits(step); updateCities(step); updateConstruction(step);
          updateVillagers(step); updateFx(step);
          updateVision(false, step);
          game.time += step;
          tickWorld(step);
        } catch (e) {
          errors.push(String((e && e.stack) || e));
          if (errors.length > 2) break;
        }
      }
      return errors;
    },

    snapshot() {
      const me = game.empires[game.player];
      const per = game.empires.map(e => ({
        name: e.name, dead: e.dead,
        cities: game.cities.filter(c => !c.dead && c.owner === e.i).length,
        units: game.units.filter(u => !u.dead && u.owner === e.i).length,
        techs: e.techs.size, gold: Math.round(e.gold),
      }));
      return {
        mode: game.mode, turn: game.turn, year: Math.round(game.year),
        buildings: game.buildings.filter(b => !b.dead).length,
        totalCities: game.cities.filter(c => !c.dead).length,
        totalUnits: game.units.filter(u => !u.dead).length,
        embarked: game.units.filter(u => !u.dead && u.embarked).length,
        kills: game.stats.kills, captured: game.stats.captured,
        myGold: Math.round(me.gold), myTechs: me.techs.size,
        empires: per,
        errors: window.__TE_ERRORS,
      };
    },
  };
  window.__TE_BOOTED = true;
}

function boot() {
  initUI();
  initRender($('game'), $('minimap'));
  wire();
  installDebugHooks();
  showScreen('menu');
  requestAnimationFrame(frame);
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();
