// The third-person camera: behind and above the hero (looking north), following smoothly; a tall wall between it and
// the hero pulls it in, so the hero stays in sight.
import { segmentHit, solids } from './physics.js';

export const CAMERA_OFFSET = { x: 0, y: 6, z: 7 }, FOV = 55;

export function createCamera() {
  return { pos: { x: 0, y: 0, z: 0 }, look: { x: 0, y: 0, z: 0 }, fov: FOV };
}

export function updateCamera(state, dt) {
  const h = state.hero, cam = state.camera;
  const look = { x: h.pos.x, y: 0.8, z: h.pos.z };
  const want = { x: look.x + CAMERA_OFFSET.x, y: CAMERA_OFFSET.y, z: look.z + CAMERA_OFFSET.z };
  avoidWalls(state, look, want);
  const k = 1 - Math.exp(-6 * dt);
  for (const axis of ['x', 'y', 'z']) {
    cam.pos[axis] += (want[axis] - cam.pos[axis]) * k;
    cam.look[axis] += (look[axis] - cam.look[axis]) * k;
  }
}

// Pulls the wanted position toward the look point to just before the first tall box in between.
export function avoidWalls(state, look, want) {
  let t = 1;
  for (const box of solids(state)) {
    if (box.h < 2) continue;
    const hit = segmentHit(look, want, box);
    if (hit !== null) t = Math.min(t, hit);
  }
  if (t >= 1) return;
  const keep = Math.max(0.15, t - 0.05);
  for (const axis of ['x', 'y', 'z']) want[axis] = look[axis] + (want[axis] - look[axis]) * keep;
}
