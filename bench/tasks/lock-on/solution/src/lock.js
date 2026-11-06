// Target lock: Q locks onto the nearest slime in a cone in front and releases it, Tab switches to the next slime in
// range; a lock ends when its slime is gone or far away. hero.target is the locked slime's id, or null.
import { angleDiff, angleTo, distance } from './math.js';
import { find } from './entities.js';

export const LOCK_RANGE = 10, LOCK_CONE = Math.PI / 4, LOCK_KEEP = 15;

export function updateLock(state, input) {
  const h = state.hero;
  const target = h.target && find(state, h.target);
  if (h.target && (!target || distance(h.pos, target.pos) > LOCK_KEEP)) h.target = null;
  if (input.lock) h.target = h.target ? null : (candidates(state, true)[0]?.id ?? null);
  if (input.switch && h.target) {
    const list = candidates(state, false);
    const at = list.findIndex(s => s.id === h.target);
    h.target = list[(at + 1) % list.length]?.id ?? h.target;
  }
}

// The slimes in lock range, nearest first; only those in the cone in front when `cone`.
function candidates(state, cone) {
  const h = state.hero;
  return state.entities
    .filter(e => e.kind === 'slime' && !e.removed && distance(h.pos, e.pos) <= LOCK_RANGE)
    .filter(e => !cone || Math.abs(angleDiff(angleTo(h.pos, e.pos), h.facing)) <= LOCK_CONE)
    .sort((a, b) => distance(h.pos, a.pos) - distance(h.pos, b.pos));
}

export const lockedTarget = state => (state.hero.target ? find(state, state.hero.target) : null);
