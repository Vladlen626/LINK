import test from 'node:test';
import assert from 'node:assert/strict';
import { game, simulate, alive, withPage, sleep } from '../lib/kit.mjs';

const g = await game();
const PICKUPS = ['gem', 'heart', 'key', 'heart-piece'];
const empty = seed => g.createGame({ seed, level: 'empty' });
const swing = (s, after = 0.6) => {
  g.step(s, { attack: true });
  simulate(g, s, {}, after);
};

test('one swing breaks the pot in front, not one behind or one out of reach', () => {
  const s = empty(1);
  const front = g.spawn(s, 'pot', 0, -1.4);
  const behind = g.spawn(s, 'pot', 0, 1.3);
  const far = g.spawn(s, 'pot', 0, -4.5);
  swing(s);
  assert.ok(!alive(s, front.id), 'the pot in front still stands');
  assert.ok(alive(s, behind.id), 'the pot behind broke');
  assert.ok(alive(s, far.id), 'the pot out of reach broke');
});

test('a crate takes three hits', () => {
  const s = empty(1);
  const crate = g.spawn(s, 'crate', 0, -1.5);
  swing(s);
  swing(s);
  assert.ok(alive(s, crate.id), 'the crate broke in two hits');
  swing(s);
  assert.ok(!alive(s, crate.id), 'the crate stands after three hits');
});

// The entities a break adds that are not pickups: its pieces.
function breakPot(s) {
  const pot = g.spawn(s, 'pot', 0, -1.4);
  const before = new Set(s.entities.map(e => e.id));
  g.step(s, { attack: true });
  return { pot, pieces: () => s.entities.filter(e => !before.has(e.id) && !PICKUPS.includes(e.kind) && e.id !== pot.id) };
}

test('a break leaves pieces in the game state that fly out', () => {
  const s = empty(3);
  const { pot, pieces } = breakPot(s);
  let count = 0, high = 0, wide = 0;
  simulate(g, s, {}, 0.6, {
    each: () => {
      count = Math.max(count, pieces().length);
      for (const p of pieces()) {
        high = Math.max(high, p.pos.y ?? 0);
        wide = Math.max(wide, Math.hypot(p.pos.x - pot.pos.x, p.pos.z - pot.pos.z));
      }
    },
  });
  assert.ok(count >= 3, `${count} pieces in the state`);
  assert.ok(high > 0.2, `the pieces rose to ${high.toFixed(2)} m`);
  assert.ok(wide > 0.3, `the pieces flew ${wide.toFixed(2)} m out`);
});

test('the pieces land and rest on the ground, never below it', () => {
  const s = empty(4);
  const { pieces } = breakPot(s);
  let lowest = Infinity, rested = false;
  const last = new Map();
  simulate(g, s, {}, 3, {
    each: () => {
      for (const p of pieces()) {
        lowest = Math.min(lowest, p.pos.y ?? 0);
        const was = last.get(p.id);
        const still = was && Math.hypot(p.pos.x - was.x, (p.pos.y ?? 0) - was.y, p.pos.z - was.z) < 0.002;
        if (still && (p.pos.y ?? 0) <= 0.3) rested = true;
        last.set(p.id, { x: p.pos.x, y: p.pos.y ?? 0, z: p.pos.z });
      }
    },
  });
  assert.ok(last.size > 0, 'no pieces');
  assert.ok(lowest >= -0.02, `a piece went down to y ${lowest.toFixed(2)}`);
  assert.ok(rested, 'no piece came to rest on the ground');
});

// What 30 pots drop, one after another: the kinds of new pickups and what the hero collected at once.
function drops(seed) {
  const s = empty(seed);
  const out = [];
  for (let i = 0; i < 30; i++) {
    Object.assign(s.hero, { hp: 1, maxHp: 99 });
    const before = new Set(s.entities.map(e => e.id));
    const { gems } = s.hero;
    const pot = g.spawn(s, 'pot', 0, -1.4);
    before.add(pot.id);
    swing(s, 1);
    const fresh = s.entities.filter(e => !before.has(e.id));
    const kinds = fresh.filter(e => PICKUPS.includes(e.kind)).map(e => e.kind);
    for (let n = 0; n < s.hero.gems - gems; n++) kinds.push('gem');
    for (let n = 0; n < s.hero.hp - 1; n++) kinds.push('heart');
    out.push(kinds.sort().join('+') || '-');
    s.entities = s.entities.filter(e => before.has(e.id) && e.id !== pot.id);
  }
  return out;
}

test('each break drops gems or hearts, decided by the seed', () => {
  const a = drops(7);
  const all = a.flatMap(d => d.split('+')).filter(k => k !== '-');
  assert.ok(all.every(k => k === 'gem' || k === 'heart'), `drops: ${[...new Set(all)].join(', ')}`);
  assert.ok(all.includes('gem') && all.includes('heart'), `30 pots dropped ${all.length}: ${[...new Set(all)].join(', ') || 'nothing'}`);
  assert.deepEqual(drops(7), a, 'the same seed dropped other things');
  assert.notDeepEqual(drops(8), a, 'another seed dropped the very same things');
});

test('in the page, the sword breaks a pot', async () => {
  await withPage('', async page => {
    const id = await page.eval(`(() => {
      const s = island.state, pot = s.entities.find(e => e.kind === 'pot');
      Object.assign(s.hero.pos, { x: pot.pos.x, z: pot.pos.z + 1.3 });
      s.hero.facing = Math.PI;
      return pot.id;
    })()`);
    await page.press(' ');
    await sleep(400);
    assert.equal(await page.eval(`island.state.entities.some(e => e.id === ${id})`), false, 'the pot still stands');
  });
});
