// Pots and crates: the sword breaks them (a pot at once, a crate in three hits) into pieces that fly and settle, and
// each leaves a gem or a heart, from the game's seed.
import { random } from './math.js';
import { DROPS } from './items.gen.js';
import { spawn } from './entities.js';

const GRAVITY = 20, PIECE_LIFE = 4;
const LOOK = { pot: { pieces: 6, color: '#c8743c' }, crate: { pieces: 8, color: '#9a6a3a' } };

export function damageBreakable(state, e) {
  e.hp -= 1;
  e.shakeUntil = state.time + 0.2;
  state.events.push({ type: 'hit', id: e.id });
  if (e.hp <= 0) breakApart(state, e);
}

export function breakApart(state, e) {
  e.removed = true;
  state.events.push({ type: 'break', id: e.id });
  const look = LOOK[e.kind] || LOOK.pot;
  for (let i = 0; i < look.pieces; i++) {
    const angle = (i / look.pieces) * Math.PI * 2 + random(state);
    const speed = 1.5 + random(state) * 2;
    spawn(state, 'piece', e.pos.x, e.pos.z, {
      pos: { x: e.pos.x, y: (e.pos.y || 0) + 0.4, z: e.pos.z },
      vel: { x: Math.sin(angle) * speed, y: 3 + random(state) * 3, z: Math.cos(angle) * speed },
      life: PIECE_LIFE, color: look.color, size: 0.12 + random(state) * 0.1,
    });
  }
  spawn(state, pickDrop(state), e.pos.x, e.pos.z);
}

// One of the droppable items, by their weights.
function pickDrop(state) {
  const total = DROPS.reduce((sum, d) => sum + d.weight, 0);
  let roll = random(state) * total;
  for (const d of DROPS) if ((roll -= d.weight) < 0) return d.id;
  return DROPS[DROPS.length - 1].id;
}

// Pieces fall, bounce low on the ground, slide to a stop and fade away.
export function updatePieces(state, dt) {
  for (const p of state.entities) {
    if (p.kind !== 'piece') continue;
    p.life -= dt;
    if (p.life <= 0) { p.removed = true; continue; }
    if (p.rest) continue;
    p.vel.y -= GRAVITY * dt;
    p.pos.x += p.vel.x * dt;
    p.pos.y += p.vel.y * dt;
    p.pos.z += p.vel.z * dt;
    if (p.pos.y <= 0) {
      p.pos.y = 0;
      p.vel.y = Math.abs(p.vel.y) > 2 ? -p.vel.y * 0.3 : 0;
      p.vel.x *= 0.5;
      p.vel.z *= 0.5;
      if (p.vel.y === 0 && Math.hypot(p.vel.x, p.vel.z) < 0.2) p.rest = true;
    }
  }
}
