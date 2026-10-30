// Link Bench (docs/BENCH_PLAN.md): how well a harness lets a model build a game.
//   node bench/bench.mjs new <task>                      a fresh run folder, the prompt printed and on the clipboard
//   node bench/bench.mjs score <folder>                  hidden checks + the run's sessions -> a row of results.csv
//   node bench/bench.mjs report                          the results by task, harness and model
//   node bench/bench.mjs run <task> --harness claude-cli|codex-cli [--model <m>]
//   node bench/bench.mjs run <task> --harness link --folder <run folder> --project <Link's project> [--agent Codex]
//   node bench/bench.mjs selftest [task]                 each task's start scores low, start + solution scores 100
import { spawnSync, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { findSessions, readSession, summarize, linkSessionIds } from './lib/sessions.mjs';

const BENCH = path.dirname(fileURLToPath(import.meta.url));
const HOME = path.join(os.homedir(), 'LinkBench');
const RUNS = path.join(HOME, 'runs');
const RESULTS = path.join(HOME, 'results.csv');
const SHEETS = path.join(HOME, 'sheets');
const LINK_SKILL = path.resolve(BENCH, '..', '.agents', 'skills', 'drive-link');
const LINK_LOG = path.join(process.env.LOCALAPPDATA || '', 'Link', 'link.log');

const COLUMNS = ['time', 'task', 'run', 'harness', 'model', 'effort', 'score', 'passed', 'total', 'wallSec', 'firstEditSec',
  'userWaitSec', 'turns', 'toolCalls', 'searchesBeforeEdit', 'toolErrors', 'extraUserMessages', 'questions', 'helpers',
  'tokensIn', 'tokensCached', 'tokensOut', 'costUsd', 'ranTests', 'servedPage', 'tookScreenshot', 'filesChanged',
  'linesAdded', 'linesRemoved', 'sessions', 'commit', 'mark', 'sheet', 'notes'];

function loadTask(id) {
  const dir = path.join(BENCH, 'tasks', id);
  if (!fs.existsSync(path.join(dir, 'task.json'))) throw new Error(`no task '${id}': ${taskIds().join(', ')}`);
  const read = name => (fs.existsSync(path.join(dir, name)) ? fs.readFileSync(path.join(dir, name), 'utf8').trim() : null);
  return { ...JSON.parse(read('task.json')), dir, prompt: read('prompt.md'), followup: read('followup.md') };
}

const taskIds = () => fs.readdirSync(path.join(BENCH, 'tasks')).filter(id => fs.existsSync(path.join(BENCH, 'tasks', id, 'task.json')));

const git = (cwd, ...args) => execFileSync('git', ['-c', 'core.autocrlf=false', ...args], { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();

// The task's starting state of the game in `folder`, as its own git repository with one commit.
function makeStart(task, folder) {
  fs.cpSync(path.join(BENCH, 'game'), folder, { recursive: true });
  overlay(path.join(task.dir, 'start'), folder, task.remove);
  git(folder, 'init', '-q');
  git(folder, 'add', '-A');
  git(folder, '-c', 'user.name=bench', '-c', 'user.email=bench@localhost', 'commit', '-q', '-m', 'Island');
}

function overlay(from, folder, remove = []) {
  if (fs.existsSync(from)) fs.cpSync(from, folder, { recursive: true });
  for (const file of remove) fs.rmSync(path.join(folder, file), { force: true });
}

function commit() {
  try { return git(BENCH, 'rev-parse', '--short', 'HEAD'); } catch { return ''; }
}

function newRun(id) {
  const task = loadTask(id);
  fs.mkdirSync(RUNS, { recursive: true });
  const taken = fs.readdirSync(RUNS).map(name => new RegExp(`^${id}-(\\d+)$`).exec(name)?.[1]).filter(Boolean).map(Number);
  const name = `${id}-${Math.max(0, ...taken) + 1}`;
  const folder = path.join(RUNS, name);
  makeStart(task, folder);
  // What the bench knows of a run stays beside its folder, out of the agent's sight.
  fs.writeFileSync(path.join(RUNS, `${name}.json`), JSON.stringify({ task: id, created: Date.now(), commit: commit() }, null, 2));
  return { task, folder };
}

function toClipboard(text) {
  // clip.exe reads UTF-16 as Unicode (UTF-8 would go through the console's code page); a byte order mark would be
  // pasted as a character, so there is none.
  return spawnSync('clip.exe', { input: Buffer.from(text, 'utf16le') }).status === 0;
}

function cmdNew(id) {
  const { task, folder } = newRun(id);
  console.log(`${folder}\n\nOpen that folder in the harness and paste the prompt${toClipboard(task.prompt) ? ' (on the clipboard)' : ''}:\n`);
  console.log(task.prompt);
  if (task.followup) console.log(`\nAfter the first answer, paste the follow-up:\n\n${task.followup}`);
  console.log(`\nThen: node bench/bench.mjs score "${folder}"`);
}

function runMeta(folder) {
  const file = `${path.resolve(folder)}.json`;
  if (fs.existsSync(file)) return JSON.parse(fs.readFileSync(file, 'utf8'));
  const id = /^(.*)-\d+$/.exec(path.basename(folder))?.[1];
  return { task: id, created: fs.statSync(folder).birthtimeMs, commit: '' };
}

function rmTree(dir) {
  for (let i = 0; i < 20; i++) {
    try { fs.rmSync(dir, { recursive: true, force: true }); return; } catch { spawnSync(process.execPath, ['-e', 'setTimeout(()=>{},250)']); }
  }
}

// Scores a run folder: the hidden check in a temporary copy (never in the run folder), the sessions from the logs.
async function score(folder, { task: id, sessionFiles, record = true, sheets = true, quiet = false } = {}) {
  folder = path.resolve(folder);
  const meta = runMeta(folder);
  const task = loadTask(id || meta.task);
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'bench-score-'));
  try {
    const copy = path.join(tmp, 'run');
    fs.cpSync(folder, copy, { recursive: true });
    const diff = changes(copy);

    const files = sessionFiles ?? findSessions(folder, { since: (meta.created || 0) - 60_000 });
    const sessions = files.map(readSession);
    const stats = summarize(sessions, { followups: task.followup ? 1 : 0, linkIds: linkSessionIds() });

    const bench = path.join(copy, '.bench');
    fs.cpSync(path.join(task.dir, 'check'), path.join(bench, 'check'), { recursive: true });
    fs.cpSync(path.join(BENCH, 'lib'), path.join(bench, 'lib'), { recursive: true });
    fs.writeFileSync(path.join(bench, 'session.json'), JSON.stringify({ sessions }));
    const result = runCheck(copy, bench);

    let sheet = '';
    const sheetFile = path.join(task.dir, 'sheet.mjs');
    if (sheets && fs.existsSync(sheetFile) && result.score > 0) {
      const out = path.join(typeof sheets === 'string' ? sheets : SHEETS, path.basename(folder));
      fs.mkdirSync(out, { recursive: true });
      try {
        const { default: makeSheet } = await import(pathToFileURL(sheetFile).href);
        sheet = await makeSheet({ game: copy, out });
      } catch (error) {
        result.notes.push(`sheet: ${error.message.split('\n')[0]}`);
      }
    }

    const row = {
      time: new Date().toISOString(), task: task.id, run: path.basename(folder), ...stats, ...result, ...diff,
      commit: meta.commit || commit(), mark: '', sheet, notes: result.notes.join(' | '),
    };
    if (record) appendRow(row);
    if (!quiet) printScore(row, result);
    return row;
  } finally {
    rmTree(tmp);
  }
}

// Files and lines changed since the run's first commit, untracked files included.
function changes(copy) {
  if (!fs.existsSync(path.join(copy, '.git'))) return { filesChanged: '', linesAdded: '', linesRemoved: '' };
  git(copy, 'add', '-A');
  const first = git(copy, 'rev-list', '--max-parents=0', 'HEAD').split('\n')[0];
  const rows = git(copy, 'diff', '--cached', '--numstat', first).split('\n').filter(Boolean).map(line => line.split('\t'));
  const sum = column => rows.reduce((n, row) => n + (Number(row[column]) || 0), 0);
  return { filesChanged: rows.length, linesAdded: sum(0), linesRemoved: sum(1) };
}

function runCheck(copy, bench) {
  const tests = fs.readdirSync(path.join(bench, 'check')).filter(name => name.endsWith('.test.mjs')).map(name => path.join(bench, 'check', name));
  const reporter = pathToFileURL(path.join(bench, 'lib', 'reporter.mjs')).href;
  const run = spawnSync(process.execPath, ['--test', '--test-concurrency=1', `--test-reporter=${reporter}`, ...tests], {
    cwd: copy, encoding: 'utf8', timeout: 600_000, maxBuffer: 64 << 20,
    env: { ...process.env, BENCH_SESSION: path.join(bench, 'session.json') },
  });
  const line = (run.stdout || '').trim().split('\n').reverse().find(l => l.startsWith('{"score"'));
  if (!line) return { score: 0, passed: 0, total: 0, notes: [`the check did not finish: ${(run.stderr || run.error?.message || '').slice(0, 300)}`] };
  return JSON.parse(line);
}

function printScore(row, result) {
  console.log(`${row.run}: score ${row.score} (${row.passed}/${row.total})`);
  for (const note of result.notes) console.log(`  - ${note}`);
  if (row.sessions) {
    console.log(`  ${row.harness} ${row.model}: ${row.wallSec}s, first edit ${row.firstEditSec}s, turns ${row.turns}, tools ${row.toolCalls}, ` +
      `searches before edit ${row.searchesBeforeEdit}, errors ${row.toolErrors}, extra messages ${row.extraUserMessages}, ` +
      `tokens in ${row.tokensIn} (cached ${row.tokensCached}) out ${row.tokensOut}${row.costUsd != null ? `, $${row.costUsd}` : ''}`);
  } else {
    console.log('  no session found for this folder');
  }
  if (row.sheet) console.log(`  sheet: ${row.sheet} (put your 1-5 mark in the 'mark' column of ${RESULTS})`);
}

const csvCell = value => {
  const text = value == null ? '' : String(value);
  return /[",\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text;
};

function appendRow(row) {
  fs.mkdirSync(HOME, { recursive: true });
  if (!fs.existsSync(RESULTS)) fs.writeFileSync(RESULTS, `${COLUMNS.join(',')}\n`);
  fs.appendFileSync(RESULTS, `${COLUMNS.map(c => csvCell(row[c])).join(',')}\n`);
}

function readCsv(file) {
  const rows = [];
  let row = [], cell = '', quoted = false;
  const text = fs.readFileSync(file, 'utf8');
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') { cell += '"'; i++; } else if (c === '"') quoted = false; else cell += c;
    } else if (c === '"') quoted = true;
    else if (c === ',') { row.push(cell); cell = ''; }
    else if (c === '\n') { row.push(cell.replace(/\r$/, '')); rows.push(row); row = []; cell = ''; }
    else cell += c;
  }
  const [header, ...body] = rows;
  return body.map(r => Object.fromEntries(header.map((h, i) => [h, r[i] ?? ''])));
}

function cmdReport() {
  if (!fs.existsSync(RESULTS)) return console.log(`no results yet (${RESULTS})`);
  const groups = new Map();
  for (const r of readCsv(RESULTS)) {
    const key = [r.task, r.harness || '-', r.model || '-'].join('\t');
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(r);
  }
  const mean = (rows, column, digits = 0) => {
    const values = rows.map(r => r[column]).filter(v => v !== '').map(Number).filter(Number.isFinite);
    return values.length ? (values.reduce((a, b) => a + b, 0) / values.length).toFixed(digits) : '-';
  };
  const table = [['task', 'harness', 'model', 'runs', 'score', 'mark', 'wall s', 'first edit s', 'turns', 'extra msgs', 'tokens in', 'out', '$']];
  for (const [key, rows] of [...groups].sort()) {
    table.push([...key.split('\t'), rows.length, mean(rows, 'score'), mean(rows, 'mark', 1), mean(rows, 'wallSec'), mean(rows, 'firstEditSec'),
      mean(rows, 'turns'), mean(rows, 'extraUserMessages', 1), mean(rows, 'tokensIn'), mean(rows, 'tokensOut'), mean(rows, 'costUsd', 2)].map(String));
  }
  const widths = table[0].map((_, i) => Math.max(...table.map(r => r[i].length)));
  for (const r of table) console.log(r.map((cell, i) => cell.padEnd(widths[i])).join('  '));
}

const quote = arg => (/[\s"&|<>^()]/.test(arg) ? `"${arg.replace(/"/g, '\\"')}"` : arg);

// One agent turn of a CLI, the text on stdin; its JSON events come back for the session id.
function agentTurn(command, args, cwd, text, minutes) {
  console.log(`> ${command} ${args.join(' ')}`);
  // Through the shell: an npm-installed CLI is a .cmd, which only the shell runs.
  const run = spawnSync([command, ...args].map(quote).join(' '), {
    cwd, input: text, encoding: 'utf8', shell: true, timeout: minutes * 60_000, maxBuffer: 512 << 20,
  });
  if (run.error || run.status !== 0) console.log(`  ${command} ended: ${run.error?.message || `exit ${run.status}`} ${(run.stderr || '').slice(0, 300)}`);
  return run.stdout || '';
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

// Waits for the `[Agent] ...` line Link writes when a run ends, after `since`.
async function linkRunEnd(since, minutes) {
  const end = Date.now() + minutes * 60_000;
  while (Date.now() < end) {
    const text = fs.existsSync(LINK_LOG) ? fs.readFileSync(LINK_LOG, 'utf8') : '';
    for (const match of text.matchAll(/^\[([^\]]+)\] \[Agent\] [^\n]*$/gm)) {
      if (Date.parse(match[1]) > since) return match[0];
    }
    await sleep(10_000);
  }
  throw new Error('Link did not finish the run in time');
}

function powershell(script, ...args) {
  return spawnSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(LINK_SKILL, script), ...args], { stdio: 'inherit' }).status;
}

async function cmdRun(id, options) {
  const harness = options.harness;
  if (!['claude-cli', 'codex-cli', 'link'].includes(harness)) throw new Error('--harness claude-cli|codex-cli|link');
  let task, folder;
  if (options.folder) {
    folder = path.resolve(options.folder);
    task = loadTask(runMeta(folder).task);
  } else if (harness === 'link') {
    throw new Error('Link works in its own project: `new` the run, add its folder as a project in Link, then run with --folder and --project');
  } else {
    ({ task, folder } = newRun(id));
  }
  const minutes = task.timeLimitMin || 30;
  const turns = [task.prompt, task.followup].filter(Boolean);
  console.log(`${task.id} in ${folder} by ${harness}${options.model ? ` (${options.model})` : ''}`);

  if (harness === 'claude-cli') {
    let session = null;
    for (const text of turns) {
      const args = ['-p', '--output-format', 'stream-json', '--verbose', '--permission-mode', 'bypassPermissions'];
      if (options.model) args.push('--model', options.model);
      if (session) args.push('--resume', session);
      session = /"session_id":"([^"]+)"/.exec(agentTurn('claude', args, folder, text, minutes))?.[1] ?? session;
    }
  } else if (harness === 'codex-cli') {
    let thread = null;
    for (const text of turns) {
      // resume takes no --sandbox or --cd: the sandbox goes through config, the folder is the working one.
      const args = thread
        ? ['exec', 'resume', thread, '--json', '--skip-git-repo-check', '-c', 'sandbox_mode=workspace-write']
        : ['exec', '--json', '--skip-git-repo-check', '--sandbox', 'workspace-write', '--cd', folder];
      if (options.model) args.push('-c', `model=${options.model}`);
      args.push('-');
      thread = /"thread_id":"([^"]+)"/.exec(agentTurn('codex', args, folder, text, minutes))?.[1] ?? thread;
    }
  } else {
    if (!options.project) throw new Error('--project: the name of the Link project that shows the run folder');
    if (options.model) console.log('Link runs the model its card picks: --model is not passed');
    for (const [i, text] of turns.entries()) {
      const file = path.join(os.tmpdir(), `bench-${task.id}-${i}.txt`);
      fs.writeFileSync(file, text);
      const since = Date.now();
      const status = i === 0
        ? powershell('newtask.ps1', '-Project', options.project, '-Goal', file, ...(options.agent ? ['-Agent', options.agent] : []))
        : powershell('reply.ps1', '-Project', options.project, '-Words', file);
      if (status !== 0) throw new Error('driving Link failed');
      console.log(await linkRunEnd(since, minutes));
    }
  }
  await score(folder);
}

// Each task's start scores below 50 and start + solution scores 100, with no agent; the project's own tests pass in both.
async function cmdSelftest(only) {
  const ids = only ? [only] : taskIds();
  const lines = [];
  let ok = true;
  for (const id of ids) {
    const task = loadTask(id);
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'bench-self-'));
    try {
      const folder = path.join(tmp, `${id}-0`);
      makeStart(task, folder);
      const projectStart = projectTests(folder);
      const start = await score(folder, { task: id, sessionFiles: [], record: false, sheets: false, quiet: true });
      overlay(path.join(task.dir, 'solution'), folder, task.solutionRemove);
      const projectSolved = projectTests(folder);
      const oracle = task.oracleSession ? [{ agent: 'claude', file: path.join(task.dir, task.oracleSession) }] : [];
      const solved = await score(folder, { task: id, sessionFiles: oracle, record: false, sheets: path.join(tmp, 'sheets'), quiet: true });
      const pass = start.score < 50 && solved.score === 100 && projectStart && projectSolved;
      ok &&= pass;
      const line = `${pass ? 'ok  ' : 'FAIL'} ${id.padEnd(13)} start ${String(start.score).padStart(3)} (${start.passed}/${start.total})  ` +
        `solution ${String(solved.score).padStart(3)} (${solved.passed}/${solved.total})  project tests ${projectStart ? 'pass' : 'FAIL'}/${projectSolved ? 'pass' : 'FAIL'}` +
        `${solved.sheet ? '  sheet ok' : ''}`;
      console.log(line);
      lines.push(line);
      if (!pass) for (const note of [...solved.notes.split(' | ')].filter(Boolean)) console.log(`     solution: ${note}`);
    } finally {
      rmTree(tmp);
    }
  }
  console.log(ok ? 'selftest: all tasks pass' : 'selftest: FAILED');
  process.exitCode = ok ? 0 : 1;
}

function projectTests(folder) {
  return spawnSync(process.execPath, ['--test'], { cwd: folder, encoding: 'utf8', timeout: 120_000 }).status === 0;
}

function options(args) {
  const out = {};
  for (let i = 0; i < args.length; i++) if (args[i].startsWith('--')) out[args[i].slice(2)] = args[++i];
  return out;
}

const [command, arg, ...rest] = process.argv.slice(2);
try {
  if (command === 'new' && arg) cmdNew(arg);
  else if (command === 'score' && arg) await score(arg);
  else if (command === 'report') cmdReport();
  else if (command === 'run' && arg) await cmdRun(arg, options(rest));
  else if (command === 'selftest') await cmdSelftest(arg);
  else console.log(fs.readFileSync(fileURLToPath(import.meta.url), 'utf8').split('\n').slice(0, 8).join('\n'));
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
