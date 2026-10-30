// What the hidden checks share. A check runs in a temporary copy of the run as <copy>/.bench/check/*.test.mjs, with this
// folder copied to <copy>/.bench/lib, so the game under test is two folders up.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { launch } from './driver.mjs';
import { serve } from './serve.mjs';

export { sleep, percentile } from './driver.mjs';
export { firstAsk } from './sessions.mjs';

export const GAME = path.resolve(fileURLToPath(new URL('../..', import.meta.url)));

// The game's logic module of the run (its documented API: createGame, step, spawn).
export const game = () => import(pathToFileURL(path.join(GAME, 'src', 'game.js')).href);

// Steps a state for some seconds with the same input; `each` sees the state after every step.
export function simulate(g, state, input, seconds, { dt = 1 / 60, each } = {}) {
  for (let t = 0; t < seconds - 1e-9; t += dt) {
    g.step(state, input, dt);
    each?.(state);
  }
  return state;
}

export const alive = (state, id) => state.entities.some(e => e.id === id && !e.removed);

// Opens the run's page (with a query such as 'level=room') and hands the page to fn; the browser and the server
// always close. Page errors fail the check.
export async function withPage(query, fn) {
  const server = await serve(GAME);
  const page = await launch();
  try {
    await page.open(`${server.url}index.html${query ? `?${query}` : ''}`, { until: 'window.island && window.island.ready' });
    const result = await fn(page, server.url);
    if (page.errors.length) throw new Error(`page errors: ${page.errors.slice(0, 2).join(' | ')}`);
    return result;
  } finally {
    await page.close();
    server.close();
  }
}

// The sessions of the run, as the scorer read them from the harnesses' logs (empty when none was found).
export function sessions() {
  const file = process.env.BENCH_SESSION;
  return file && fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')).sessions : [];
}
