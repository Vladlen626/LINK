// The dungeon room's mechanics: the hero pushes a block by walking into it; a block on a floor switch opens the gate.
import { distance } from './math.js';
import { solids } from './physics.js';

export const PUSH_SPEED = 1.5;

const boxesOverlap = (a, b) => Math.abs(a.x - b.x) < (a.w + b.w) / 2 && Math.abs(a.z - b.z) < (a.d + b.d) / 2;

// A block slides along the walk's main axis while the hero walks into its face, unless something is in the way.
export function pushBlocks(state, input, dt) {
  const h = state.hero, mx = input.moveX || 0, mz = input.moveZ || 0;
  if (!mx && !mz) return;
  const alongX = Math.abs(mx) >= Math.abs(mz), dir = Math.sign(alongX ? mx : mz);
  for (const b of state.entities) {
    if (b.kind !== 'block') continue;
    const ahead = alongX ? b.pos.x - h.pos.x : b.pos.z - h.pos.z;
    const across = alongX ? b.pos.z - h.pos.z : b.pos.x - h.pos.x;
    const reach = (alongX ? b.w : b.d) / 2 + h.r + 0.05, face = (alongX ? b.d : b.w) / 2;
    if (Math.sign(ahead) !== dir || Math.abs(ahead) > reach || Math.abs(across) > face) continue;
    const next = { x: b.pos.x + (alongX ? dir * PUSH_SPEED * dt : 0), z: b.pos.z + (alongX ? 0 : dir * PUSH_SPEED * dt), w: b.w - 0.02, d: b.d - 0.02 };
    if (solids(state, b).some(box => boxesOverlap(next, box))) continue;
    b.pos.x = next.x;
    b.pos.z = next.z;
  }
}

// Each switch is pressed while a block sits on it; the gates open while every switch is pressed.
export function updateSwitches(state) {
  const blocks = state.entities.filter(e => e.kind === 'block');
  const switches = state.entities.filter(e => e.kind === 'switch');
  for (const sw of switches) sw.pressed = blocks.some(b => distance(b.pos, sw.pos) < 0.5);
  const open = switches.length > 0 && switches.every(sw => sw.pressed);
  for (const gate of state.entities) {
    if (gate.kind !== 'gate') continue;
    gate.open = open;
    gate.solid = !open;
  }
}
