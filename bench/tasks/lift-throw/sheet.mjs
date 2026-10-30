// Frames of a pot lifted, carried and thrown, from fixed angles.
import { takeSheet } from '../../lib/sheet.mjs';

const walk = (seconds, input = '{}') => `for (let t = 0; t < ${seconds}; t += 1 / 60) island.step(${input}, 1 / 60);`;
const here = 'const h = island.state.hero; return { x: h.pos.x, z: h.pos.z };';

export default ({ game, out }) => takeSheet({
  game, out, title: 'lift-throw',
  scenes: [
    { name: '1-lifted', at: [3.5, 2.5, 2.5], look: [0, 1, 0], setup: new Function(`
      const s = island.state, pot = s.entities.find(e => e.kind === 'pot' && e.pos.x > 1);
      Object.assign(s.hero.pos, { x: pot.pos.x, z: pot.pos.z + 1.2 }); s.hero.facing = Math.PI;
      island.step({ use: true }, 1 / 60); ${walk(0.3)} ${here}`) },
    { name: '2-carried', at: [-4, 2.5, 3], look: [0, 1, 0], setup: new Function(`${walk(0.6, '{ moveX: -1 }')} ${walk(0.2, '{ moveZ: -1 }')} ${here}`) },
    { name: '3-flying', at: [4.5, 2, -1.5], look: [0, 1, -2], setup: new Function(`
      island.step({ use: true }, 1 / 60); ${walk(0.2)} ${here}`) },
    { name: '4-landed', at: [3, 4, 0], look: [0, 0, -3.5], setup: new Function(`${walk(0.8)} ${here}`) },
  ],
});
