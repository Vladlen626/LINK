// A stand-in for Link's remote API, to look at remote/ in a browser without Link: node fake-link.js <remote folder> [port]
// The page gets a token already, so it opens on the task list. /?task opens the first task, /?new the new-task form.
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');

const root = process.argv[2];
const port = Number(process.argv[3] || 8799);
const id = '11111111-2222-3333-4444-555555555555';
const now = () => new Date().toISOString();
const task = {
  id, project: 'Game', goal: 'Make the jump', status: 'Needs you', agent: 'Claude Code', working: false,
  waits: 'your answer', noStop: true, updated: now(), lastWords: 'Which jump should stay?'
};
const view = {
  task,
  entries: [{ at: now(), kind: 'You', text: 'Make the jump <b>not bold</b>' }],
  question: { text: 'Which jump?', options: [{ label: 'Keep double', description: 'less code' }, { label: 'One jump', description: '' }], multiSelect: false },
  decision: { text: 'Claude was not allowed: Bash(rm x)', button: 'Allow and retry', canRemember: true },
  limit: null, noStopPending: 'Keep double', changedFiles: ['a.cs'], usage: 'This task: 2 runs', takesWords: true
};
const api = {
  '/api/tasks': [task],
  [`/api/tasks/${id}`]: view,
  '/api/usage': { line: 'Spent today $1.00 · month $9.00', hint: 'hint' },
  '/api/projects': [{ id: 'p', name: 'Game', active: true, hasWorkspace: true }],
  '/api/push': { publicKey: 'BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8' }
};
const types = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html', '.webmanifest': 'application/manifest+json' };

http.createServer((request, response) => {
  const [route, query = ''] = request.url.split('?');
  if (route.startsWith('/api/')) {
    // No change feed: the page retries in 5 s, past headless Edge's virtual time, which an open or busy poll would hold up.
    if (route === '/api/events') {
      response.writeHead(503);
      return response.end();
    }
    response.writeHead(200, { 'Content-Type': 'application/json' });
    return response.end(JSON.stringify(api[route] ?? { ok: true }));
  }

  const file = path.join(root, route === '/' ? 'index.html' : route.slice(1));
  if (!fs.existsSync(file)) {
    response.writeHead(404);
    return response.end();
  }

  let body = fs.readFileSync(file, 'utf8');
  if (route === '/') {
    const open = query.includes('task') ? "document.querySelector('.task').click()" : query.includes('new') ? "document.getElementById('new').click()" : '';
    body = body.replace('<script src="/app.js">',
      `<script>localStorage.setItem('link.token', 't'); window.onerror = message => document.title = 'ERROR ' + message;` +
      `setTimeout(() => { ${open} }, 400);</script><script src="/app.js">`);
  }
  response.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'text/plain' });
  response.end(body);
}).listen(port, '127.0.0.1');
