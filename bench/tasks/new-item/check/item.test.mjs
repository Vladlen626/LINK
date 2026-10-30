import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { GAME, game, simulate, withPage, sleep } from '../lib/kit.mjs';

const g = await game();
const read = file => fs.readFileSync(path.join(GAME, file), 'utf8').replace(/\r\n/g, '\n');

test('heart-piece is in data/items.json, and src/items.gen.js is what tools/gen-items.mjs makes of it', () => {
  assert.match(read('data/items.json'), /"heart-piece"/, 'data/items.json has no heart-piece');
  const generated = read('src/items.gen.js');
  assert.match(generated, /heart-piece/, 'src/items.gen.js has no heart-piece');
  execFileSync(process.execPath, ['tools/gen-items.mjs'], { cwd: GAME, stdio: 'ignore' });
  assert.equal(generated, read('src/items.gen.js'), 'src/items.gen.js differs from the generator\'s output: edited by hand');
});

// Walks the hero onto `count` heart pieces, one after another.
function collect(s, count) {
  for (let i = 0; i < count; i++) {
    const piece = g.spawn(s, 'heart-piece', s.hero.pos.x + 1, s.hero.pos.z);
    simulate(g, s, { moveX: 1 }, 0.5);
    assert.ok(!s.entities.some(e => e.id === piece.id && !e.removed), 'the hero did not pick up a heart piece');
  }
}

test('three heart pieces add no heart; the fourth adds one and fills the hearts', () => {
  const s = g.createGame({ level: 'empty' });
  s.hero.hp = 1;
  const max = s.hero.maxHp;
  collect(s, 3);
  assert.equal(s.hero.maxHp, max, 'three pieces added a heart');
  assert.equal(s.hero.hp, 1, 'a piece healed');
  collect(s, 1);
  assert.equal(s.hero.maxHp, max + 1, 'four pieces did not add a heart');
  assert.equal(s.hero.hp, max + 1, 'the new heart did not fill the hearts');
});

test('eight heart pieces make two hearts', () => {
  const s = g.createGame({ level: 'empty' });
  const max = s.hero.maxHp;
  collect(s, 8);
  assert.equal(s.hero.maxHp, max + 2);
});

test('in the page, a heart piece shows and is picked up', async () => {
  await withPage('', async page => {
    const before = await page.eval(`(async () => {
      const g = await import('/src/game.js');
      const s = island.state;
      g.spawn(s, 'heart-piece', s.hero.pos.x + 1, s.hero.pos.z);
      return s.hero.maxHp;
    })()`);
    await sleep(300);
    const after = await page.eval(`(async () => {
      const g = await import('/src/game.js');
      const s = island.state;
      for (let i = 0; i < 3; i++) g.spawn(s, 'heart-piece', s.hero.pos.x + 1 + i * 0.3, s.hero.pos.z);
      for (let t = 0; t < 1.5; t += 1 / 60) island.step({ moveX: 1 }, 1 / 60);
      return { max: island.state.hero.maxHp, left: island.state.entities.filter(e => e.kind === 'heart-piece').length };
    })()`);
    assert.equal(after.left, 0, 'heart pieces were not picked up');
    assert.equal(after.max, before + 1, 'four pieces in the page did not add a heart');
  });
});
