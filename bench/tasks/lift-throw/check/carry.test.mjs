import test from 'node:test';
import assert from 'node:assert/strict';
import { game, simulate, withPage, sleep } from '../lib/kit.mjs';

const g = await game();
const PICKUPS = ['gem', 'heart', 'key', 'heart-piece'];

// The pot by its id: on the ground, or carried in the hero's own fields.
const potOf = (s, id) => s.entities.find(e => e.id === id && !e.removed) ??
  Object.values(s.hero).find(v => v && typeof v === 'object' && v.id === id);
const carriedByHero = (s, id) => !s.entities.some(e => e.id === id && !e.removed) && !!potOf(s, id);

function lifted() {
  const s = g.createGame({ level: 'empty' });
  const pot = g.spawn(s, 'pot', 0, -1.2);
  g.step(s, { use: true });
  simulate(g, s, {}, 0.3);
  return { s, id: pot.id };
}

test('E lifts the pot in front overhead, and it goes where the hero goes', () => {
  const far = g.createGame({ level: 'empty' });
  const away = g.spawn(far, 'pot', 0, -3.5);
  g.step(far, { use: true });
  simulate(g, far, {}, 0.3);
  assert.ok((potOf(far, away.id)?.pos.y ?? 0) < 0.5 && !carriedByHero(far, away.id), 'a pot 3.5 m away was lifted');

  const { s, id } = lifted();
  const overhead = () => {
    if (carriedByHero(s, id)) return;
    const pot = potOf(s, id);
    assert.ok(pot, 'the pot is gone');
    assert.ok(pot.pos.y >= 1.2, `the pot is at height ${pot.pos.y.toFixed(2)}`);
    assert.ok(Math.hypot(pot.pos.x - s.hero.pos.x, pot.pos.z - s.hero.pos.z) < 0.6, 'the pot is not over the hero');
  };
  overhead();
  simulate(g, s, { moveX: 1 }, 1);
  overhead();
});

test('carrying a pot slows the walk', () => {
  const free = g.createGame({ level: 'empty' });
  simulate(g, free, { moveX: 1 }, 1);
  const { s } = lifted();
  const x = s.hero.pos.x;
  simulate(g, s, { moveX: 1 }, 1);
  const slow = s.hero.pos.x - x, normal = free.hero.pos.x;
  assert.ok(slow < 0.85 * normal && slow > 0.2 * normal, `walked ${slow.toFixed(2)} m carrying, ${normal.toFixed(2)} m free`);
});

// Throws the lifted pot north and follows it until it is gone (3 s at most).
function throwPot(s, id, each) {
  const from = { ...s.hero.pos };
  const path = [];
  g.step(s, { use: true });
  for (let t = 0; t < 3; t += 1 / 60) {
    const pot = s.entities.find(e => e.id === id && !e.removed);
    if (!pot) break;
    path.push({ ...pot.pos, y: pot.pos.y ?? 0 });
    each?.(s);
    g.step(s, {});
  }
  return { from, path, gone: !s.entities.some(e => e.id === id && !e.removed) };
}

test('E again throws it forward in an arc, and it breaks where it lands', () => {
  const { s, id } = lifted();
  const before = new Set(s.entities.map(e => e.id));
  const { from, path, gone } = throwPot(s, id);
  assert.ok(gone, 'the pot did not break within 3 s');
  assert.ok(path.length >= 10, `the pot flew for ${path.length} frames`);
  const land = path.at(-1), start = path[0];
  const ahead = from.z - land.z, aside = Math.abs(land.x - from.x);
  assert.ok(ahead >= 2 && ahead <= 12 && aside <= 1, `it landed ${ahead.toFixed(2)} m ahead, ${aside.toFixed(2)} m aside`);
  assert.ok(Math.max(...path.map(p => p.y)) >= start.y - 0.05 && land.y <= 0.8, 'it did not fly in an arc down to the ground');
  const debris = s.entities.filter(e => !before.has(e.id) && Math.hypot(e.pos.x - land.x, e.pos.z - land.z) < 1.5);
  assert.ok(debris.length > 0, 'nothing (pieces or a drop) where it landed');
});

test('a thrown pot hurts a slime it lands on', () => {
  const dry = lifted();
  const land = throwPot(dry.s, dry.id).path.at(-1);
  const { s, id } = lifted();
  const slime = g.spawn(s, 'slime', land.x, land.z);
  const hp = slime.hp;
  const { gone } = throwPot(s, id, () => Object.assign(slime.pos, { x: land.x, z: land.z }));
  simulate(g, s, {}, 0.1);
  const after = s.entities.find(e => e.id === slime.id && !e.removed);
  assert.ok(gone, 'the pot did not break');
  assert.ok(!after || after.hp < hp, 'the slime was not hurt');
});

test('in the page, E lifts a pot and E throws it', async () => {
  await withPage('', async page => {
    const id = await page.eval(`(() => {
      const s = island.state, pot = s.entities.find(e => e.kind === 'pot' && e.pos.x > 1);
      Object.assign(s.hero.pos, { x: pot.pos.x, z: pot.pos.z + 1.2 });
      s.hero.facing = Math.PI;
      return pot.id;
    })()`);
    await page.press('e');
    await sleep(400);
    const up = await page.eval(`(() => {
      const s = island.state, pot = s.entities.find(e => e.id === ${id});
      return pot ? pot.pos.y : (Object.values(s.hero).some(v => v && v.id === ${id}) ? 9 : -1);
    })()`);
    assert.ok(up >= 1.2, `the pot is at height ${up}`);
    await page.press('e');
    await sleep(1500);
    assert.equal(await page.eval(`island.state.entities.some(e => e.id === ${id})`), false, 'the thrown pot did not break');
  });
});
