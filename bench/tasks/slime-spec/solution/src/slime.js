// The slime, as docs/slime.md designs it: hops, notices the hero, chases, tells, lunges, blinks when hit.
import { angleTo, distance, forward } from './math.js';
import { blocked, moveCircle, solids } from './physics.js';
import { hurtHero } from './hero.js';
import { spawn } from './entities.js';

export const SIGHT = 6, GIVE_UP = 10, CHASE_SPEED = 2.5, TELL_RANGE = 2, TELL_TIME = 0.5;
export const LUNGE_SPEED = 6, LUNGE_TIME = 0.4, REST_TIME = 1, HURT_TIME = 0.6, HOP_EVERY = 1.2, HOP_HEIGHT = 0.4;

export function updateSlimes(state, dt) {
  for (const s of state.entities) if (s.kind === 'slime' && !s.removed) updateSlime(state, s, dt);
}

function updateSlime(state, s, dt) {
  const hero = state.hero;
  const d = distance(s.pos, hero.pos);
  s.timer = (s.timer || 0) + dt;
  s.hop = ((s.hop || 0) + dt) % HOP_EVERY;
  let speed = 0, dir = 0;

  switch (s.mode) {
    case 'idle':
      if (d <= SIGHT && sees(state, s)) enter(s, 'chase');
      break;
    case 'chase':
      if (d > GIVE_UP) enter(s, 'idle');
      else if (d <= TELL_RANGE) {
        enter(s, 'tell');
        s.lungeDir = angleTo(s.pos, hero.pos);
      } else {
        speed = CHASE_SPEED;
        dir = angleTo(s.pos, hero.pos);
      }
      break;
    case 'tell':
      if (s.timer >= TELL_TIME) enter(s, 'lunge');
      break;
    case 'lunge':
      speed = LUNGE_SPEED;
      dir = s.lungeDir;
      if (d < s.r + hero.r) hurtHero(state, 1, s.pos);
      if (s.timer >= LUNGE_TIME) enter(s, 'rest');
      break;
    case 'rest':
      if (s.timer >= REST_TIME) enter(s, 'chase');
      break;
    case 'hurt':
      if (s.timer >= HURT_TIME) enter(s, 'chase');
      break;
  }

  // Hops while idle or chasing; still while it tells, lunges, rests or blinks.
  const hops = s.mode === 'idle' || s.mode === 'chase';
  s.pos.y = hops && s.hop < 0.4 ? HOP_HEIGHT * Math.sin((Math.PI * s.hop) / 0.4) : 0;
  const f = forward(dir);
  const knock = s.knock || { x: 0, z: 0 };
  const fade = Math.max(0, 1 - 5 * dt);
  moveCircle(s.pos, s.r, (f.x * speed + knock.x) * dt, (f.z * speed + knock.z) * dt, solids(state));
  s.knock = { x: knock.x * fade, z: knock.z * fade };
}

function enter(s, mode) {
  s.mode = mode;
  s.timer = 0;
}

// Sight is blocked by walls and by solid things at least 1 m high.
function sees(state, s) {
  return !blocked(s.pos, state.hero.pos, solids(state).filter(box => box.h >= 1));
}

export function hurtSlime(state, s) {
  if (s.mode === 'hurt') return false;
  s.hp -= 1;
  state.events.push({ type: 'hit', id: s.id });
  if (s.hp <= 0) {
    s.removed = true;
    spawn(state, 'gem', s.pos.x, s.pos.z);
    state.events.push({ type: 'defeat', id: s.id });
    return true;
  }
  enter(s, 'hurt');
  const away = angleTo(state.hero.pos, s.pos);
  s.knock = { x: Math.sin(away) * 9, z: Math.cos(away) * 9 };
  return true;
}
