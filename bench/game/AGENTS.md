# Island

A small third-person action-adventure in the browser: three.js (vendored in `vendor/`, never edit or read it),
plain ES modules, no build and no packages. Node 24.

## Run, test, look
- Serve: `node tools/serve.mjs` -> http://127.0.0.1:8080/ (`?seed=2`, `?level=empty` pick the game).
- Test: `node --test` (tests in `test/`, they drive the logic in Node).
- Screenshot: `node tools/shot.mjs out.png [--query "seed=2"] [--wait 1000] [--eval "<js run in the page first>"]`
  (headless Edge; it prints page errors).
- Items: `data/items.json` -> `node tools/gen-items.mjs` -> `src/items.gen.js`.
- Project skills: `.agents/skills/<name>/SKILL.md`, read the one that fits before the work.

## How it is built
- Logic in `src/` apart from drawing in `src/draw/` and `src/hud.js`: everything that moves or changes lives in the
  state; the drawing only reads it.
- `src/game.js`: `createGame({ seed, level })` returns a plain-data state (it saves as JSON); `step(state, input, dt)`
  advances it by dt seconds (the page passes its frame time, at most 0.1 s; tests pass what they like);
  `spawn(state, kind, x, z, props)` adds an entity. All randomness comes from the state's seed (`random(state)`).
- Ground is x-z, y up; -z is north, away from the camera. A facing angle f looks along (sin f, cos f).
- `input`: `moveX`, `moveZ` (-1..1, world axes), `run` (held); `attack`, `roll`, `use` are true on the step their key
  went down. Keys: WASD/arrows, Shift run, Space or J sword, K roll, E use.
- State: `hero` (`pos`, `facing`, `hp`, `maxHp`, `gems`, `keys`, ...), `entities` (`{ id, kind, pos: { x, y, z } }`
  plus their own fields; a solid one blocks as a box `w` x `d`), `walls` (boxes `{ x, z, w, d, h }`), `grass`
  (blades `{ x, z, cut }`), `camera` (`pos`, `look`, `fov`).
- The page exposes `window.island` for tests and tools: `state` (the live state), `step(input, dt)` (steps it now and
  draws), `view(pos, look)` (pins the camera; `view(null)` follows again), `frameTimes` (ms between recent frames),
  `ready`.
