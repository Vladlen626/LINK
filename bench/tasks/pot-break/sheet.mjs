// Frames of a pot and a crate breaking, from fixed angles.
import { takeSheet } from '../../lib/sheet.mjs';

const walk = seconds => `for (let t = 0; t < ${seconds}; t += 1 / 60) island.step({}, 1 / 60);`;

export default ({ game, out }) => takeSheet({
  game, out, title: 'pot-break',
  scenes: [
    { name: '1-before', at: [3, 2.2, 3], look: [0, 0.4, 0], setup: new Function(`
      const s = island.state, pot = s.entities.find(e => e.kind === 'pot' && e.pos.x > 1);
      Object.assign(s.hero.pos, { x: pot.pos.x, z: pot.pos.z + 1.3 }); s.hero.facing = Math.PI; island.step({}, 1 / 60);
      return { x: pot.pos.x, z: pot.pos.z };`) },
    { name: '2-burst', at: [3, 2.2, 3], look: [0, 0.4, 0], setup: new Function(`
      const h = island.state.hero; island.step({ attack: true }, 1 / 60); ${walk(0.12)}
      return { x: h.pos.x, z: h.pos.z - 1.3 };`) },
    { name: '3-settled', at: [0.5, 5, 3], look: [0, 0, 0], setup: new Function(`
      const h = island.state.hero; ${walk(1.5)} return { x: h.pos.x, z: h.pos.z - 1.3 };`) },
    { name: '4-crate', at: [-3, 2.5, 3], look: [0, 0.5, 0], setup: new Function(`
      const s = island.state, crate = s.entities.find(e => e.kind === 'crate');
      Object.assign(s.hero.pos, { x: crate.pos.x, z: crate.pos.z + 1.5 }); s.hero.facing = Math.PI;
      for (let i = 0; i < 3; i++) { island.step({ attack: true }, 1 / 60); ${walk(0.5)} }
      return { x: crate.pos.x, z: crate.pos.z };`) },
  ],
});
