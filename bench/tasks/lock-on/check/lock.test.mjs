import test from 'node:test';
import assert from 'node:assert/strict';
import { game, simulate, withPage, sleep } from '../lib/kit.mjs';

const g = await game();
// The hero stands at the origin facing north (-z). Slimes further than 6 m stay where they are.
const scene = (...slimes) => {
  const s = g.createGame({ level: 'empty' });
  return { s, ids: slimes.map(([x, z]) => g.spawn(s, 'slime', x, z).id) };
};
const target = s => {
  const t = s.hero.target;
  return t && typeof t === 'object' ? t.id : t ?? null;
};
const lock = s => g.step(s, { lock: true });
const around = (s, p) => Math.atan2(s.hero.pos.x - p.x, s.hero.pos.z - p.z);
const wrap = a => Math.atan2(Math.sin(a), Math.cos(a));

test('Q locks onto the nearest slime in a cone in front, not one behind or out of range', () => {
  let { s, ids } = scene([0, -8], [0, 3]);
  lock(s);
  assert.equal(target(s), ids[0], 'it did not lock onto the slime in front');
  ({ s, ids } = scene([3, -8.5], [0, -7.5]));
  lock(s);
  assert.equal(target(s), ids[1], 'it did not lock onto the nearest');
  ({ s } = scene([0, -13]));
  lock(s);
  assert.equal(target(s), null, 'it locked onto a slime 13 m away');
  ({ s } = scene([0, 7]));
  lock(s);
  assert.equal(target(s), null, 'it locked onto a slime behind');
});

test('Q again releases the lock', () => {
  const { s } = scene([0, -8]);
  lock(s);
  assert.notEqual(target(s), null, 'Q did not lock');
  simulate(g, s, {}, 0.2);
  lock(s);
  assert.equal(target(s), null, 'Q did not release');
});

test('Tab switches to the other slime and back', () => {
  const { s, ids } = scene([-2, -7], [3, -8]);
  lock(s);
  assert.equal(target(s), ids[0]);
  g.step(s, { switch: true });
  assert.equal(target(s), ids[1], 'Tab did not switch');
  g.step(s, { switch: true });
  assert.equal(target(s), ids[0], 'Tab did not switch back');
});

test('locked, the hero faces the target, strafes round it and walks toward it', () => {
  const { s, ids } = scene([0, -8]);
  const slime = s.entities.find(e => e.id === ids[0]);
  lock(s);
  const start = around(s, slime.pos);
  let lo = Infinity, hi = 0;
  simulate(g, s, { moveX: 1 }, 1.5, {
    each: () => {
      const d = Math.hypot(s.hero.pos.x - slime.pos.x, s.hero.pos.z - slime.pos.z);
      lo = Math.min(lo, d);
      hi = Math.max(hi, d);
    },
  });
  assert.ok(lo > 8 * 0.85 && hi < 8 * 1.15, `the distance went from ${lo.toFixed(2)} to ${hi.toFixed(2)} m`);
  const turned = Math.abs(wrap(around(s, slime.pos) - start));
  assert.ok(turned >= Math.PI / 6, `it went ${(turned * 180 / Math.PI).toFixed(0)} degrees round the target`);
  const facing = Math.atan2(slime.pos.x - s.hero.pos.x, slime.pos.z - s.hero.pos.z);
  assert.ok(Math.abs(wrap(s.hero.facing - facing)) < 0.26, 'the hero does not face the target');
  const d0 = Math.hypot(s.hero.pos.x - slime.pos.x, s.hero.pos.z - slime.pos.z);
  simulate(g, s, { moveZ: -1 }, 0.4);
  const d1 = Math.hypot(s.hero.pos.x - slime.pos.x, s.hero.pos.z - slime.pos.z);
  assert.ok(d0 - d1 >= 1, `forward took the hero from ${d0.toFixed(2)} to ${d1.toFixed(2)} m`);
});

// The angle between the camera's forward and the direction to a point, in degrees.
function offAxis(camera, p) {
  const f = { x: camera.look.x - camera.pos.x, y: camera.look.y - camera.pos.y, z: camera.look.z - camera.pos.z };
  const v = { x: p.x - camera.pos.x, y: p.y - camera.pos.y, z: p.z - camera.pos.z };
  const dot = f.x * v.x + f.y * v.y + f.z * v.z;
  return (Math.acos(dot / Math.hypot(f.x, f.y, f.z) / Math.hypot(v.x, v.y, v.z)) * 180) / Math.PI;
}

test('locked, the camera frames hero and target; released, it follows the hero again', () => {
  for (const [x, z] of [[0, -9], [6.4, -6], [-7, -5]]) {
    const { s, ids } = scene([x, z]);
    s.hero.facing = Math.atan2(x, z);
    lock(s);
    assert.equal(target(s), ids[0]);
    simulate(g, s, {}, 2);
    const slime = s.entities.find(e => e.id === ids[0]);
    const cam = s.camera, half = (cam.fov || 55) / 2;
    const hero = offAxis(cam, { x: s.hero.pos.x, y: 1, z: s.hero.pos.z }), them = offAxis(cam, { x: slime.pos.x, y: 0.4, z: slime.pos.z });
    assert.ok(hero < half && them < half, `with the slime at (${x}, ${z}) the hero is ${hero.toFixed(0)} and the slime ${them.toFixed(0)} degrees off the camera's axis (fov ${cam.fov})`);
    assert.ok(Math.hypot(cam.pos.x - s.hero.pos.x, cam.pos.z - s.hero.pos.z) > 2 && cam.pos.y > 1, 'the camera is on top of the hero');
    lock(s);
    simulate(g, s, {}, 2);
    assert.ok(Math.hypot(cam.look.x - s.hero.pos.x, cam.look.z - s.hero.pos.z) < 0.5, 'released, the camera does not look at the hero');
  }
});

test('the lock ends when the target is defeated or far away', () => {
  let { s, ids } = scene([0, -8]);
  lock(s);
  assert.equal(target(s), ids[0], 'Q did not lock');
  Object.assign(s.entities.find(e => e.id === ids[0]).pos, { x: 0, z: -25 });
  simulate(g, s, {}, 0.2);
  assert.equal(target(s), null, 'still locked onto a slime 25 m away');
  ({ s, ids } = scene([0, -8]));
  lock(s);
  assert.equal(target(s), ids[0], 'Q did not lock');
  s.entities = s.entities.filter(e => e.id !== ids[0]);
  simulate(g, s, {}, 0.2);
  assert.equal(target(s), null, 'still locked onto a slime that is gone');
});

test('in the page, Q locks onto the slime ahead', async () => {
  await withPage('', async page => {
    const id = await page.eval(`(() => {
      const s = island.state, slime = s.entities.find(e => e.kind === 'slime' && e.pos.x < 0);
      Object.assign(s.hero.pos, { x: slime.pos.x + 0.5, z: slime.pos.z + 7.5 });
      s.hero.facing = Math.PI;
      return slime.id;
    })()`);
    await page.press('q');
    await sleep(300);
    const locked = await page.eval('(t => t && typeof t === "object" ? t.id : t)(island.state.hero.target)');
    assert.equal(locked, id, 'Q did not lock onto the slime');
    await page.press('Tab');
    await page.press('q');
    await sleep(300);
  });
});
