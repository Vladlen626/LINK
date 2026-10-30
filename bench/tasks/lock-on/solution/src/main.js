// The page: keys to input, one step and one drawing per frame, and window.island for tests and tools.
import { createGame, step, MAX_DT } from './game.js';
import { createView } from './draw/render.js';
import { createHud } from './hud.js';

const params = new URLSearchParams(location.search);
let state = createGame({ seed: Number(params.get('seed') || 1), level: params.get('level') || 'island' });
const view = createView(document.getElementById('game'), state);
const hud = createHud(document.getElementById('hud'));

// Held keys move and run; attack, roll and use fire once, on the frame after their key went down.
const held = new Set();
const pressed = new Set();
const ACTIONS = { Space: 'attack', KeyJ: 'attack', KeyK: 'roll', KeyE: 'use', KeyQ: 'lock', Tab: 'switch' };
addEventListener('keydown', event => {
  if (event.code in ACTIONS || event.code.startsWith('Arrow')) event.preventDefault();
  if (!event.repeat && ACTIONS[event.code]) pressed.add(ACTIONS[event.code]);
  held.add(event.code);
});
addEventListener('keyup', event => held.delete(event.code));
addEventListener('blur', () => held.clear());

function readInput() {
  const axis = (minus, plus) => plus.some(k => held.has(k)) - minus.some(k => held.has(k));
  const input = {
    moveX: axis(['KeyA', 'ArrowLeft'], ['KeyD', 'ArrowRight']),
    moveZ: axis(['KeyW', 'ArrowUp'], ['KeyS', 'ArrowDown']),
    run: held.has('ShiftLeft') || held.has('ShiftRight'),
  };
  for (const action of pressed) input[action] = true;
  pressed.clear();
  return input;
}

const frameTimes = [];
let last = performance.now();
function frame(now) {
  const dt = Math.min((now - last) / 1000, MAX_DT);
  frameTimes.push(now - last);
  if (frameTimes.length > 300) frameTimes.shift();
  last = now;
  step(state, readInput(), dt);
  draw();
  requestAnimationFrame(frame);
}

function draw() {
  view.draw(state);
  hud.update(state.hero);
}

// For tests and tools (see AGENTS.md).
window.island = {
  get state() { return state; },
  set state(next) { state = next; draw(); },
  step(input, dt) { step(state, input, dt); draw(); return state; },
  view: view.view,
  frameTimes,
  ready: false,
};
draw();
window.island.ready = true;
requestAnimationFrame(frame);
