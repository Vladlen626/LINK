// The hero: walking, running, rolling, being hurt.
import { forward } from './math.js';
import { moveCircle, solids } from './physics.js';

export const WALK = 4, RUN = 7, ROLL_SPEED = 10, ROLL_TIME = 0.4, ROLL_REST = 0.2;

export function createHero({ x, z, facing }) {
  return {
    pos: { x, y: 0, z }, facing, r: 0.4,
    hp: 3, maxHp: 3, gems: 0, keys: 0,
    roll: 0, rollRest: 0, rollDir: facing, swing: 0, invuln: 0,
    knock: { x: 0, z: 0 }, spawn: { x, z, facing },
  };
}

export function updateHero(state, input, dt) {
  const h = state.hero;
  h.invuln = Math.max(0, h.invuln - dt);
  h.rollRest = Math.max(0, h.rollRest - dt);
  let mx = input.moveX || 0, mz = input.moveZ || 0;
  const len = Math.hypot(mx, mz);
  if (len > 1) { mx /= len; mz /= len; }

  if (input.roll && h.roll <= 0 && h.rollRest <= 0 && h.swing <= 0) {
    h.roll = ROLL_TIME;
    h.rollDir = len > 0.1 ? Math.atan2(mx, mz) : h.facing;
    h.facing = h.rollDir;
  }
  let vx, vz;
  if (h.roll > 0) {
    // Only the roll's time left moves at roll speed, so a roll is as long at any frame rate.
    const f = forward(h.rollDir), share = Math.min(dt, h.roll) / dt;
    vx = f.x * ROLL_SPEED * share;
    vz = f.z * ROLL_SPEED * share;
    h.roll -= dt;
    if (h.roll <= 0) h.rollRest = ROLL_REST;
  } else {
    // A swing roots the hero for its short time.
    const speed = h.swing > 0 ? 0 : input.run ? RUN : WALK;
    vx = mx * speed;
    vz = mz * speed;
    if (len > 0.1 && h.swing <= 0) h.facing = Math.atan2(mx, mz);
  }
  vx += h.knock.x;
  vz += h.knock.z;
  const fade = Math.max(0, 1 - 8 * dt);
  h.knock.x *= fade;
  h.knock.z *= fade;
  moveCircle(h.pos, h.r, vx * dt, vz * dt, solids(state));
  keepOnLand(h.pos, h.r, state.radius);
}

// The sea is out of bounds: the shore stops whatever walks to it.
export function keepOnLand(pos, r, radius) {
  const d = Math.hypot(pos.x, pos.z), max = radius - r;
  if (d > max) {
    pos.x *= max / d;
    pos.z *= max / d;
  }
}

// Takes hearts and knocks the hero away from `from`, unless the hero still blinks from the last hit.
export function hurtHero(state, amount, from) {
  const h = state.hero;
  if (h.invuln > 0) return false;
  h.hp = Math.max(0, h.hp - amount);
  h.invuln = 1;
  const dx = h.pos.x - from.x, dz = h.pos.z - from.z, d = Math.hypot(dx, dz) || 1;
  h.knock.x = (dx / d) * 10;
  h.knock.z = (dz / d) * 10;
  state.events.push({ type: 'hurt' });
  if (h.hp === 0) {
    // Fainting sends the hero back to the start with full hearts.
    Object.assign(h.pos, { x: h.spawn.x, z: h.spawn.z });
    h.facing = h.spawn.facing;
    h.hp = h.maxHp;
    h.knock.x = h.knock.z = 0;
    state.events.push({ type: 'faint' });
  }
  return true;
}
