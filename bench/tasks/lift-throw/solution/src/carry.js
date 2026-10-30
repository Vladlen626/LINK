// Lifting and throwing pots: the use key lifts the pot the hero faces overhead and throws the carried one forward in
// an arc; a thrown pot breaks where it lands, or on a slime or a wall it meets, and hurts the slime.
import { distance, forward } from './math.js';
import { overlaps, solids } from './physics.js';
import { facedThing } from './props.js';
import { breakApart } from './breakables.js';
import { hurtSlime } from './slime.js';
import { find } from './entities.js';

export const CARRY_HEIGHT = 1.5, CARRY_SLOW = 0.6, THROW_SPEED = 7, THROW_UP = 4, GRAVITY = 20;

// Returns whether the use key went to lifting or throwing (else it opens chests and doors).
export function liftOrThrow(state, input) {
  if (!input.use) return false;
  const h = state.hero;
  const carried = h.carrying && find(state, h.carrying);
  if (carried) {
    const f = forward(h.facing);
    carried.vel = { x: f.x * THROW_SPEED, y: THROW_UP, z: f.z * THROW_SPEED };
    carried.carried = false;
    carried.flying = true;
    h.carrying = null;
    state.events.push({ type: 'throw', id: carried.id });
    return true;
  }
  const pot = facedThing(state, ['pot']);
  if (!pot) return false;
  h.carrying = pot.id;
  pot.carried = true;
  pot.solid = false;
  state.events.push({ type: 'lift', id: pot.id });
  return true;
}

export function updateCarried(state, dt) {
  const h = state.hero;
  for (const pot of state.entities) {
    if (pot.carried) {
      pot.pos = { x: h.pos.x, y: CARRY_HEIGHT, z: h.pos.z };
      continue;
    }
    if (!pot.flying || pot.removed) continue;
    pot.vel.y -= GRAVITY * dt;
    pot.pos.x += pot.vel.x * dt;
    pot.pos.y += pot.vel.y * dt;
    pot.pos.z += pot.vel.z * dt;
    const slime = state.entities.find(e => e.kind === 'slime' && !e.removed && pot.pos.y < 1.2 && distance(e.pos, pot.pos) < e.r + 0.35);
    const wall = solids(state, pot).some(box => pot.pos.y < box.h && overlaps(pot.pos, 0.3, box));
    if (slime) hurtSlime(state, slime);
    if (slime || wall || pot.pos.y <= 0) {
      pot.pos.y = Math.max(0, pot.pos.y);
      breakApart(state, pot);
    }
  }
}
