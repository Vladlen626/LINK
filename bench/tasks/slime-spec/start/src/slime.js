// The slime, for now: it creeps toward a hero close by and hurts on touch. Its design is in docs/slime.md.
import { angleTo, distance } from './math.js';
import { moveCircle, solids } from './physics.js';
import { hurtHero } from './hero.js';

export function updateSlimes(state, dt) {
  const hero = state.hero;
  for (const s of state.entities) {
    if (s.kind !== 'slime' || s.removed) continue;
    const d = distance(s.pos, hero.pos);
    if (d < 5) {
      const dir = angleTo(s.pos, hero.pos);
      moveCircle(s.pos, s.r, Math.sin(dir) * 1.5 * dt, Math.cos(dir) * 1.5 * dt, solids(state));
    }
    if (d < s.r + hero.r + 0.05) hurtHero(state, 1, s.pos);
  }
}

export function hurtSlime(state, s) {
  s.hp -= 1;
  if (s.hp <= 0) s.removed = true;
  return true;
}
