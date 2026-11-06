// Grass: blades { x, z, cut } in state.grass. A sword swing cuts the blades in its arc.

export const MEADOW = { blades: 20000, x: 2, z: 0, w: 13, d: 13 };

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

// The blades by 1 m cell, built once per meadow: a swing looks only at the cells around the hero.
const grids = new WeakMap();
function gridOf(grass) {
  if (!grids.has(grass)) {
    const cells = new Map();
    for (const blade of grass) {
      const key = cellKey(Math.floor(blade.x), Math.floor(blade.z));
      if (!cells.has(key)) cells.set(key, []);
      cells.get(key).push(blade);
    }
    grids.set(grass, cells);
  }
  return grids.get(grass);
}
const cellKey = (cx, cz) => cx * 4096 + cz;

// Cuts the blades `hits` says the swing reaches, among those within `radius` of the hero (the sword reaches less);
// the drawing redraws the grass when grassVersion changes.
export function cutGrass(state, hits, radius = 2.5) {
  const cells = gridOf(state.grass), h = state.hero.pos;
  let cut = 0;
  for (let cx = Math.floor(h.x - radius); cx <= Math.floor(h.x + radius); cx++) {
    for (let cz = Math.floor(h.z - radius); cz <= Math.floor(h.z + radius); cz++) {
      for (const blade of cells.get(cellKey(cx, cz)) || []) {
        if (!blade.cut && hits(blade)) {
          blade.cut = true;
          cut++;
        }
      }
    }
  }
  if (cut) state.grassVersion = (state.grassVersion || 0) + 1;
  return cut;
}
