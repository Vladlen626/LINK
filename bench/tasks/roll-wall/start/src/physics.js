// Circles against boxes on the ground plane. A box is { x, z, w, d, h }: its centre, width (x), depth (z) and height.
import { clamp } from './math.js';

// The boxes that block movement: the level's walls and every solid entity (a pot, a crate, a closed door).
export function solids(state, except) {
  const boxes = state.walls.slice();
  for (const e of state.entities) {
    if (e.solid && e !== except) boxes.push({ x: e.pos.x, z: e.pos.z, w: e.w, d: e.d, h: e.h });
  }
  return boxes;
}

// Moves a circle by (dx, dz) and returns whether it touched a box: a move that would end in a box is not made.
export function moveCircle(pos, r, dx, dz, boxes) {
  const next = { x: pos.x + dx, z: pos.z + dz };
  if (boxes.some(box => overlaps(next, r, box))) return true;
  pos.x = next.x;
  pos.z = next.z;
  return false;
}

export const overlaps = (pos, r, box) =>
  Math.hypot(pos.x - clamp(pos.x, box.x - box.w / 2, box.x + box.w / 2), pos.z - clamp(pos.z, box.z - box.d / 2, box.z + box.d / 2)) < r - 1e-6;

// Where the segment a-b first enters a box, as a share 0..1 of the way, or null (a slab test on the ground plane).
export function segmentHit(a, b, box) {
  let t0 = 0, t1 = 1;
  for (const [p, q, c, half] of [[a.x, b.x, box.x, box.w / 2], [a.z, b.z, box.z, box.d / 2]]) {
    const dir = q - p;
    if (Math.abs(dir) < 1e-9) {
      if (Math.abs(p - c) > half) return null;
      continue;
    }
    let u0 = (c - half - p) / dir, u1 = (c + half - p) / dir;
    if (u0 > u1) [u0, u1] = [u1, u0];
    t0 = Math.max(t0, u0);
    t1 = Math.min(t1, u1);
    if (t0 > t1) return null;
  }
  return t0;
}

// Whether a box of at least the given height stands between two points: sight lines and the camera.
export const blocked = (a, b, boxes, minHeight = 0) =>
  boxes.some(box => box.h >= minHeight && segmentHit(a, b, box) !== null);
