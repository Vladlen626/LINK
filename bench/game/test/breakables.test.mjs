import test from 'node:test';
import assert from 'node:assert/strict';
import { createGame, step, spawn, find } from '../src/game.js';

test('one swing breaks a pot in front into pieces and a drop', () => {
  const s = createGame({ level: 'empty' });
  const pot = spawn(s, 'pot', 0, -1.4);
  step(s, { attack: true });
  assert.equal(find(s, pot.id), undefined);
  assert.ok(s.entities.some(e => e.kind === 'piece'));
  assert.ok(s.entities.some(e => e.kind === 'gem' || e.kind === 'heart'));
});

test('a crate takes three hits', () => {
  const s = createGame({ level: 'empty' });
  const crate = spawn(s, 'crate', 0, -1.5);
  for (let i = 0; i < 2; i++) {
    step(s, { attack: true });
    for (let t = 0; t < 0.4; t += 1 / 60) step(s, {});
  }
  assert.ok(find(s, crate.id));
  step(s, { attack: true });
  assert.equal(find(s, crate.id), undefined);
});
