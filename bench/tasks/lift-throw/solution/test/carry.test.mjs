import test from 'node:test';
import assert from 'node:assert/strict';
import { createGame, step, spawn, find } from '../src/game.js';

const run = (state, input, seconds) => {
  for (let t = 0; t < seconds; t += 1 / 60) step(state, input);
};

test('E lifts the pot in front overhead and E throws it, where it breaks', () => {
  const s = createGame({ level: 'empty' });
  const pot = spawn(s, 'pot', 0, -1.2);
  step(s, { use: true });
  assert.ok(pot.pos.y > 1.2);
  step(s, { use: true });
  run(s, {}, 1);
  assert.equal(find(s, pot.id), undefined);
  assert.ok(s.entities.some(e => e.kind === 'piece' && e.pos.z < -2));
});
