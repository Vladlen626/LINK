// A frame sheet for the user's 1-5 mark: the run's page, a few scenes set up in it, each shot from a fixed camera, and
// an index.html that shows them side by side.
import fs from 'node:fs';
import path from 'node:path';
import { launch, sleep } from './driver.mjs';
import { serve } from './serve.mjs';

// scenes: [{ name, setup: a self-contained function run in the page (it may use window.island), at: [x, y, z],
// look: [x, y, z] }]. A scene's camera is relative to what its setup returns ({ x, z }), or to the scene before;
// `at: null` shows the game's own camera.
export async function takeSheet({ game, out, query = '', scenes, title }) {
  const server = await serve(game);
  const page = await launch({ width: 640, height: 400 });
  try {
    await page.open(`${server.url}index.html${query ? `?${query}` : ''}`, { until: 'window.island && window.island.ready' });
    let at = { x: 0, z: 0 };
    for (const scene of scenes) {
      if (scene.setup) at = (await page.call(scene.setup)) || at;
      if (scene.at) {
        const pos = { x: at.x + scene.at[0], y: scene.at[1], z: at.z + scene.at[2] };
        const look = { x: at.x + scene.look[0], y: scene.look[1], z: at.z + scene.look[2] };
        await page.call((p, l) => window.island.view(p, l), pos, look);
      } else {
        await page.call(() => window.island.view(null));
      }
      await sleep(120);
      await page.screenshot(path.join(out, `${scene.name}.png`));
    }
  } finally {
    await page.close();
    server.close();
  }
  const cells = scenes.map(s => `<figure><img src="${s.name}.png"><figcaption>${s.name}</figcaption></figure>`).join('\n');
  const index = path.join(out, 'index.html');
  fs.writeFileSync(index, `<!doctype html><meta charset="utf-8"><title>${title}</title>
<style>body{font:14px system-ui;margin:16px;background:#222;color:#eee}main{display:flex;flex-wrap:wrap;gap:12px}
figure{margin:0}img{width:480px;display:block}</style><h1>${title}</h1><p>Mark it 1-5 in results.csv.</p><main>${cells}</main>`);
  return index;
}
