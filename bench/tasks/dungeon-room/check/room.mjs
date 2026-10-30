// The room's parts played through an api { state(), step(input, dt) }: the same function runs in Node and, as its
// source, in the page (so it is self-contained). Returns what each part did.
export function playRoom(api, parts) {
  const DT = 1 / 60;
  const s = () => api.state();
  const find = kind => s().entities.find(e => e.kind === kind && !e.removed);
  const run = (input, seconds) => { for (let t = 0; t < seconds; t += DT) api.step(input, DT); };
  const out = {};

  // Walks through the gate along its thin side, from the side the hero is on; true when the hero got 0.8 m past it.
  const cross = () => {
    const gate = find('gate');
    if (!gate) return true;
    const h = s().hero, alongZ = (gate.d ?? 0) <= (gate.w ?? 0);
    const side = Math.sign(alongZ ? h.pos.z - gate.pos.z : h.pos.x - gate.pos.x) || 1;
    Object.assign(h.pos, alongZ ? { x: gate.pos.x, z: gate.pos.z + side * 1.2 } : { x: gate.pos.x + side * 1.2, z: gate.pos.z });
    run(alongZ ? { moveZ: -side } : { moveX: -side }, 2);
    return (alongZ ? h.pos.z - gate.pos.z : h.pos.x - gate.pos.x) * -side > 0.8;
  };

  // Uses a thing from each of its four sides in turn until `done` says it worked.
  const useFromSides = (thing, done) => {
    const h = s().hero;
    for (const [x, z] of [[0, 1], [0, -1], [1, 0], [-1, 0]]) {
      const reach = (x ? (thing.w ?? 1) : (thing.d ?? 1)) / 2 + h.r + 0.2;
      Object.assign(h.pos, { x: thing.pos.x + x * reach, z: thing.pos.z + z * reach });
      h.facing = Math.atan2(-x, -z);
      api.step({}, DT);
      h.facing = Math.atan2(thing.pos.x - h.pos.x, thing.pos.z - h.pos.z);
      api.step({ use: true }, DT);
      run({}, 0.3);
      if (done()) return true;
    }
    return false;
  };

  for (const part of parts) {
    if (part === 'kinds') out.kinds = ['block', 'switch', 'gate', 'chest', 'door'].filter(kind => find(kind));
    if (part === 'cross') out.crossed = cross();
    if (part === 'push') {
      const block = find('block'), sw = find('switch'), h = s().hero;
      const alongX = Math.abs(sw.pos.x - block.pos.x) >= Math.abs(sw.pos.z - block.pos.z);
      const dir = Math.sign(alongX ? sw.pos.x - block.pos.x : sw.pos.z - block.pos.z);
      const back = (alongX ? (block.w ?? 1) : (block.d ?? 1)) / 2 + h.r + 0.15;
      Object.assign(h.pos, alongX ? { x: block.pos.x - dir * back, z: block.pos.z } : { x: block.pos.x, z: block.pos.z - dir * back });
      for (let t = 0; t < 15; t += DT) {
        const b = find('block');
        if ((alongX ? sw.pos.x - b.pos.x : sw.pos.z - b.pos.z) * dir <= 0.05) break;
        api.step(alongX ? { moveX: dir } : { moveZ: dir }, DT);
      }
      run({}, 0.3);
      const b = find('block');
      out.onSwitch = Math.hypot(b.pos.x - sw.pos.x, b.pos.z - sw.pos.z) < 0.5;
      out.gateOpen = !find('gate') || find('gate').open === true;
    }
    if (part === 'chest') {
      const keys = s().hero.keys;
      out.chestKey = useFromSides(find('chest'), () => s().hero.keys > keys);
      const after = s().hero.keys;
      api.step({ use: true }, DT);
      run({}, 0.3);
      out.chestOnce = s().hero.keys === after;
    }
    if (part === 'door') {
      const door = find('door');
      out.doorOpen = useFromSides(door, () => door.open === true || !find('door'));
      out.keys = s().hero.keys;
    }
  }
  return out;
}

// What a saved room must bring back: each thing's kind, place and state.
export function roomSummary(state) {
  return {
    keys: state.hero.keys,
    things: state.entities.filter(e => ['block', 'switch', 'gate', 'chest', 'door'].includes(e.kind) && !e.removed)
      .map(e => `${e.kind} ${e.pos.x.toFixed(1)},${e.pos.z.toFixed(1)} ${e.open ? 'open' : ''}${e.opened ? 'opened' : ''}`).sort(),
  };
}
