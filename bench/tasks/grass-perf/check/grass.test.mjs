import test from 'node:test';
import assert from 'node:assert/strict';
import { performance } from 'node:perf_hooks';
import { game, simulate, withPage, sleep, percentile } from '../lib/kit.mjs';

const g = await game();
// The budget: 60 fps, with a little room for the timer's own jitter.
const FRAME_MS = 17.5, WORST_MS = 50;
const MEADOW = { x: 9, z: 7 };

test('the meadow keeps its 20,000 blades; a swing cuts those in front within reach, none behind', () => {
  const s = g.createGame();
  assert.ok(s.grass.length >= 20000, `${s.grass.length} blades`);
  Object.assign(s.hero.pos, MEADOW);
  s.hero.facing = Math.PI;
  const near = (b, ahead) => {
    const d = Math.hypot(b.x - MEADOW.x, b.z - MEADOW.z);
    const angle = Math.abs(Math.atan2(Math.sin(Math.atan2(b.x - MEADOW.x, b.z - MEADOW.z) - Math.PI), Math.cos(Math.atan2(b.x - MEADOW.x, b.z - MEADOW.z) - Math.PI)));
    return ahead ? d > 0.3 && d < 1 && angle < Math.PI / 6 : d > 0.6 && d < 1.5 && angle > (Math.PI * 2) / 3;
  };
  const front = s.grass.filter(b => !b.cut && near(b, true)), back = s.grass.filter(b => !b.cut && near(b, false));
  g.step(s, { attack: true });
  simulate(g, s, {}, 0.3);
  const cut = front.filter(b => b.cut).length;
  assert.ok(front.length > 10 && cut >= 0.9 * front.length, `${cut} of ${front.length} blades in front were cut`);
  assert.equal(back.filter(b => b.cut).length, 0, 'blades behind the hero were cut');
});

test('a swing in the meadow costs under 1.5 ms of logic', () => {
  const s = g.createGame();
  let seed = 5;
  const rand = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  const times = [];
  for (let i = 0; i < 60; i++) {
    Object.assign(s.hero.pos, { x: 4 + rand() * 9, z: 2 + rand() * 9 });
    const t0 = performance.now();
    g.step(s, { attack: true });
    if (i >= 10) times.push(performance.now() - t0);
    simulate(g, s, {}, 0.35);
  }
  const median = percentile(times, 0.5);
  assert.ok(median < 1.5, `a swing's step takes ${median.toFixed(2)} ms`);
});

async function measure(page, swing) {
  await page.eval(`(() => {
    const s = island.state;
    Object.assign(s.hero.pos, { x: 5, z: 10 });
    s.hero.facing = Math.PI / 2;
  })()`);
  await sleep(500);
  await page.keyDown('d');
  let swinging = true;
  const swings = (async () => {
    while (swing && swinging) {
      await page.press(' ', 40);
      await sleep(210);
    }
  })();
  const times = await page.frameTimes(150);
  swinging = false;
  await swings;
  await page.keyUp('d');
  return { p90: percentile(times, 0.9), worst: Math.max(...times) };
}

test('the page holds 60 fps walking through the meadow', async () => {
  await withPage('', async page => {
    const { p90 } = await measure(page, false);
    assert.ok(p90 <= FRAME_MS, `90% of frames within ${p90.toFixed(1)} ms`);
  });
});

test('the page holds 60 fps swinging through the meadow', async () => {
  await withPage('', async page => {
    const { p90, worst } = await measure(page, true);
    assert.ok(p90 <= FRAME_MS && worst <= WORST_MS, `90% of frames within ${p90.toFixed(1)} ms, the worst ${worst.toFixed(1)} ms`);
    assert.ok(await page.eval('island.state.grass.some(b => b.cut)'), 'no blade was cut');
  });
});
