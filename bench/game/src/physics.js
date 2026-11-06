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

// Moves a circle by (dx, dz) and returns whether it touched a box. The move goes in substeps of at most half the
// radius, so a fast roll at a low frame rate cannot jump a thin fence; each substep pushes the circle out along the
// contact normal, so it slides along a wall instead of stopping.
export function moveCircle(pos, r, dx, dz, boxes) {
  const steps = Math.max(1, Math.ceil(Math.hypot(dx, dz) / (r * 0.5)));
  let hit = false;
  for (let i = 0; i < steps; i++) {
    pos.x += dx / steps;
    pos.z += dz / steps;
    for (const box of boxes) if (pushOut(pos, r, box)) hit = true;
  }
  return hit;
}

export function pushOut(pos, r, box) {
  const hw = box.w / 2, hd = box.d / 2;
  const cx = clamp(pos.x, box.x - hw, box.x + hw), cz = clamp(pos.z, box.z - hd, box.z + hd);
  const nx = pos.x - cx, nz = pos.z - cz, d = Math.hypot(nx, nz);
  if (d >= r) return false;
  if (d > 1e-9) {
    pos.x = cx + (nx / d) * r;
    pos.z = cz + (nz / d) * r;
    return true;
  }
  // The centre is inside the box: out through the nearest side.
  const px = hw - Math.abs(pos.x - box.x), pz = hd - Math.abs(pos.z - box.z);
  if (px < pz) pos.x = box.x + Math.sign(pos.x - box.x || 1) * (hw + r);
  else pos.z = box.z + Math.sign(pos.z - box.z || 1) * (hd + r);
  return true;
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
