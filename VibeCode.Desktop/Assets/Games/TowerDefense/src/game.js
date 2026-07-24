// Run control: overlay screens, the main loop, and the wiring between input,
// HUD and simulation.
//
// The host window (VibeCode's GameWindow) drives pause/resume through the DOM:
// it looks for #stage.playing, sends a "p" keydown to pause, and clicks
// #resumeBtn to continue. Those hooks must keep working.
import { TDEF } from './config.js';
import { S, resetRun, place, upgrade, sell, canBuild, TARGET_MODES as MODES } from './state.js';
import { step, sendWaveNow } from './sim.js';
import { view, layout } from './view.js';
import { renderFrame } from './render.js';
import {
  initHud, flushHud, resetHud, refreshSelection, setSpeedLabel,
  hideTurretInfo,
} from './hud.js';
import { bindInput } from './input.js';
import { unlockAudio } from './audio.js';

const $ = id => document.getElementById(id);
const FIXED = 1 / 60;

let screens, stage, running = false, paused = false, over = false;
let last = 0, acc = 0;

export function startGame() {
  stage = $('stage');
  screens = { menu: $('menu'), pause: $('pause'), over: $('over') };

  initHud({
    onPickType: pickType,
    onSendWave: () => { if (running && !paused) sendWaveNow(); },
    onSpeed: cycleSpeed,
    onPause: togglePause,
    onUpgrade: () => { hideTurretInfo(); upgrade(S.selected); },
    onSell: () => { hideTurretInfo(); sell(S.selected); },
    onCycleTarget: cycleTargeting,
  });

  bindInput({
    isLive: () => running && !paused,
    onCell: onCellClick,
    onRightClick: onRightClick,
    onPause: togglePause,
  });

  $('startBtn').addEventListener('click', beginRun);
  $('restartBtn').addEventListener('click', beginRun);
  $('resumeBtn').addEventListener('click', () => { if (paused) togglePause(); });

  // Losing the window is an implicit pause; the host relies on this too.
  document.addEventListener('visibilitychange', () => {
    if (document.hidden && running && !paused) togglePause();
  });

  window.addEventListener('resize', relayout);
  relayout();
  show('menu');
  renderFrame();
  requestAnimationFrame(frame);
}

function relayout() {
  // Resizing reallocates the canvas backing store, which clears it. Repaint
  // immediately: when the game is paused, on the menu, or in a window whose
  // animation frames are throttled, nothing else would redraw the board.
  layout(stage, $('shop'));
  renderFrame();
}

// ---------------- selection & building ----------------
function pickType(def) {
  unlockAudio();
  hideTurretInfo();
  S.selectedType = S.selectedType === def ? null : def;
  S.selected = null;
  refreshSelection();
}

function clearSelection() {
  S.selectedType = null;
  S.selected = null;
  hideTurretInfo();
  refreshSelection();
}

function onCellClick(i) {
  unlockAudio();
  hideTurretInfo();
  const existing = (i >= 0 && S.grid[i] >= 0) ? S.towers[S.grid[i]] : null;

  // Click a placed turret → sell / upgrade panel.
  if (existing) {
    S.selected = existing;
    S.selectedType = null;
    refreshSelection();
    return;
  }

  // Empty ground: place if a shop tool is active and the tile is legal, then always
  // clear selection so the next empty click doesn't keep a sticky tool/panel open.
  if (S.selectedType && i >= 0 && canBuild(i)) {
    place(S.selectedType, i);
  }
  clearSelection();
}

/** Right-click on the map: never open the shop description (that's shop-only).
 *  Empty / any board right-click just closes info and clears selection. */
function onRightClick(i) {
  unlockAudio();
  clearSelection();
}

function cycleTargeting() {
  const t = S.selected;
  if (!t || t.def.kind === 'aura' || t.def.kind === 'buff' || t.def.kind === 'nova'
      || t.def.kind === 'singularity' || t.def.kind === 'tempest') return;
  t.targeting = MODES[(MODES.indexOf(t.targeting) + 1) % MODES.length];
}

function cycleSpeed() {
  S.speed = S.speed === 1 ? 2 : S.speed === 2 ? 3 : 1;
  setSpeedLabel(S.speed);
}

// ---------------- screens & run state ----------------
function show(name) {
  for (const k in screens) screens[k].classList.toggle('hidden', k !== name);
  stage.classList.toggle('playing', name === null);
}

function beginRun() {
  unlockAudio();
  resetRun();
  S.selectedType = TDEF.pulse;
  setSpeedLabel(1);
  resetHud();
  over = false; paused = false; running = true;
  last = 0; acc = 0;
  show(null);
}

function togglePause() {
  if (over || !running) return;
  paused = !paused;
  show(paused ? 'pause' : null);
  if (paused) {
    $('pauseSub').textContent =
      `Wave ${S.wave} · ${S.towers.length} turrets standing · ${S.lives} lives left.`;
  } else {
    last = 0;
  }
}

function gameOver() {
  if (over) return;
  over = true; running = false; paused = false;
  $('ovWave').textContent = Math.max(0, S.wave - 1);
  $('ovKills').textContent = S.kills;
  $('ovTowers').textContent = S.built;
  $('overSub').textContent = S.wave <= 5
    ? 'Build more turrets early — Pulse towers are cheap and stack well.'
    : 'Layer Cryo in front of your damage and keep an Amplifier near the cluster.';
  show('over');
}

// ---------------- main loop ----------------
function frame(now) {
  requestAnimationFrame(frame);
  if (!running || paused) { last = now; return; }
  if (!last) last = now;

  const real = Math.min(0.12, (now - last) / 1000);
  last = now;
  acc += real * S.speed;

  // Fixed timestep, so 2x/3x speed is just more ticks per frame. The guard
  // stops a long stall (or a background tab) from turning into a huge catch-up.
  let guard = 0;
  while (acc >= FIXED && guard++ < 16) {
    acc -= FIXED;
    step(FIXED);
    if (S.gameOver) { gameOver(); acc = 0; break; }
  }
  if (guard >= 16) acc = 0;

  flushHud();
  renderFrame();
}
