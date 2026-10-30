// A page driver over the Chrome DevTools Protocol, with Node's own WebSocket and child_process: a headless Edge (or
// Chrome) with a throwaway profile, a page loaded, JS evaluated, keys played, screenshots and frame times taken.
// close() always kills the browser and deletes the profile.
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const BROWSERS = [
  'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
  'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
];

// What a key press needs in CDP: the key, its code and the Windows virtual key.
const KEYS = {
  ' ': ['Space', 32], Shift: ['ShiftLeft', 16], Tab: ['Tab', 9],
  ArrowLeft: ['ArrowLeft', 37], ArrowUp: ['ArrowUp', 38], ArrowRight: ['ArrowRight', 39], ArrowDown: ['ArrowDown', 40],
};

function keyInfo(key) {
  if (KEYS[key]) return { key, code: KEYS[key][0], vk: KEYS[key][1] };
  const upper = key.toUpperCase();
  return { key, code: /[A-Z]/.test(upper) ? `Key${upper}` : `Digit${key}`, vk: upper.charCodeAt(0) };
}

// Edge first; a machine's policy may stop a headless Edge at once (seen on this one), then Chrome.
export async function launch(options = {}) {
  let failure = new Error('no Edge or Chrome found');
  for (const exe of BROWSERS.filter(file => fs.existsSync(file))) {
    try { return await launchOne(exe, options); } catch (error) { failure = error; }
  }
  throw failure;
}

async function launchOne(exe, { width = 960, height = 540 }) {
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'bench-browser-'));
  // msedge.exe is a launcher that may exit while the browser goes on: the browser is found by the port file it writes
  // into the profile, and closed through the protocol, not by the launcher's process.
  spawn(exe, [
    '--headless=new', '--remote-debugging-port=0', `--user-data-dir=${profile}`, `--window-size=${width},${height}`,
    '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--mute-audio',
    // Software WebGL when the session has no GPU.
    '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', 'about:blank',
  ], { stdio: 'ignore' }).on('error', () => {});
  const browser = new Browser(profile);
  try {
    const portFile = path.join(profile, 'DevToolsActivePort');
    for (let i = 0; i < 200 && !fs.existsSync(portFile); i++) await sleep(100);
    const [port, browserPath] = fs.readFileSync(portFile, 'utf8').split(/\r?\n/);
    browser.browserUrl = `ws://127.0.0.1:${port}${browserPath}`;
    let page;
    for (let i = 0; i < 50 && !page; i++) {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      page = targets.find(t => t.type === 'page');
      if (!page) await sleep(100);
    }
    await browser.connect(page.webSocketDebuggerUrl);
    return browser;
  } catch (error) {
    await browser.close();
    throw new Error(`${path.basename(exe)} did not start: ${error.message}`);
  }
}

class Browser {
  constructor(profile) {
    this.profile = profile;
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = [];
    // Uncaught page errors and console errors, for checks that want a page without them.
    this.errors = [];
  }

  async connect(url) {
    this.ws = new WebSocket(url);
    await new Promise((resolve, reject) => { this.ws.onopen = resolve; this.ws.onerror = reject; });
    this.ws.onmessage = event => {
      const message = JSON.parse(event.data);
      if (message.id && this.pending.has(message.id)) {
        const { resolve, reject } = this.pending.get(message.id);
        this.pending.delete(message.id);
        if (message.error) reject(new Error(message.error.message)); else resolve(message.result);
      } else if (message.method) {
        if (message.method === 'Runtime.exceptionThrown') {
          const details = message.params.exceptionDetails;
          this.errors.push(details.exception?.description || details.text);
        }
        if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error') {
          this.errors.push(message.params.args.map(arg => arg.value ?? arg.description).join(' '));
        }
        for (const listener of this.listeners) listener(message);
      }
    };
    await this.send('Page.enable');
    await this.send('Runtime.enable');
  }

  send(method, params = {}) {
    const id = this.nextId++;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve, reject) => this.pending.set(id, { resolve, reject }));
  }

  waitFor(method, ms = 20000) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(`timeout waiting for ${method}`)), ms);
      const listener = message => {
        if (message.method !== method) return;
        clearTimeout(timer);
        this.listeners = this.listeners.filter(l => l !== listener);
        resolve(message.params);
      };
      this.listeners.push(listener);
    });
  }

  // Loads a page and waits for its load event and, when given, for an expression to be true.
  async open(url, { until, ms = 20000 } = {}) {
    const loaded = this.waitFor('Page.loadEventFired', ms);
    await this.send('Page.navigate', { url });
    await loaded;
    if (until) await this.waitUntil(until, ms);
  }

  async reload({ until, ms = 20000 } = {}) {
    const loaded = this.waitFor('Page.loadEventFired', ms);
    await this.send('Page.reload');
    await loaded;
    if (until) await this.waitUntil(until, ms);
  }

  async waitUntil(expression, ms = 20000) {
    const end = Date.now() + ms;
    while (Date.now() < end) {
      if (await this.eval(`!!(${expression})`).catch(() => false)) return;
      await sleep(100);
    }
    throw new Error(`the page never had ${expression}`);
  }

  // Evaluates an expression (promises awaited) and returns its value as JSON.
  async eval(expression) {
    const result = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    if (result.exceptionDetails) {
      throw new Error(result.exceptionDetails.exception?.description || result.exceptionDetails.text);
    }
    return result.result.value;
  }

  // Calls a self-contained function in the page with JSON arguments.
  call(fn, ...args) {
    return this.eval(`(${fn})(...${JSON.stringify(args)})`);
  }

  async keyDown(key) {
    const { code, vk } = keyInfo(key);
    await this.send('Input.dispatchKeyEvent', {
      type: 'keyDown', key, code, windowsVirtualKeyCode: vk, text: key.length === 1 ? key : undefined,
    });
  }

  async keyUp(key) {
    const { code, vk } = keyInfo(key);
    await this.send('Input.dispatchKeyEvent', { type: 'keyUp', key, code, windowsVirtualKeyCode: vk });
  }

  async press(key, ms = 80) {
    await this.keyDown(key);
    await sleep(ms);
    await this.keyUp(key);
  }

  async screenshot(file) {
    const { data } = await this.send('Page.captureScreenshot', { format: 'png' });
    const png = Buffer.from(data, 'base64');
    if (file) fs.writeFileSync(file, png);
    return png;
  }

  // The times between the page's next frames, in ms, measured by requestAnimationFrame itself.
  frameTimes(frames = 120) {
    return this.call(count => new Promise(resolve => {
      const times = [];
      let last = performance.now();
      const frame = now => {
        times.push(now - last);
        last = now;
        if (times.length < count) requestAnimationFrame(frame); else resolve(times.slice(1));
      };
      requestAnimationFrame(frame);
    }), frames + 1);
  }

  // Closes the browser through the protocol, kills whatever of it still holds the profile, deletes the profile.
  async close() {
    try { this.ws?.close(); } catch { /* already closed */ }
    if (this.browserUrl) {
      await new Promise(resolve => {
        const ws = new WebSocket(this.browserUrl);
        const done = () => { try { ws.close(); } catch { /* closed */ } resolve(); };
        setTimeout(done, 3000);
        ws.onopen = () => ws.send(JSON.stringify({ id: 1, method: 'Browser.close' }));
        ws.onmessage = done;
        ws.onerror = done;
      });
    }
    for (let i = 0; i < 40; i++) {
      try { fs.rmSync(this.profile, { recursive: true, force: true }); return; } catch { await sleep(250); }
      if (i === 20) killByProfile(this.profile);
    }
  }
}

// The last resort: the processes whose command line names this run's own profile.
function killByProfile(profile) {
  const script = `Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*${profile}*' } | ` +
    'ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }';
  try { execFileSync('powershell', ['-NoProfile', '-Command', script], { stdio: 'ignore' }); } catch { /* gone */ }
}

export const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export const percentile = (values, p) => {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))] ?? 0;
};
