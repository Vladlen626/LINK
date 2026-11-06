// Frames of the room: before, the block on the switch with the gate open, the chest and the open door.
import { takeSheet } from '../../lib/sheet.mjs';

const walk = (seconds, input = '{}') => `for (let t = 0; t < ${seconds}; t += 1 / 60) island.step(${input}, 1 / 60);`;
const at = kind => `const e = island.state.entities.find(e => e.kind === '${kind}'); return { x: e.pos.x, z: e.pos.z };`;

export default ({ game, out }) => takeSheet({
  game, out, query: 'level=room', title: 'dungeon-room',
  scenes: [
    { name: '1-room', at: [0, 16, 9], look: [0, 0, -0.5], setup: null },
    { name: '2-pushed', at: [-3, 5, 6], look: [0, 0.5, 0], setup: new Function(`
      const s = island.state, b = s.entities.find(e => e.kind === 'block'), sw = s.entities.find(e => e.kind === 'switch');
      const dx = Math.sign(sw.pos.x - b.pos.x), dz = Math.sign(sw.pos.z - b.pos.z), alongX = Math.abs(sw.pos.x - b.pos.x) >= Math.abs(sw.pos.z - b.pos.z);
      Object.assign(s.hero.pos, alongX ? { x: b.pos.x - dx * 1.05, z: b.pos.z } : { x: b.pos.x, z: b.pos.z - dz * 1.05 });
      for (let t = 0; t < 8 && Math.hypot(b.pos.x - sw.pos.x, b.pos.z - sw.pos.z) > 0.05; t += 1 / 60) island.step(alongX ? { moveX: dx } : { moveZ: dz }, 1 / 60);
      ${walk(0.3)} ${at('gate')}`) },
    { name: '3-chest', at: [3, 4, 4], look: [0, 0.5, 0], setup: new Function(`
      const s = island.state, c = s.entities.find(e => e.kind === 'chest');
      Object.assign(s.hero.pos, { x: c.pos.x, z: c.pos.z + 1.2 }); s.hero.facing = Math.PI;
      island.step({ use: true }, 1 / 60); ${walk(0.5)} ${at('chest')}`) },
    { name: '4-door', at: [3, 4, -5], look: [0, 1, 0], setup: new Function(`
      const s = island.state, d = s.entities.find(e => e.kind === 'door');
      Object.assign(s.hero.pos, { x: d.pos.x, z: d.pos.z + 1.2 }); s.hero.facing = Math.PI;
      island.step({ use: true }, 1 / 60); ${walk(0.5)} ${at('door')}`) },
  ],
});
