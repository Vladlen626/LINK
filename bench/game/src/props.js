// Things the hero uses (the use key): a chest opens and gives what it holds, a locked door takes a small key.
import { angleDiff, angleTo } from './math.js';
import { spawn } from './entities.js';

export const USE_RANGE = 1.6;

// The usable thing the hero faces, close enough: measured to its nearest edge, within 60 degrees of the facing.
export function facedThing(state, kinds) {
  const h = state.hero;
  let best = null, bestD = Infinity;
  for (const e of state.entities) {
    if (e.removed || !kinds.includes(e.kind)) continue;
    const dx = Math.max(Math.abs(h.pos.x - e.pos.x) - (e.w || 0) / 2, 0);
    const dz = Math.max(Math.abs(h.pos.z - e.pos.z) - (e.d || 0) / 2, 0);
    const d = Math.hypot(dx, dz);
    if (d > USE_RANGE || Math.abs(angleDiff(angleTo(h.pos, e.pos), h.facing)) > Math.PI / 3 || d >= bestD) continue;
    best = e;
    bestD = d;
  }
  return best;
}

export function updateProps(state, input) {
  if (!input.use) return;
  const e = facedThing(state, ['chest', 'door']);
  if (!e) return;
  if (e.kind === 'chest' && !e.opened) {
    e.opened = true;
    // What the chest holds drops at the hero's feet and is picked up at once.
    if (e.contains) spawn(state, e.contains, state.hero.pos.x, state.hero.pos.z);
    state.events.push({ type: 'open', id: e.id });
  } else if (e.kind === 'door' && !e.open) {
    if (state.hero.keys > 0) {
      state.hero.keys -= 1;
      e.open = true;
      e.solid = false;
      state.events.push({ type: 'unlock', id: e.id });
    } else {
      state.events.push({ type: 'locked', id: e.id });
    }
  }
}
