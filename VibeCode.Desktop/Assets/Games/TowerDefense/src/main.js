// Entry point: hand the canvas to the view, then start the game shell.
import { TURRETS, TDEF, COLS, ROWS } from './config.js';
import { BLOCKED, cellIndex } from './path.js';
import { S, place, upgrade, sell, canBuild } from './state.js';
import { step } from './sim.js';
import { initView } from './view.js';
import { renderFrame } from './render.js';
import { startGame } from './game.js';

initView(document.getElementById('game'));
startGame();

// Automation surface. Everything here is already reachable by playing; it is
// exposed so a headless check can drive a real board (build, fast-forward,
// screenshot) without pretending to be a mouse.
window.__td = {
  S, step, renderFrame, place, upgrade, sell, canBuild, cellIndex,
  TURRETS, TDEF, BLOCKED, COLS, ROWS,
};
