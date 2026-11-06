// Grass: blades { x, z, cut } in state.grass. A sword swing cuts the blades in its arc.

export const MEADOW = { blades: 600, x: 6, z: 2, w: 8, d: 8 };

// The meadow's layout is the level's, the same in every game: its own small generator, not the game's seed.
export function createMeadow({ blades, x, z, w, d } = MEADOW) {
  let seed = 12345;
  const next = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  const grass = [];
  // Only on the island's turf.
  while (grass.length < blades) {
    const blade = { x: x + next() * w, z: z + next() * d, cut: false };
    if (Math.hypot(blade.x, blade.z) < 16.5) grass.push(blade);
  }
  return grass;
}

// Cuts the blades `hits` says the swing reaches; the drawing redraws the grass when grassVersion changes.
export function cutGrass(state, hits) {
  let cut = 0;
  for (const blade of state.grass) {
    if (!blade.cut && hits(blade)) {
      blade.cut = true;
      cut++;
    }
  }
  if (cut) state.grassVersion = (state.grassVersion || 0) + 1;
  return cut;
}
