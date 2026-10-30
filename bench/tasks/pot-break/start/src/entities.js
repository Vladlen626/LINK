// Everything on the island but the hero and the walls is an entity: { id, kind, pos: { x, y, z }, ...its own fields }.
import { ITEMS } from './items.gen.js';

// Each kind's fields when spawned. Solid entities block movement as boxes w x d, h high.
const KINDS = {
  pot: { solid: true, w: 0.7, d: 0.7, h: 0.8 },
  crate: { solid: true, w: 1, d: 1, h: 1 },
  chest: { solid: true, w: 1.1, d: 0.8, h: 0.8, contains: 'key', opened: false },
  door: { solid: true, w: 2, d: 0.4, h: 2.5, open: false },
  slime: { r: 0.45, hp: 2, mode: 'idle' },
};

export function spawn(state, kind, x, z, props = {}) {
  const pickup = ITEMS[kind] ? { r: 0.3, pickup: true } : {};
  const e = { id: state.nextId++, kind, pos: { x, y: 0, z }, ...pickup, ...structuredClone(KINDS[kind] || {}), ...props };
  state.entities.push(e);
  return e;
}

export const find = (state, id) => state.entities.find(e => e.id === id && !e.removed);
