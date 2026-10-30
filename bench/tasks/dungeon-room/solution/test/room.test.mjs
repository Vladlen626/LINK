import test from 'node:test';
import assert from 'node:assert/strict';
import { createGame, step, save, load } from '../src/game.js';

const run = (state, input, seconds) => {
  for (let t = 0; t < seconds; t += 1 / 60) step(state, input);
};

test('the block pushed onto the switch opens the gate, and the room saves and loads', () => {
  const s = createGame({ level: 'room' });
  const gate = s.entities.find(e => e.kind === 'gate');
  Object.assign(s.hero.pos, { x: -3.1, z: 3 });
  run(s, { moveX: 1 }, 3);
  assert.equal(gate.open, true);
  const loaded = load(save(s));
  assert.equal(loaded.entities.find(e => e.kind === 'gate').open, true);
});
