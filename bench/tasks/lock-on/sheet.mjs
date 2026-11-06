// Frames of a lock onto a slime: the game's own camera, then fixed angles.
import { takeSheet } from '../../lib/sheet.mjs';

const walk = (seconds, input = '{}') => `for (let t = 0; t < ${seconds}; t += 1 / 60) island.step(${input}, 1 / 60);`;

export default ({ game, out }) => takeSheet({
  game, out, title: 'lock-on',
  scenes: [
    { name: '1-game-camera', at: null, setup: new Function(`
      const s = island.state, slime = s.entities.find(e => e.kind === 'slime' && e.pos.x < 0);
      Object.assign(s.hero.pos, { x: slime.pos.x + 0.5, z: slime.pos.z + 7.5 }); s.hero.facing = Math.PI;
      island.step({ lock: true }, 1 / 60); ${walk(1.5)} return { x: s.hero.pos.x, z: s.hero.pos.z };`) },
    { name: '2-side', at: [-4, 6, 8], look: [0, 0.5, -4], setup: null },
    { name: '3-strafed', at: [6, 6, 4], look: [0, 0.5, -4], setup: new Function(`
      ${walk(1.2, '{ moveX: 1 }')} const h = island.state.hero; return { x: h.pos.x, z: h.pos.z };`) },
    { name: '4-from-above', at: [0, 14, 2], look: [0, 0, -3], setup: new Function(`
      const h = island.state.hero; return { x: h.pos.x, z: h.pos.z };`) },
  ],
});
