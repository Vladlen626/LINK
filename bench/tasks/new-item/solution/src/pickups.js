// Pickups (gems, hearts, heart pieces, keys: data/items.json) are collected by walking onto them; each adds its effect to the hero.
import { distance } from './math.js';
import { ITEMS } from './items.gen.js';

export const PICKUP_RANGE = 0.4;

export function collectPickups(state) {
  const h = state.hero;
  for (const e of state.entities) {
    if (e.removed || !e.pickup || distance(h.pos, e.pos) > h.r + e.r + PICKUP_RANGE) continue;
    e.removed = true;
    for (const [stat, amount] of Object.entries(ITEMS[e.kind]?.effect || {})) h[stat] = (h[stat] || 0) + amount;
    // Four heart pieces make a new heart, and fill the hearts.
    if (h.heartPieces >= 4) {
      h.heartPieces -= 4;
      h.maxHp += 1;
      h.hp = h.maxHp;
    }
    h.hp = Math.min(h.hp, h.maxHp);
    state.events.push({ type: 'pickup', kind: e.kind });
  }
}
