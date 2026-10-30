import test from 'node:test';
import assert from 'node:assert/strict';
import { createGame, step, spawn } from '../src/game.js';

const run = (state, input, seconds, dt = 1 / 60) => {
  for (let t = 0; t < seconds; t += dt) step(state, input, dt);
};

test('the hero walks, runs and faces where it goes', () => {
  const s = createGame({ level: 'empty' });
  run(s, { moveX: 1 }, 1);
  assert.ok(Math.abs(s.hero.pos.x - 4) < 0.2, `walked to ${s.hero.pos.x}`);
  assert.ok(Math.abs(s.hero.facing - Math.PI / 2) < 1e-6);
  run(s, { moveX: 1, run: true }, 1);
  assert.ok(s.hero.pos.x > 10.5);
});

test('a wall stops the hero', () => {
  const s = createGame({ level: 'empty' });
  s.walls.push({ x: 0, z: -3, w: 6, d: 1, h: 2.5 });
  run(s, { moveZ: -1 }, 2);
  assert.ok(s.hero.pos.z > -2.5 + s.hero.r - 0.01, `hero at ${s.hero.pos.z}`);
});

test('gems, hearts and keys are picked up by walking onto them', () => {
  const s = createGame({ level: 'empty' });
  s.hero.hp = 1;
  spawn(s, 'gem', 1, 0);
  spawn(s, 'heart', 2, 0);
  spawn(s, 'key', 3, 0);
  run(s, { moveX: 1 }, 1);
  assert.deepEqual([s.hero.gems, s.hero.hp, s.hero.keys], [1, 2, 1]);
  assert.equal(s.entities.length, 0);
});

test('a chest gives its key once, and the key opens a locked door', () => {
  const s = createGame({ level: 'empty' });
  spawn(s, 'chest', 0, -1.2);
  const door = spawn(s, 'door', 3, -1.2);
  step(s, { use: true });
  step(s, { use: true });
  assert.equal(s.hero.keys, 1);
  s.hero.pos.x = 3;
  step(s, { use: true });
  assert.equal(door.open, true);
  assert.equal(s.hero.keys, 0);
});

test('the camera follows from behind and above, looking at the hero', () => {
  const s = createGame();
  run(s, { moveX: 1 }, 1);
  run(s, {}, 2);
  const { pos, look } = s.camera;
  assert.ok(Math.abs(look.x - s.hero.pos.x) < 0.1 && pos.y > 3 && pos.z > s.hero.pos.z);
});
