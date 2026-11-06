// Small helpers shared by the game's logic. The ground is the x-z plane, y is up.

export const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

// The direction a facing angle looks: 0 faces +z, PI/2 faces +x.
export const forward = facing => ({ x: Math.sin(facing), z: Math.cos(facing) });

export const angleTo = (from, to) => Math.atan2(to.x - from.x, to.z - from.z);

// The signed difference a - b, wrapped into -PI..PI.
export const angleDiff = (a, b) => Math.atan2(Math.sin(a - b), Math.cos(a - b));

export const distance = (a, b) => Math.hypot(a.x - b.x, a.z - b.z);

// Seeded randomness (mulberry32) kept in the state, so a seed replays the same game and the state saves as JSON.
export function random(state) {
  let t = (state.seed = (state.seed + 0x6D2B79F5) | 0);
  t = Math.imul(t ^ (t >>> 15), t | 1);
  t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
}
