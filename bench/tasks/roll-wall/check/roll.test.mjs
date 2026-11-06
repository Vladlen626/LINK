import test from 'node:test';
import assert from 'node:assert/strict';
import { game, simulate } from '../lib/kit.mjs';

const g = await game();
const RATES = [0.1, 1 / 15, 0.05, 1 / 30, 1 / 60];
const FENCE = { x: 0, z: -2, w: 10, d: 0.12, h: 1 };

const inside = (pos, r, box) =>
  Math.hypot(pos.x - Math.max(box.x - box.w / 2, Math.min(pos.x, box.x + box.w / 2)), pos.z - Math.max(box.z - box.d / 2, Math.min(pos.z, box.z + box.d / 2))) < r - 0.02;

function roll(fromZ, dt, walls = [FENCE]) {
  const s = g.createGame({ level: 'empty' });
  s.walls.push(...walls);
  s.hero.pos.z = fromZ;
  g.step(s, { moveZ: -1, roll: true }, dt);
  simulate(g, s, { moveZ: -1 }, 1, { dt });
  return s;
}

test('a roll at a low frame rate stops at a thin fence, and in the open goes as far as at 60 fps', () => {
  for (const dt of RATES) {
    for (let z = -1.3; z <= 0.6; z += 0.05) {
      const s = roll(z, dt);
      assert.ok(s.hero.pos.z > FENCE.z, `at ${Math.round(1 / dt)} fps a roll from z ${z.toFixed(2)} ended beyond the fence (z ${s.hero.pos.z.toFixed(2)})`);
    }
  }
  const open = dt => {
    const s = g.createGame({ level: 'empty' });
    g.step(s, { roll: true }, dt);
    simulate(g, s, {}, 1, { dt });
    return Math.hypot(s.hero.pos.x, s.hero.pos.z);
  };
  const smooth = open(1 / 60), slow = open(0.1);
  assert.ok(smooth >= 3, `a roll in the open goes ${smooth.toFixed(2)} m`);
  assert.ok(Math.abs(slow - smooth) <= 0.15 * smooth, `a roll goes ${slow.toFixed(2)} m at 10 fps, ${smooth.toFixed(2)} m at 60`);
});

test('a roll into a wall at an angle slides along it', () => {
  for (const dt of [1 / 60, 0.1]) {
    const s = g.createGame({ level: 'empty' });
    const wall = { x: 0, z: -2, w: 30, d: 1, h: 2.5 };
    s.walls.push(wall);
    s.hero.pos.z = -1;
    g.step(s, { moveX: 1, moveZ: -1, roll: true }, dt);
    simulate(g, s, { moveX: 1, moveZ: -1 }, 0.5, { dt });
    assert.ok(s.hero.pos.x >= 1.5, `at ${Math.round(1 / dt)} fps the roll stuck at x ${s.hero.pos.x.toFixed(2)}`);
    assert.ok(!inside(s.hero.pos, s.hero.r, wall), 'the hero ended inside the wall');
  }
});

test('walking along a wall at an angle slides along it', () => {
  for (const dt of [1 / 60, 0.1]) {
    const s = g.createGame({ level: 'empty' });
    s.walls.push({ x: -2, z: 0, w: 1, d: 30, h: 2.5 });
    s.hero.pos.x = -1;
    simulate(g, s, { moveX: -1, moveZ: -1 }, 1, { dt });
    assert.ok(s.hero.pos.z <= -2, `at ${Math.round(1 / dt)} fps the hero stuck at z ${s.hero.pos.z.toFixed(2)}`);
  }
});

// A seeded walk with rolls and runs at frame rates from 10 to 60 fps, among walls and thin fences.
test('a long walk at any frame rate never ends inside a wall or across a fence', () => {
  let seed = 99;
  const rand = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  const s = g.createGame({ level: 'empty' });
  const fences = [{ x: 0, z: -3, w: 12, d: 0.12, h: 1 }, { x: 0, z: 3, w: 12, d: 0.12, h: 1 }, { x: -3, z: 0, w: 0.12, d: 4, h: 1 }];
  const walls = [{ x: 0, z: -8, w: 20, d: 1, h: 2.5 }, { x: 0, z: 8, w: 20, d: 1, h: 2.5 }, { x: -8, z: 0, w: 1, d: 16, h: 2.5 }, { x: 8, z: 0, w: 1, d: 16, h: 2.5 }];
  s.walls.push(...fences, ...walls);
  // Which side of each fence the hero is on, where the fence spans.
  const sides = () => fences.map(f => (f.w > f.d ? Math.sign(s.hero.pos.z - f.z) : Math.sign(s.hero.pos.x - f.x)));
  const within = (f, p) => (f.w > f.d ? Math.abs(p.x - f.x) < f.w / 2 - 0.5 : Math.abs(p.z - f.z) < f.d / 2 - 0.5);
  let input = {};
  for (let i = 0; i < 4000; i++) {
    if (i % 20 === 0) {
      const a = rand() * Math.PI * 2;
      input = { moveX: Math.sin(a), moveZ: Math.cos(a), run: rand() < 0.5 };
    }
    const dt = [0.1, 0.1, 1 / 15, 0.05, 1 / 30, 1 / 60][Math.floor(rand() * 6)];
    const before = sides(), from = { ...s.hero.pos };
    g.step(s, { ...input, roll: rand() < 0.15 }, dt);
    for (const box of [...fences, ...walls]) assert.ok(!inside(s.hero.pos, s.hero.r, box), `step ${i}: the hero is inside a wall`);
    sides().forEach((side, k) => {
      if (within(fences[k], from) && within(fences[k], s.hero.pos)) assert.equal(side, before[k], `step ${i}: the hero crossed a fence at ${Math.round(1 / dt)} fps`);
    });
  }
});
