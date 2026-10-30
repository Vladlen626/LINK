import test from 'node:test';
import assert from 'node:assert/strict';
import { game, simulate } from '../lib/kit.mjs';

// One test per rule of docs/slime.md. The hero stands at the origin facing north (-z) unless a test moves it.
const g = await game();
const DT = 1 / 60;
const scene = (z, walls = []) => {
  const s = g.createGame({ level: 'empty' });
  s.walls.push(...walls);
  return { s, slime: g.spawn(s, 'slime', 0, z) };
};
const alive = (s, slime) => s.entities.some(e => e.id === slime.id && !e.removed);
const gap = (s, slime) => Math.hypot(slime.pos.x - s.hero.pos.x, slime.pos.z - s.hero.pos.z);
// Steps until the slime is in a mode (or the time is up); returns the seconds it took, or null.
const until = (s, slime, mode, seconds = 3, input = {}) => {
  for (let t = 0; t < seconds; t += DT) {
    if (slime.mode === mode) return t;
    g.step(s, input, DT);
  }
  return slime.mode === mode ? seconds : null;
};
// A sword hit on a slime set just in front of the hero.
const hit = (s, slime) => {
  Object.assign(slime.pos, { x: 0, z: -1.2 });
  g.step(s, { attack: true }, DT);
};

test('1. a slime has 2 HP: one sword hit does not defeat it', () => {
  const { s, slime } = scene(-1.2);
  assert.equal(slime.hp, 2);
  hit(s, slime);
  simulate(g, s, {}, 0.3);
  assert.ok(alive(s, slime), 'one hit defeated it');
});

test('2. idle, it hops in place every 1.2 s, 0.4 m high', () => {
  const { s, slime } = scene(-9);
  let top = 0, hops = 0, up = false, moved = 0;
  simulate(g, s, {}, 3.6, {
    each: () => {
      const y = slime.pos.y ?? 0;
      top = Math.max(top, y);
      if (y > 0.2 && !up) hops++;
      up = y > 0.2;
      moved = Math.max(moved, Math.hypot(slime.pos.x, slime.pos.z + 9));
    },
  });
  assert.ok(top >= 0.3 && top <= 0.5, `hops ${top.toFixed(2)} m high`);
  assert.ok(hops >= 2 && hops <= 4, `${hops} hops in 3.6 s`);
  assert.ok(moved < 0.1, `wandered ${moved.toFixed(2)} m`);
  assert.equal(slime.mode, 'idle');
});

test('3. it notices the hero within 6 m and chases', () => {
  const near = scene(-5.5);
  assert.notEqual(until(near.s, near.slime, 'chase', 0.5), null, 'it did not notice the hero 5.5 m away');
  const far = scene(-6.6);
  simulate(g, far.s, {}, 1);
  assert.equal(far.slime.mode, 'idle', 'it noticed the hero 6.6 m away');
});

test('4. a wall between them hides the hero', () => {
  const wall = { x: 0, z: -2, w: 4, d: 0.5, h: 2.5 };
  const { s, slime } = scene(-4, [wall]);
  simulate(g, s, {}, 1);
  assert.equal(slime.mode, 'idle', 'it noticed the hero through a wall');
  s.walls.splice(s.walls.indexOf(wall), 1);
  assert.notEqual(until(s, slime, 'chase', 0.5), null, 'it did not notice the hero once the wall was gone');
});

test('5. chasing, it moves toward the hero at 2.5 m/s', () => {
  const { s, slime } = scene(-5.5);
  assert.notEqual(until(s, slime, 'chase', 0.5), null);
  const d0 = gap(s, slime);
  simulate(g, s, {}, 0.8);
  const speed = (d0 - gap(s, slime)) / 0.8;
  assert.ok(Math.abs(speed - 2.5) <= 0.4, `it closed in at ${speed.toFixed(2)} m/s`);
  assert.ok(Math.abs(slime.pos.x) < 0.05, 'it did not go straight at the hero');
});

test('6. it gives up beyond 10 m and is idle again', () => {
  const { s, slime } = scene(-5);
  assert.notEqual(until(s, slime, 'chase', 0.5), null);
  s.hero.pos.z = 8;
  assert.notEqual(until(s, slime, 'idle', 0.3), null, 'it still chases a hero 12 m away');
});

test('7. within 2 m it stops and tells for 0.5 s', () => {
  const { s, slime } = scene(-3);
  assert.notEqual(until(s, slime, 'tell', 2), null, 'it never told');
  assert.ok(gap(s, slime) <= 2.1, `it told ${gap(s, slime).toFixed(2)} m away`);
  const at = { ...slime.pos };
  simulate(g, s, {}, 0.45, { each: () => assert.equal(slime.mode, 'tell', 'the tell ended early') });
  assert.ok(Math.hypot(slime.pos.x - at.x, slime.pos.z - at.z) < 0.05, 'it moved while telling');
  assert.notEqual(until(s, slime, 'lunge', 0.15), null, 'no lunge 0.6 s after the tell began');
});

test('8. it lunges at 6 m/s for 0.4 s toward where the hero was, rests 1 s, then chases', () => {
  const { s, slime } = scene(-3);
  assert.notEqual(until(s, slime, 'tell', 2), null);
  const target = { ...s.hero.pos }, from = { ...slime.pos };
  s.hero.pos.x = 3;
  const lunge = until(s, slime, 'lunge', 0.7);
  assert.notEqual(lunge, null, 'no lunge');
  const start = { ...slime.pos };
  let lunged = 0;
  for (; slime.mode === 'lunge' && lunged < 1; lunged += DT) g.step(s, {}, DT);
  const moved = Math.hypot(slime.pos.x - start.x, slime.pos.z - start.z);
  assert.ok(Math.abs(lunged - 0.4) < 0.05, `the lunge lasted ${lunged.toFixed(2)} s`);
  assert.ok(Math.abs(moved - 2.4) < 0.4, `the lunge went ${moved.toFixed(2)} m`);
  const aim = Math.atan2(target.x - from.x, target.z - from.z), went = Math.atan2(slime.pos.x - start.x, slime.pos.z - start.z);
  assert.ok(Math.abs(Math.atan2(Math.sin(went - aim), Math.cos(went - aim))) < 0.26, 'it did not lunge where the hero was');
  assert.equal(slime.mode, 'rest');
  const resting = { ...slime.pos };
  simulate(g, s, {}, 0.9);
  assert.equal(slime.mode, 'rest', 'the rest ended early');
  assert.ok(Math.hypot(slime.pos.x - resting.x, slime.pos.z - resting.z) < 0.05, 'it moved while resting');
  assert.notEqual(until(s, slime, 'chase', 0.2), null, 'it did not chase again after its rest');
});

test('9. a lunge that touches the hero takes a heart and knocks the hero back 1 m', () => {
  const { s, slime } = scene(-3);
  assert.notEqual(until(s, slime, 'lunge', 3), null, 'no lunge');
  let hurtAt = null, slimeAt = null;
  for (let t = 0; t < 0.6 && hurtAt === null; t += DT) {
    g.step(s, {}, DT);
    if (s.hero.hp < 3) [hurtAt, slimeAt] = [{ ...s.hero.pos }, { ...slime.pos }];
  }
  assert.equal(s.hero.hp, 2, 'the lunge did not take a heart');
  simulate(g, s, {}, 0.8);
  // Knocked back: 1 m or more, away from where the slime touched.
  const dx = s.hero.pos.x - hurtAt.x, dz = s.hero.pos.z - hurtAt.z;
  const away = dx * (hurtAt.x - slimeAt.x) + dz * (hurtAt.z - slimeAt.z);
  assert.ok(Math.hypot(dx, dz) >= 1 && away > 0, `the hero was knocked ${Math.hypot(dx, dz).toFixed(2)} m`);
});

test('10. a sword hit takes 1 HP and knocks the slime back 1.5 m', () => {
  const { s, slime } = scene(-1.2);
  hit(s, slime);
  assert.equal(slime.hp, 1);
  simulate(g, s, {}, 0.6);
  assert.ok(gap(s, slime) - 1.2 >= 1.5, `knocked back ${(gap(s, slime) - 1.2).toFixed(2)} m`);
});

test('11. a hit slime blinks for 0.6 s and cannot be hurt again meanwhile', () => {
  const { s, slime } = scene(-1.2);
  hit(s, slime);
  assert.equal(slime.mode, 'hurt');
  simulate(g, s, {}, 0.35);
  hit(s, slime);
  assert.equal(slime.hp, 1, 'hurt again while blinking');
  simulate(g, s, {}, 0.15);
  assert.equal(slime.mode, 'hurt', 'the blink ended early');
  simulate(g, s, {}, 0.2);
  assert.notEqual(slime.mode, 'hurt', 'still blinking after 0.7 s');
});

test('12. a defeated slime is gone at once and leaves a gem where it was', () => {
  const { s, slime } = scene(-1.2);
  hit(s, slime);
  simulate(g, s, {}, 0.8);
  hit(s, slime);
  assert.ok(!alive(s, slime), 'two hits did not defeat it');
  const gem = s.entities.find(e => e.kind === 'gem');
  assert.ok(gem, 'no gem');
  assert.ok(Math.hypot(gem.pos.x, gem.pos.z + 1.2) < 0.6, 'the gem is not where the slime was');
});
