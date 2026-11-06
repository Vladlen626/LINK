import test from 'node:test';
import assert from 'node:assert/strict';
import { createGame, step, spawn } from '../src/game.js';

test('four heart pieces make a new heart and fill the hearts', () => {
  const s = createGame({ level: 'empty' });
  s.hero.hp = 1;
  for (let i = 0; i < 4; i++) {
    spawn(s, 'heart-piece', 0.5, 0);
    step(s, {});
  }
  assert.deepEqual([s.hero.maxHp, s.hero.hp, s.hero.heartPieces], [4, 4, 0]);
});
