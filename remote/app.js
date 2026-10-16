// Link's tasks from a phone or another computer. Everything shown comes from Link's API with this device's token;
// text is always set as text, never as HTML, since it is the agents' words.
'use strict';

const view = document.getElementById('view');
const title = document.getElementById('title');
const back = document.getElementById('back');
const newButton = document.getElementById('new');
const TOKEN = 'link.token';

let screen = { name: 'list' };
let version = -1;
let drawing = false;

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (value === undefined || value === null || value === false) continue;
    if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
    else if (key === 'text') node.textContent = value;
    else if (key in node && key !== 'list') node[key] = value;
    else node.setAttribute(key, value === true ? '' : value);
  }
  for (const child of children.flat()) if (child) node.append(child);
  return node;
}

async function api(path, body) {
  const response = await fetch(path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { 'Authorization': 'Bearer ' + (localStorage.getItem(TOKEN) || ''), 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const data = await response.json().catch(() => ({}));
  if (response.status === 401 && path !== '/api/pair') {
    localStorage.removeItem(TOKEN);
    show({ name: 'pair' });
    throw new Error(data.error || 'Pair this device first');
  }
  if (!response.ok) throw new Error(data.error || response.statusText);
  return data;
}

function when(at) {
  const date = new Date(at);
  const today = new Date().toDateString() === date.toDateString();
  return today ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : date.toLocaleDateString([], { day: 'numeric', month: 'short' });
}

function show(next) {
  screen = next;
  draw();
}

async function draw() {
  if (drawing) return;
  drawing = true;
  try {
    const paired = !!localStorage.getItem(TOKEN);
    if (!paired) screen = { name: 'pair' };
    back.hidden = screen.name === 'list' || screen.name === 'pair';
    newButton.hidden = screen.name !== 'list';
    const content = screen.name === 'pair' ? pairView()
      : screen.name === 'new' ? await newView()
      : screen.name === 'task' ? await taskView(screen.id)
      : await listView();
    view.replaceChildren(content);
  } catch (error) {
    view.replaceChildren(el('p', { className: 'error', text: error.message }));
  } finally {
    drawing = false;
  }
}

function pairView() {
  title.textContent = 'Pair with Link';
  const code = el('input', { inputMode: 'numeric', autocomplete: 'one-time-code', placeholder: '6-digit code', maxLength: 6 });
  const name = el('input', { placeholder: 'This device', value: /Android|iPhone/.test(navigator.userAgent) ? 'Phone' : 'Browser' });
  const error = el('p', { className: 'error' });
  const pair = async () => {
    try {
      const data = await api('/api/pair', { code: code.value.trim(), name: name.value.trim() });
      localStorage.setItem(TOKEN, data.token);
      show({ name: 'list' });
    } catch (failure) {
      error.textContent = failure.message;
    }
  };
  return el('div', {},
    el('p', { className: 'meta', text: 'In Link: Settings → Remote control → Pair a device. Enter the code it shows.' }),
    el('div', { className: 'card' }, code, el('div', { className: 'row' }, name), el('div', { className: 'row' }, el('button', { onclick: pair, text: 'Pair' }))),
    error);
}

async function listView() {
  title.textContent = 'Link';
  const [tasks, usage] = await Promise.all([api('/api/tasks'), api('/api/usage')]);
  const spent = usage.line ? el('p', { className: 'meta', title: usage.hint, text: usage.line }) : null;
  const top = el('div', {}, notifyButton(), spent);
  if (tasks.length === 0) return el('div', {}, top, el('p', { className: 'empty', text: 'No tasks yet: + New starts one.' }));
  return el('div', {}, top, tasks.map(task => el('div', {
    className: 'card task' + (task.waits ? ' waits' : ''),
    onclick: () => show({ name: 'task', id: task.id })
  },
    el('div', { className: 'goal', text: task.goal }),
    el('div', { className: 'meta' },
      `${task.project} · ${task.agent ? task.agent + ' · ' : ''}${task.status} · ${when(task.updated)}`,
      task.working ? el('span', { className: 'badge work', text: 'working' }) : null,
      task.waits ? el('span', { className: 'badge warn', text: 'needs you: ' + task.waits }) : null,
      task.noStop ? el('span', { className: 'badge', text: 'No-stop' }) : null),
    task.lastWords ? el('div', { className: 'meta words', text: task.lastWords.slice(0, 220) }) : null)));
}

// Notifications need HTTPS (Tailscale gives it) and a tap: the browser asks once, then Link's alerts come while you are away.
function notifyButton() {
  if (!('PushManager' in window) || !window.isSecureContext || Notification.permission === 'denied' || localStorage.getItem('link.push')) return null;
  const button = el('button', { className: 'plain', text: '🔔 Notify me when a task needs me' });
  button.addEventListener('click', async () => {
    try {
      if (await Notification.requestPermission() !== 'granted') return button.remove();
      const registration = await navigator.serviceWorker.ready;
      const { publicKey } = await api('/api/push');
      const padded = (publicKey + '==='.slice((publicKey.length + 3) % 4)).replace(/-/g, '+').replace(/_/g, '/');
      const key = Uint8Array.from(atob(padded), char => char.charCodeAt(0));
      const subscription = await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: key });
      await api('/api/push', subscription.toJSON());
      localStorage.setItem('link.push', '1');
      button.remove();
    } catch (failure) {
      button.textContent = 'Notifications: ' + failure.message;
    }
  });
  return button;
}

async function taskView(id) {
  const data = await api('/api/tasks/' + id);
  const task = data.task;
  title.textContent = task.goal;
  const error = el('p', { className: 'error' });
  const act = async (path, body) => {
    try {
      await api(`/api/tasks/${id}/${path}`, body ?? {});
      draw();
    } catch (failure) {
      error.textContent = failure.message;
    }
  };

  const parts = [];
  parts.push(el('div', { className: 'meta' }, `${task.project} · ${task.agent ? task.agent + ' · ' : ''}${task.status}`,
    task.working ? el('span', { className: 'badge work', text: 'working' }) : null));

  const noStop = el('input', { type: 'checkbox', checked: task.noStop, onchange: () => act('nostop', { on: noStop.checked }) });
  parts.push(el('div', { className: 'row' },
    el('label', { className: 'check' }, noStop, 'No-stop: take its own recommended choices'),
    task.working ? el('button', { className: 'stop', onclick: () => act('stop'), text: 'Stop' }) : null));
  if (data.noStopPending) parts.push(el('div', { className: 'card waits', text: `No-stop takes «${data.noStopPending}» in a few seconds.` }));

  if (data.question) {
    const question = data.question;
    const card = el('div', { className: 'card waits' }, el('div', { className: 'goal', text: question.text }));
    if (question.multiSelect) {
      const boxes = question.options.map(option => ({ option, box: el('input', { type: 'checkbox' }) }));
      card.append(el('div', { className: 'options' }, boxes.map(({ option, box }) =>
        el('label', { className: 'check' }, box, el('span', {}, el('b', { text: option.label }), option.description ? ' — ' + option.description : ''))),
        el('button', { text: 'Send', onclick: () => act('reply', { words: boxes.filter(item => item.box.checked).map(item => item.option.label).join(', ') }) })));
    } else {
      card.append(el('div', { className: 'options' }, question.options.map(option =>
        el('button', { className: 'plain', onclick: () => act('reply', { words: option.label }) },
          el('b', { text: option.label }), option.description ? el('div', { className: 'meta', text: option.description }) : null))));
    }
    parts.push(card);
  }

  if (data.decision) {
    const always = el('input', { type: 'checkbox' });
    parts.push(el('div', { className: 'card waits' },
      el('div', { className: 'words', text: data.decision.text }),
      data.decision.canRemember ? el('label', { className: 'check' }, always, 'Always allow for this project') : null,
      el('div', { className: 'row' }, el('button', { text: data.decision.button, onclick: () => act('decide', { always: always.checked }) }))));
  }

  if (data.limit) parts.push(el('div', { className: 'card waits words', text: data.limit }));

  if (task.lastWords) parts.push(el('div', { className: 'card words', text: task.lastWords }));

  if (data.takesWords) {
    const words = el('textarea', { placeholder: task.working ? 'Correct it now: it stops the step and goes on with your words' : 'Reply' });
    parts.push(el('div', {}, words, el('div', { className: 'row' },
      el('button', { text: task.working ? 'Steer' : 'Send', onclick: () => words.value.trim() && act('reply', { words: words.value.trim() }) }))));
  }
  parts.push(error);

  if (data.changedFiles.length > 0) {
    parts.push(el('details', { className: 'card' }, el('summary', { text: `${data.changedFiles.length} changed files` }),
      data.changedFiles.map(file => el('div', { className: 'meta', text: file }))));
  }
  if (data.usage) parts.push(el('p', { className: 'meta', text: data.usage }));
  parts.push(el('details', { className: 'card', open: true }, el('summary', { text: 'History' }),
    data.entries.slice().reverse().map(entry => el('div', { className: 'entry' },
      el('div', { className: 'kind', text: `${when(entry.at)} · ${entry.kind}` }),
      el('div', { className: 'words', text: entry.text })))));
  return el('div', {}, parts);
}

async function newView() {
  title.textContent = 'New task';
  const projects = await api('/api/projects');
  const project = el('select', {}, projects.map(each =>
    el('option', { value: each.id, selected: each.active, disabled: !each.hasWorkspace, text: each.name + (each.hasWorkspace ? '' : ' (no folder)') })));
  const goal = el('textarea', { placeholder: 'What should the agent do?' });
  const planFirst = el('input', { type: 'checkbox' });
  const noStop = el('input', { type: 'checkbox' });
  const error = el('p', { className: 'error' });
  const start = async () => {
    if (!goal.value.trim()) return;
    try {
      await api('/api/tasks', { projectId: project.value, goal: goal.value.trim(), planFirst: planFirst.checked, noStop: noStop.checked });
      show({ name: 'list' });
    } catch (failure) {
      error.textContent = failure.message;
    }
  };
  return el('div', {}, project, el('div', { className: 'row' }, goal),
    el('label', { className: 'check' }, planFirst, 'Plan first: a plan, then you run it'),
    el('label', { className: 'check' }, noStop, 'No-stop: it takes the choices it recommends and asks for nothing'),
    el('button', { onclick: start, text: 'Start' }), error);
}

// Link answers once its tasks change, or after a while: the screen is drawn again either way. A typed reply is kept.
async function follow() {
  for (;;) {
    if (!localStorage.getItem(TOKEN)) {
      await new Promise(resolve => setTimeout(resolve, 2000));
      continue;
    }
    try {
      const data = await api('/api/events?since=' + version);
      const changed = data.version !== version;
      version = data.version;
      const typing = document.activeElement && ['TEXTAREA', 'INPUT', 'SELECT'].includes(document.activeElement.tagName);
      if (changed && !typing && screen.name !== 'new' && screen.name !== 'pair') draw();
    } catch {
      await new Promise(resolve => setTimeout(resolve, 5000));
    }
  }
}

back.addEventListener('click', () => show({ name: 'list' }));
newButton.addEventListener('click', () => show({ name: 'new' }));
if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js').catch(() => {});
draw();
follow();
