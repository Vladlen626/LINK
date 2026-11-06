// The levels: walls (boxes), entities to spawn and grass. 'island' is the game; 'empty' is flat ground for tests.
import { createMeadow } from './grass.js';

export const ISLAND_RADIUS = 18;

const wall = (x, z, w, d, h = 2.5) => ({ x, z, w, d, h });

export function createLevel(name) {
  if (name === 'empty') return { hero: { x: 0, z: 0, facing: Math.PI }, walls: [], entities: [], grass: [], radius: 60 };
  if (name !== 'island') throw new Error(`no level '${name}'`);
  return {
    hero: { x: 0, z: 6, facing: Math.PI },
    radius: ISLAND_RADIUS,
    walls: [
      // A ruined yard in the north, its gate shut by a locked door.
      wall(0, -12, 10, 0.6), wall(-5, -9, 0.6, 6), wall(5, -9, 0.6, 6), wall(-3, -6, 4, 0.6), wall(3, -6, 4, 0.6),
      // A low fence west of the start, and rocks.
      wall(-3.5, -2, 5, 0.12, 1), wall(-10, 4, 2, 1.5, 1.2), wall(10, 10, 1.5, 2, 1.2),
    ],
    entities: [
      { kind: 'pot', x: -2, z: 3 }, { kind: 'pot', x: -3, z: 3.5 }, { kind: 'pot', x: 2, z: 2.5 }, { kind: 'pot', x: -1, z: 0 },
      { kind: 'crate', x: 4, z: 0 }, { kind: 'crate', x: 5.2, z: 0.3 },
      { kind: 'chest', x: 7, z: -3, contains: 'key' },
      { kind: 'door', x: 0, z: -6 },
      { kind: 'slime', x: -9, z: -5 }, { kind: 'slime', x: 10, z: -8 },
      { kind: 'gem', x: -2, z: -9 }, { kind: 'gem', x: 0, z: -9 }, { kind: 'gem', x: 2, z: -9 }, { kind: 'heart', x: 0, z: -10.5 },
      { kind: 'gem', x: -12, z: 8 },
    ],
    grass: createMeadow(),
  };
}
