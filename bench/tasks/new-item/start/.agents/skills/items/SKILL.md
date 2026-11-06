---
name: items
description: Add or change an item (a pickup such as a gem, a heart or a key): its data, look, effect and drop weight.
---

# Items

Items are data. `data/items.json` lists each one: `id`, `name`, `shape`, `color`, `effect` and `drop` (its weight
among the drops of broken pots and crates; none: never dropped). `src/items.gen.js` is generated from it: never edit
it by hand, a hand edit is lost on the next run.

1. Add or change the item in `data/items.json`.
2. Run `node tools/gen-items.mjs`: it rewrites `src/items.gen.js`.
3. An effect adds to the hero's fields (`{ "gems": 1 }`); anything more than adding goes in `src/pickups.js`.
4. A new `shape` needs its look in `makeMesh` (`src/draw/shapes.js`).
5. `node --test`.
