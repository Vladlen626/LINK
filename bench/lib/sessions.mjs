// Reads the session logs every harness already writes, for the runs in one folder:
//   Claude Code (the app, the CLI, Link's Claude runs): ~/.claude/projects/<folder with - for every other char>/*.jsonl,
//     and Link's own accounts under %LOCALAPPDATA%\Link\ClaudeAccounts\<account>\projects\;
//   Codex (the CLI, the app, Link's Codex runs): ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl, `cwd` in session_meta;
//   Link: `[Agent] ... session <id>` lines in %LOCALAPPDATA%\Link\link.log mark a session as Link's.
// Each session becomes a list of plain events; summarize() turns the sessions of a run into its numbers.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const HOME = os.homedir();
const LOCAL = process.env.LOCALAPPDATA || path.join(HOME, 'AppData', 'Local');

const SEARCH = /(^|[\s"'`(;|&{])(rg|grep|egrep|findstr|fd|find|tree|ls|dir|Get-ChildItem|gci|Select-String|sls|git grep|git ls-files)(\s|$|")/;
const TESTS = /node(\.exe)?\s+(--test|[^\s]*test)|npm(\.cmd)?\s+(run\s+)?test/;
const SERVES = /serve\.mjs|http\.server|http-server|npx\s+serve|live-server/;
const SHOTS = /shot\.mjs|screenshot|captureScreenshot/i;
const ASKS = /\?|\b(options?|which (one|do you|would you)|would you like|do you want|prefer|let me know)\b/i;

const norm = p => path.resolve(p).replace(/[\\/]+$/, '').toLowerCase();

export function claudeProjectRoots() {
  const roots = [path.join(HOME, '.claude', 'projects')];
  const accounts = path.join(LOCAL, 'Link', 'ClaudeAccounts');
  if (fs.existsSync(accounts)) {
    for (const account of fs.readdirSync(accounts)) roots.push(path.join(accounts, account, 'projects'));
  }
  // An account's folder may be a junction to the user's own: each real folder once.
  return [...new Set(roots.filter(root => fs.existsSync(root)).map(root => fs.realpathSync(root)))];
}

// The session files whose working folder is `folder`, written since `since` (ms), oldest first.
export function findSessions(folder, { since = 0 } = {}) {
  const want = norm(folder);
  const found = [];
  const dirName = path.resolve(folder).replace(/[^A-Za-z0-9]/g, '-');
  for (const root of claudeProjectRoots()) {
    const dir = path.join(root, dirName);
    if (!fs.existsSync(dir)) continue;
    for (const name of fs.readdirSync(dir)) {
      const file = path.join(dir, name);
      if (!name.endsWith('.jsonl') || fs.statSync(file).mtimeMs < since) continue;
      const cwd = /"cwd":"((?:[^"\\]|\\.)*)"/.exec(head(file, 1 << 20))?.[1];
      if (cwd && norm(JSON.parse(`"${cwd}"`)) === want) found.push({ agent: 'claude', file });
    }
  }
  const codexRoot = path.join(HOME, '.codex', 'sessions');
  for (const file of walk(codexRoot)) {
    if (!/rollout-.*\.jsonl$/.test(file) || fs.statSync(file).mtimeMs < since) continue;
    const cwd = /"cwd":"((?:[^"\\]|\\.)*)"/.exec(head(file, 1 << 16))?.[1];
    if (cwd && norm(JSON.parse(`"${cwd}"`)) === want) found.push({ agent: 'codex', file });
  }
  return found.sort((a, b) => fs.statSync(a.file).mtimeMs - fs.statSync(b.file).mtimeMs);
}

function head(file, bytes) {
  const fd = fs.openSync(file, 'r');
  try {
    const buffer = Buffer.alloc(Math.min(bytes, fs.fstatSync(fd).size));
    fs.readSync(fd, buffer, 0, buffer.length, 0);
    return buffer.toString('utf8');
  } finally {
    fs.closeSync(fd);
  }
}

function* walk(dir) {
  if (!fs.existsSync(dir)) return;
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* walk(full); else yield full;
  }
}

const lines = file => fs.readFileSync(file, 'utf8').split('\n').filter(Boolean).flatMap(line => {
  try { return [JSON.parse(line)]; } catch { return []; }
});

// The session ids of Link's runs, from its log.
export function linkSessionIds(log = path.join(LOCAL, 'Link', 'link.log')) {
  const ids = new Set();
  if (!fs.existsSync(log)) return ids;
  for (const match of fs.readFileSync(log, 'utf8').matchAll(/\[Agent\][^\n]*?session ([0-9a-f-]{36})/g)) ids.add(match[1]);
  return ids;
}

// A session: { agent, id, origin, model, effort, cost, events }. Events are in time order:
//   { t, type: 'user', text } a message the user wrote; { t, type: 'text', text } the agent's words;
//   { t, type: 'tool', name, command, edit, editLines, search, error } a tool call;
//   { t, type: 'usage', input, cached, output } one model request (input counts the cached part too).
export function readSession({ agent, file }) {
  return agent === 'codex' ? readCodex(file) : readClaude(file);
}

function readClaude(file) {
  const session = { agent: 'claude', file, id: null, origin: null, model: null, effort: null, cost: null, events: [] };
  const tools = new Map(), requests = new Set(), models = new Map();
  for (const line of lines(file)) {
    const t = Date.parse(line.timestamp);
    session.id ??= line.sessionId;
    // The session's running cost, carried over a resume too: the largest is the session's.
    if (line.type === 'cost-state') session.cost = Math.max(session.cost ?? 0, line.totalCostUSD || 0);
    if (line.type === 'api-request') session.effort ??= line.params?.output_config?.effort ?? null;
    if (line.isSidechain) continue;
    if (line.type === 'user' && !line.isMeta) {
      session.origin ??= line.entrypoint;
      const content = line.message?.content;
      if (Array.isArray(content) && content.some(c => c.type === 'tool_result')) {
        for (const c of content) if (c.type === 'tool_result' && c.is_error && tools.has(c.tool_use_id)) tools.get(c.tool_use_id).error = true;
        continue;
      }
      const text = typeof content === 'string' ? content : (content || []).filter(c => c.type === 'text').map(c => c.text).join('\n');
      // Commands, their output and reminders come as tagged text, not as the user's words.
      if (text.trim() && !text.trimStart().startsWith('<')) session.events.push({ t, type: 'user', text });
    }
    if (line.type === 'assistant' && line.message) {
      const m = line.message;
      if (m.model && m.model !== '<synthetic>') models.set(m.model, (models.get(m.model) || 0) + 1);
      if (m.usage && !requests.has(m.id)) {
        requests.add(m.id);
        const u = m.usage;
        const cached = u.cache_read_input_tokens || 0;
        session.events.push({ t, type: 'usage', input: (u.input_tokens || 0) + (u.cache_creation_input_tokens || 0) + cached, cached, output: u.output_tokens || 0 });
      }
      for (const c of m.content || []) {
        if (c.type === 'text' && c.text.trim()) session.events.push({ t, type: 'text', text: c.text });
        if (c.type === 'tool_use') {
          const event = toolEvent(t, c.name, c.input || {});
          tools.set(c.id, event);
          session.events.push(event);
        }
      }
    }
  }
  session.model = [...models].sort((a, b) => b[1] - a[1])[0]?.[0] ?? null;
  return session;
}

function toolEvent(t, name, input) {
  const command = typeof input === 'string' ? input : input.command || input.cmd || '';
  const editTools = ['Edit', 'Write', 'MultiEdit', 'NotebookEdit'];
  const edit = editTools.includes(name);
  let editLines = 0;
  if (name === 'Write') editLines = countLines(input.content);
  if (name === 'Edit') editLines = Math.max(countLines(input.old_string), countLines(input.new_string));
  if (name === 'MultiEdit') editLines = (input.edits || []).reduce((n, e) => n + Math.max(countLines(e.old_string), countLines(e.new_string)), 0);
  const search = ['Grep', 'Glob', 'LS'].includes(name) || (['Bash', 'PowerShell'].includes(name) && SEARCH.test(command));
  return { t, type: 'tool', name, command, edit, editLines, search, error: false };
}

const countLines = text => (text ? String(text).split('\n').length : 0);

function readCodex(file) {
  const session = { agent: 'codex', file, id: null, origin: null, model: null, effort: null, cost: null, events: [] };
  const calls = new Map();
  let totals = null, responses = 0;
  for (const line of lines(file)) {
    const t = Date.parse(line.timestamp);
    const p = line.payload || {};
    if (line.type === 'session_meta') {
      session.id ??= p.id;
      session.origin ??= p.originator;
    }
    if (line.type === 'turn_context') {
      session.model ??= p.model;
      session.effort ??= p.effort ?? null;
    }
    if (line.type === 'event_msg' && p.type === 'token_count' && p.info?.total_token_usage) {
      totals = p.info.total_token_usage;
      responses++;
    }
    if (line.type !== 'response_item') continue;
    if (p.type === 'message') {
      const texts = (p.content || []).filter(c => /text/.test(c.type)).map(c => c.text || '');
      if (p.role === 'assistant') {
        const text = texts.join('\n');
        if (text.trim()) session.events.push({ t, type: 'text', text });
      } else if (p.role === 'user') {
        // The rules, the folder's AGENTS.md and the environment come as tagged user items, not as the user's words.
        const own = texts.filter(text => !text.trimStart().startsWith('<') && !text.startsWith('# AGENTS.md'));
        if (own.join('').trim()) session.events.push({ t, type: 'user', text: own.join('\n') });
      }
    }
    if (p.type === 'function_call' || p.type === 'custom_tool_call') {
      const command = String(p.input ?? p.arguments ?? '');
      const edit = p.name === 'apply_patch' || /apply_patch/.test(command);
      const editLines = edit ? command.split(/\\n|\n/).filter(l => /^[+-](?![+-]{2})/.test(l)).length : 0;
      const event = { t, type: 'tool', name: p.name, command, edit, editLines, search: SEARCH.test(command), error: false };
      calls.set(p.call_id, event);
      session.events.push(event);
    }
    if ((p.type === 'function_call_output' || p.type === 'custom_tool_call_output') && calls.has(p.call_id)) {
      const output = typeof p.output === 'string' ? p.output : JSON.stringify(p.output);
      if (/Script failed|Exit code: [1-9]|exit_code\\?"?:\s*[1-9]|"success\\?"?:\s*false/.test(output)) calls.get(p.call_id).error = true;
    }
  }
  // Codex's counts are the session's running totals: the run's usage is the last one, as one entry.
  if (totals) {
    const last = session.events.at(-1)?.t ?? 0;
    session.events.push({ t: last, type: 'usage', input: totals.input_tokens || 0, cached: totals.cached_input_tokens || 0, output: totals.output_tokens || 0, requests: responses });
  }
  return session;
}

const harnessOf = (session, linkIds) => {
  if (linkIds.has(session.id)) return 'link';
  if (session.agent === 'claude') return session.origin === 'claude-desktop' ? 'claude-app' : 'claude-cli';
  return /exec|cli/i.test(session.origin || '') ? 'codex-cli' : 'codex-app';
};

// The numbers of a run from its sessions. `followups` is how many messages the task itself gives after the prompt.
export function summarize(sessions, { followups = 0, linkIds = linkSessionIds() } = {}) {
  const stats = {
    harness: '', model: '', effort: '', sessions: sessions.length, wallSec: null, firstEditSec: null, userWaitSec: 0,
    turns: 0, toolCalls: 0, searchesBeforeEdit: 0, toolErrors: 0, userMessages: 0, extraUserMessages: 0, questions: 0,
    helpers: 0, tokensIn: 0, tokensCached: 0, tokensOut: 0, costUsd: null, ranTests: false, servedPage: false, tookScreenshot: false,
  };
  // No session found: nothing measured, which is not the same as zero.
  if (!sessions.length) return Object.fromEntries(Object.keys(stats).map(key => [key, key === 'sessions' ? 0 : null]));
  stats.harness = [...new Set(sessions.map(s => harnessOf(s, linkIds)))].join('+');
  stats.model = [...new Set(sessions.map(s => s.model).filter(Boolean))].join('+');
  stats.effort = [...new Set(sessions.map(s => s.effort).filter(Boolean))].join('+');
  const costs = sessions.map(s => s.cost).filter(c => c != null);
  if (costs.length) stats.costUsd = Number(costs.reduce((a, b) => a + b, 0).toFixed(4));

  const events = sessions.flatMap(s => s.events).sort((a, b) => a.t - b.t);
  let wall = 0;
  for (const s of sessions) {
    const times = s.events.map(e => e.t).filter(Number.isFinite);
    if (times.length) wall += Math.max(...times) - Math.min(...times);
  }
  stats.wallSec = Math.round(wall / 1000);
  const start = events.find(e => e.type === 'user')?.t ?? events[0]?.t;
  let edited = false, previous = null;
  for (const e of events) {
    if (e.type === 'usage') {
      stats.turns += e.requests ?? 1;
      stats.tokensIn += e.input;
      stats.tokensCached += e.cached;
      stats.tokensOut += e.output;
    }
    if (e.type === 'user') {
      stats.userMessages++;
      if (stats.userMessages > 1 && previous) stats.userWaitSec += Math.round((e.t - previous.t) / 1000);
      // The agent's words just before a user's message ended its turn: a question there waited for the user.
      if (previous?.type === 'text' && ASKS.test(previous.text)) stats.questions++;
    }
    if (e.type === 'tool') {
      stats.toolCalls++;
      if (e.error) stats.toolErrors++;
      if (e.search && !edited) stats.searchesBeforeEdit++;
      if (e.edit && !edited) {
        edited = true;
        stats.firstEditSec = Math.round((e.t - start) / 1000);
      }
      if (/^(Agent|Task|spawn_agent)$/.test(e.name)) stats.helpers++;
      if (TESTS.test(e.command)) stats.ranTests = true;
      if (SERVES.test(e.command)) stats.servedPage = true;
      if (SHOTS.test(e.command) || SHOTS.test(e.name)) stats.tookScreenshot = true;
    }
    if (e.type !== 'usage') previous = e;
  }
  if (previous?.type === 'text' && ASKS.test(previous.text)) stats.questions++;
  stats.extraUserMessages = Math.max(0, stats.userMessages - 1 - followups);
  return stats;
}

// Whether the agent asked or offered options before changing much: the first of its words that ends a turn and asks,
// with the lines its edits changed before that. For tasks that score judgment.
export function firstAsk(sessions) {
  const events = sessions.flatMap(s => s.events).filter(e => e.type !== 'usage').sort((a, b) => a.t - b.t);
  let lines = 0;
  for (let i = 0; i < events.length; i++) {
    const e = events[i];
    if (e.type === 'tool' && e.edit) lines += e.editLines;
    const endsTurn = i === events.length - 1 || events[i + 1].type === 'user';
    if (e.type === 'text' && endsTurn && ASKS.test(e.text)) return { asked: true, linesBefore: lines, text: e.text };
  }
  return { asked: false, linesBefore: lines, text: '' };
}
