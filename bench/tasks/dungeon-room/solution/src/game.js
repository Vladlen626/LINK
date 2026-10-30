// The game's logic, apart from drawing: a plain-data state and step(state, input, dt). The page and the tests drive
// it the same way.
import { createLevel } from './level.js';
import { createHero, updateHero } from './hero.js';
import { updateCombat } from './combat.js';
import { updatePieces } from './breakables.js';
import { updateSlimes } from './slime.js';
import { updateProps } from './props.js';
import { pushBlocks, updateSwitches } from './room.js';
import { collectPickups } from './pickups.js';
import { createCamera, updateCamera } from './camera.js';
import { spawn } from './entities.js';

export { spawn, find } from './entities.js';

// The longest step the page takes: a slower frame is played as this much time.
export const MAX_DT = 0.1;

export function createGame({ seed = 1, level = 'island' } = {}) {
  const lv = createLevel(level);
  const state = {
    level, time: 0, seed: seed | 0, nextId: 1,
    hero: createHero(lv.hero), walls: lv.walls, entities: [], grass: lv.grass, grassVersion: 0,
    radius: lv.radius, camera: createCamera(), events: [],
  };
  for (const { kind, x, z, ...props } of lv.entities) spawn(state, kind, x, z, props);
  updateCamera(state, 10);
  return state;
}

// Advances the game by dt seconds (the page passes its frame time, at most MAX_DT). `input` holds what the player
// does: moveX and moveZ (-1..1, world axes: -z is north, away from the camera), run (held), and attack, roll and
// use, true on the step their key went down.
export function step(state, input = {}, dt = 1 / 60) {
  state.time += dt;
  state.events = [];
  pushBlocks(state, input, dt);
  updateHero(state, input, dt);
  updateCombat(state, input, dt);
  updateProps(state, input, dt);
  updateSlimes(state, dt);
  updatePieces(state, dt);
  collectPickups(state);
  updateSwitches(state);
  state.entities = state.entities.filter(e => !e.removed);
  updateCamera(state, dt);
  return state;
}

// The whole game as text and back: the state is plain data.
export const save = state => JSON.stringify(state);
export const load = text => JSON.parse(text);
