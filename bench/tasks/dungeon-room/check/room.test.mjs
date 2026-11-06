import test from 'node:test';
import assert from 'node:assert/strict';
import { game, simulate, withPage, sleep } from '../lib/kit.mjs';
import { playRoom, roomSummary } from './room.mjs';

const g = await game();
const room = () => {
  const s = g.createGame({ level: 'room' });
  return { s, api: { state: () => s, step: (input, dt) => g.step(s, input, dt) } };
};
const inPage = (page, parts) => page.eval(`(${playRoom})({ state: () => island.state, step: (i, dt) => island.step(i, dt) }, ${JSON.stringify(parts)})`);

test('the room has a block, a switch, a gate, a chest and a door, and the gate blocks the way', () => {
  const { api } = room();
  const { kinds, crossed } = playRoom(api, ['kinds', 'cross']);
  assert.deepEqual(kinds, ['block', 'switch', 'gate', 'chest', 'door']);
  assert.equal(crossed, false, 'the hero walked through the closed gate');
});

test('pushing the block onto the switch opens the gate', () => {
  const { api } = room();
  const r = playRoom(api, ['push', 'cross']);
  assert.ok(r.onSwitch, 'the block did not get onto the switch');
  assert.ok(r.gateOpen, 'the gate is not open');
  assert.ok(r.crossed, 'the hero cannot walk through the open gate');
});

test('the chest gives a small key, once', () => {
  const { api } = room();
  const r = playRoom(api, ['push', 'cross', 'chest']);
  assert.ok(r.chestKey, 'no key from the chest');
  assert.ok(r.chestOnce, 'the chest gave another key');
});

test('the locked door stays shut without the key and opens with it', () => {
  const { api } = room();
  assert.equal(playRoom(api, ['door']).doorOpen, false, 'the door opened without a key');
  const r = playRoom(api, ['push', 'cross', 'chest', 'door']);
  assert.ok(r.doorOpen, 'the key did not open the door');
  assert.equal(r.keys, 0, 'the key was not used up');
});

test('save(state) and load(text) bring the room back as it was', () => {
  const { s, api } = room();
  playRoom(api, ['push', 'cross', 'chest']);
  const text = g.save(s);
  assert.equal(typeof text, 'string');
  const loaded = g.load(text);
  assert.deepEqual(roomSummary(loaded), roomSummary(s));
  simulate(g, loaded, {}, 1);
  simulate(g, s, {}, 1);
  assert.deepEqual(roomSummary(loaded), roomSummary(s), 'the loaded room went on differently');
  assert.ok(roomSummary(loaded).things.some(t => t.startsWith('gate') && t.endsWith('open')) || !loaded.entities.some(e => e.kind === 'gate'), 'the gate closed after loading');
});

test('in the page, the block opens the gate, the chest gives the key and the key opens the door', async () => {
  await withPage('level=room', async page => {
    const r = await inPage(page, ['push', 'cross', 'chest', 'door']);
    assert.ok(r.onSwitch && r.gateOpen && r.crossed, `the gate: ${JSON.stringify(r)}`);
    assert.ok(r.chestKey && r.doorOpen, `the chest and the door: ${JSON.stringify(r)}`);
  });
});

test('in the page, the room is as it was after a reload', async () => {
  await withPage('level=room', async page => {
    await inPage(page, ['push', 'cross', 'chest', 'door']);
    const before = await page.eval(`(${roomSummary})(island.state)`);
    await sleep(2000);
    await page.reload({ until: 'window.island && window.island.ready' });
    await sleep(300);
    const after = await page.eval(`(${roomSummary})(island.state)`);
    assert.deepEqual(after.things, before.things);
    assert.ok(after.things.some(t => t.startsWith('door') && t.endsWith('open')), 'the door is shut again');
  });
});
