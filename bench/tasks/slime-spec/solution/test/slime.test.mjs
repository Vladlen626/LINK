import test from 'node:test';
import assert from 'node:assert/strict';
import { createGame, step, spawn, find } from '../src/game.js';

const run = (state, input, seconds) => {
  for (let t = 0; t < seconds; t += 1 / 60) step(state, input);
};

test('a slime notices the hero close by and comes', () => {
  const s = createGame({ level: 'empty' });
  const slime = spawn(s, 'slime', 0, -5);
  run(s, {}, 0.5);
  assert.equal(slime.mode, 'chase');
  assert.ok(slime.pos.z > -5);
});

test('two sword hits defeat a slime, which leaves a gem', () => {
  const s = createGame({ level: 'empty' });
  const slime = spawn(s, 'slime', 0, -1.2, { mode: 'rest', timer: -10 });
  step(s, { attack: true });
  run(s, {}, 0.7);
  Object.assign(slime.pos, { x: 0, z: -1.2 });
  step(s, { attack: true });
  assert.equal(find(s, slime.id), undefined);
  assert.ok(s.entities.some(e => e.kind === 'gem'));
});
