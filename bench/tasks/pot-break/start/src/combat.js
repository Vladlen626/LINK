// The sword: a swing hits what is in an arc in front of the hero, the moment it starts.
import { angleDiff, angleTo, distance } from './math.js';
import { hurtSlime } from './slime.js';
import { cutGrass } from './grass.js';

export const SWING_TIME = 0.3, REACH = 1.6, ARC = Math.PI * 0.6;

export function updateCombat(state, input, dt) {
  const h = state.hero;
  h.swing = Math.max(0, h.swing - dt);
  if (!input.attack || h.swing > 0 || h.roll > 0) return;
  h.swing = SWING_TIME;
  state.events.push({ type: 'swing' });
  for (const e of state.entities) {
    if (e.removed || !inArc(h, e.pos, (e.w || 2 * (e.r || 0)) / 2)) continue;
    if (e.kind === 'slime') hurtSlime(state, e);
  }
  cutGrass(state, blade => inArc(h, blade, 0));
}

// Whether a point (with some size of its own) is within the sword's reach and arc.
export function inArc(hero, pos, size = 0) {
  const d = distance(hero.pos, pos);
  if (d > REACH + size) return false;
  return d < hero.r + size || Math.abs(angleDiff(angleTo(hero.pos, pos), hero.facing)) <= ARC / 2;
}
