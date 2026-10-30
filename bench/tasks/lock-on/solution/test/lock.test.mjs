import test from 'node:test';
import assert from 'node:assert/strict';
import { createGame, step, spawn } from '../src/game.js';

test('Q locks onto the slime in front, Tab switches, Q releases', () => {
  const s = createGame({ level: 'empty' });
  const a = spawn(s, 'slime', -2, -7), b = spawn(s, 'slime', 3, -8);
  step(s, { lock: true });
  assert.equal(s.hero.target, a.id);
  step(s, { switch: true });
  assert.equal(s.hero.target, b.id);
  step(s, { lock: true });
  assert.equal(s.hero.target, null);
});
