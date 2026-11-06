// A screenshot of the game through a headless Edge (or Chrome):
//   node tools/shot.mjs [out.png] [--query "level=island&seed=2"] [--wait 1000] [--eval "<js run in the page first>"]
// It serves the game itself, waits for window.island.ready, prints page errors, and always closes the browser.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { startServer } from './serve.mjs';

const args = process.argv.slice(2);
const option = (name, fallback) => (args.includes(name) ? args[args.indexOf(name) + 1] : fallback);
const out = args[0] && !args[0].startsWith('--') ? args[0] : 'shot.png';
const browsers = ['C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'];
const exe = browsers.find(file => fs.existsSync(file));
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

const server = await startServer(0);
const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'island-shot-'));
spawn(exe, ['--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`, '--window-size=960,540',
  '--no-first-run', '--enable-unsafe-swiftshader', 'about:blank'], { stdio: 'ignore' });
let browserWs;
try {
  // The browser writes its port into the profile (msedge.exe itself may exit as a launcher).
  const portFile = path.join(profile, 'DevToolsActivePort');
  for (let i = 0; i < 200 && !fs.existsSync(portFile); i++) await sleep(100);
  const [port, browserPath] = fs.readFileSync(portFile, 'utf8').split(/\r?\n/);
  browserWs = `ws://127.0.0.1:${port}${browserPath}`;
  const target = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(t => t.type === 'page');
  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise(resolve => (ws.onopen = resolve));
  let id = 0;
  const waiting = new Map();
  ws.onmessage = event => {
    const message = JSON.parse(event.data);
    if (waiting.has(message.id)) waiting.get(message.id)(message.result);
    if (message.method === 'Runtime.exceptionThrown') console.error('page error:', message.params.exceptionDetails.exception?.description);
  };
  const send = (method, params = {}) => new Promise(resolve => {
    waiting.set(++id, resolve);
    ws.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async expression => (await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })).result?.value;
  await send('Runtime.enable');
  await send('Page.navigate', { url: `http://127.0.0.1:${server.address().port}/?${option('--query', '')}` });
  for (let i = 0; i < 100 && !(await evaluate('!!window.island?.ready')); i++) await sleep(100);
  if (option('--eval')) await evaluate(option('--eval'));
  await sleep(Number(option('--wait', 500)));
  const { data } = await send('Page.captureScreenshot', { format: 'png' });
  fs.writeFileSync(out, Buffer.from(data, 'base64'));
  console.log(`saved ${path.resolve(out)}`);
  ws.close();
} finally {
  if (browserWs) {
    const ws = new WebSocket(browserWs);
    await new Promise(resolve => {
      ws.onopen = () => ws.send(JSON.stringify({ id: 1, method: 'Browser.close' }));
      ws.onmessage = ws.onerror = resolve;
      setTimeout(resolve, 3000);
    });
  }
  server.close();
  for (let i = 0; i < 20; i++) {
    try { fs.rmSync(profile, { recursive: true, force: true }); break; } catch { await sleep(250); }
  }
}
