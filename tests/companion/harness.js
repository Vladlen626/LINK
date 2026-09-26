// Checks for the companion, run by Link.Desktop.Tests in headless Edge with virtual time, or by opening run.html in a
// browser started with --allow-file-access-from-files. Each check gets fresh pages in iframes: a mock ChatGPT
// conversation (c/chat-1.html) or new chat (new-chat.html) with the real content.js, and a stand-in for LINK.
const VERSION = "0.7.0";
// The companion's own files, for mock pages wherever they are.
const COMPANION = new URL("../../companion/", location.href).href;
const checks = [];
const output = [];
let failures = 0;
let currentCheck = "";

function check(name, body) {
  checks.push({ name, body });
}

function expect(what, actual, expected) {
  const passed = JSON.stringify(actual) === JSON.stringify(expected);
  if (!passed) failures++;
  output.push(passed
    ? `PASS ${currentCheck}: ${what}`
    : `FAIL ${currentCheck}: ${what}\n  expected: ${JSON.stringify(expected)}\n  actual:   ${JSON.stringify(actual)}`);
}

const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

/** What a condition gives once it holds, or null when it does not within the time. Times are virtual in the test run. */
async function poll(condition, timeout) {
  const started = Date.now();
  while (Date.now() - started < timeout) {
    const value = condition();
    if (value) return value;
    await sleep(20);
  }
  return condition() || null;
}

/** Waits for a condition; the check fails if it never holds. */
async function until(what, condition, timeout = 15000) {
  const value = await poll(condition, timeout);
  if (!value) expect(`${what} (within ${timeout} ms)`, false, true);
  return value;
}

async function runChecks() {
  const results = document.getElementById("results");
  for (const { name, body } of checks) {
    currentCheck = name;
    // Shown as it goes, so a run cut short still tells where it stopped.
    results.textContent = `${output.join("\n")}\nRUNNING ${name}`;
    try {
      await body();
    } catch (error) {
      failures++;
      output.push(`FAIL ${name}: threw ${error?.stack ?? error}`);
    }
    for (const frame of document.querySelectorAll("iframe")) frame.remove();
  }
  output.push(`DONE ${checks.length} scenarios, ${failures} failed`);
  results.textContent = output.join("\n");
}

/** LINK's storage for the companion, kept outside the pages, so that it outlives a page reload like LINK's does. */
function memoryStorage() {
  const data = new Map();
  const copy = value => JSON.parse(JSON.stringify(value));
  return {
    data,
    async get(keys) {
      if (keys === null) return Object.fromEntries([...data].map(([key, value]) => [key, copy(value)]));
      const wanted = Array.isArray(keys) ? keys : [keys];
      return Object.fromEntries(wanted.filter(key => data.has(key)).map(key => [key, copy(data.get(key))]));
    },
    async set(items) {
      for (const [key, value] of Object.entries(items)) data.set(key, copy(value));
    },
    async remove(keys) {
      for (const key of Array.isArray(keys) ? keys : [keys]) data.delete(key);
    }
  };
}

/** LINK as the companion's events reach it: it records them and says which requests it dropped. */
function fakeBridge() {
  const bridge = {
    events: [],
    ended: new Map(),
    event(event) {
      bridge.events.push(event);
      const reason = bridge.ended.get(event.requestId);
      return reason ? { ok: false, status: 404, reason } : { ok: true };
    },
    of(requestId, state) {
      return bridge.events.filter(event => event.requestId === requestId && (!state || event.state === state));
    }
  };
  return bridge;
}

/** LINK's end of the page's channel, as embedded-host.js gives it to the companion. */
function hostApi(bridge, storage) {
  const api = {
    version: VERSION,
    listeners: [],
    onMessage: listener => {
      api.listeners.push(listener);
    },
    async sendMessage(message) {
      if (message.type === "link-event") return bridge.event(message.event);
      return { ok: true };
    },
    storage
  };
  return api;
}

/** LINK hands the page's companion a message: its listeners get it, and the first answer wins. */
function sendToPage(api, message) {
  return new Promise(resolve => {
    let answered = false;
    for (const listener of [...api.listeners]) {
      listener(message, response => {
        if (!answered) {
          answered = true;
          resolve(response);
        }
      });
    }
    setTimeout(() => resolve(undefined), 0);
  });
}

function promptRequest(conversationId, prompt, extra = {}) {
  return { requestId: crypto.randomUUID(), conversationId, kind: "prompt", prompt, effort: "normal", attachments: [], attempt: 1, sent: false, ...extra };
}

async function openFrame(src) {
  const frame = document.createElement("iframe");
  frame.style.cssText = "width: 1000px; height: 700px";
  frame.src = src;
  // A blank frame loads while it is being added, so the listener comes first.
  const loaded = new Promise(resolve => frame.addEventListener("load", resolve, { once: true }));
  document.body.append(frame);
  await loaded;
  return frame;
}

/** Runs a script in the page; with `api`, the companion there finds it as LINK's end of the channel. */
async function loadScript(frame, api, src) {
  if (api) frame.contentWindow.linkHostApi = api;
  const script = frame.contentDocument.createElement("script");
  script.src = src;
  await new Promise((resolve, reject) => {
    script.onload = resolve;
    script.onerror = () => reject(new Error(`${src} did not load`));
    frame.contentDocument.head.append(script);
  });
}

/**
 * A ChatGPT conversation page with the mock chat in it: the classic UI, the logged-out new one with `ui: "octane"`, or
 * the logged-in one of late September 2026 with `ui: "shell"`.
 */
async function openChat(options = {}) {
  // A conversation's page, or with `src: "new-chat.html"` a new chat's, which has no conversation yet.
  const frame = await openFrame(options.src ?? "c/chat-1.html");
  const mocks = { octane: OctaneMock, shell: ShellMock };
  const chat = new (mocks[options.ui] ?? ChatGptMock)(frame.contentWindow, options);
  const conversationId = frame.contentWindow.location.pathname.match(/\/c\/([^/?#]+)/)?.[1] ?? null;
  return {
    frame,
    chat,
    conversationId,
    /** Starts a copy of the real content script in the page, on the given host API. */
    start: api => loadScript(frame, api, `${COMPANION}content.js`)
  };
}

const ANSWER_BLOCKS = [
  { html: "<p></p>", words: "Первый абзац готов. Второе предложение здесь.".split(" ") },
  { html: "<ul><li>пункт один</li><li>пункт два</li></ul>" },
  { html: '<pre><code class="language-js">let x = 1;</code></pre>' }
];
const ANSWER_MARKDOWN = "Первый абзац готов. Второе предложение здесь.\n\n- пункт один\n- пункт два\n\n```js\nlet x = 1;\n```";
const MODES = [
  ["Мгновенный", "Отвечает сразу"],
  ["Средний", "Думает, когда нужно"],
  ["Высокий", "Думает дольше"],
  ["Очень высокий", "Думает дольше всего"]
];

/**
 * ChatGPT's conversation page as the companion sees it: articles with user and assistant messages, the composer
 * form with its Send / Stop button and its mode picker (a Radix menu opened on pointerdown, rendered in a portal),
 * and the header's model picker. Options vary what the real page was seen to do or might do.
 */
class ChatGptMock {
  constructor(window, options = {}) {
    this.window = window;
    this.document = window.document;
    this.options = {
      markdown: true,       // the answer sits in a Markdown root
      actions: true,        // a finished turn gets its action buttons
      stopAfter: "removed", // Stop after the answer: "removed", "hidden" (kept, display: none) or "visible" (stuck)
      thinkingMs: 400,      // "Думаю" before the answer starts
      tokenMs: 80,
      takesPrompt: true,    // false: Send does nothing
      itemsChoose: true,    // false: the mode menu's items ignore clicks
      composerPicker: true, // false: the older UI, whose header picker ("ChatGPT") offers Auto, Instant and Thinking
      pickerPrefix: "",     // what the composer's picker shows before the mode, e.g. "Рассуждение · "
      mode: "Средний",
      turns: 2,
      ...options
    };
    this.sent = [];
    this.stopClicks = 0;
    this.headerOpens = 0;
    this.generating = false;
    this.finished = false;
    this.nextId = 1;
    this.build();
  }

  element(tag, attributes = {}, html = "") {
    const element = this.document.createElement(tag);
    for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, value);
    element.innerHTML = html;
    return element;
  }

  wait(milliseconds) {
    return new Promise(resolve => this.window.setTimeout(resolve, milliseconds));
  }

  build() {
    this.document.body.innerHTML = `
      <header><button data-testid="model-switcher-dropdown-button" aria-haspopup="menu" aria-label="Селектор модели">ChatGPT</button></header>
      <main>
        <div id="thread"><div class="turns"></div><div aria-hidden="true" class="spacer"></div></div>
        <form data-type="unified-composer">
          <div contenteditable="true" id="prompt-textarea" class="ProseMirror" role="textbox"></div>
          <div class="footer">
            <button type="button" data-testid="composer-plus-btn" aria-haspopup="menu" aria-label="Добавить файлы и другое">+</button>
            <button type="button" class="mode-picker" aria-haspopup="menu" aria-expanded="false" data-state="closed"><span></span><svg width="8" height="8"></svg></button>
            <button type="button" aria-label="Начало диктовки">mic</button>
            <span class="submit"></span>
          </div>
        </form>
      </main>`;
    this.turns = this.document.querySelector(".turns");
    this.form = this.document.querySelector("form");
    this.composer = this.document.getElementById("prompt-textarea");
    this.picker = this.document.querySelector(".mode-picker");
    this.header = this.document.querySelector('[data-testid="model-switcher-dropdown-button"]');
    this.headerMode = "Auto";
    this.mode = this.options.mode;
    if (!this.options.composerPicker) this.picker.remove();
    for (let turn = 0; turn < this.options.turns; turn++) {
      this.addUserTurn(`Старый вопрос ${turn + 1}`);
      const { article, root } = this.addAssistantTurn();
      root.innerHTML = `<p>Старый ответ ${turn + 1}.</p>`;
      this.addActions(article);
    }
    this.setSubmit("speech");

    this.composer.addEventListener("input", () => {
      if (!this.generating) this.setSubmit(this.composer.innerText.trim() ? "send" : "speech");
    });
    for (const [trigger, modes, choose] of [
      [this.picker, MODES, label => {
        this.mode = label;
      }],
      [this.header, [["Auto", "Решает сам"], ["Instant", "Сразу"], ["Thinking", "Думает"], ["Pro", "Для исследований"]], label => {
        this.headerMode = label;
      }]
    ]) {
      trigger.addEventListener("pointerdown", event => {
        if (event.button !== 0 || event.ctrlKey) return;
        event.preventDefault();
        if (trigger === this.header) this.headerOpens++;
        if (this.menu) this.closeMenu();
        // The logged-in header picker offered modes before June 2026; now it is a model picker without them.
        else this.openMenu(trigger, trigger === this.picker || !this.options.composerPicker ? modes : [], choose);
      });
    }
    this.document.addEventListener("keydown", event => {
      if (event.key === "Escape") this.closeMenu();
    });
  }

  get mode() {
    return this.picker.querySelector("span").textContent.slice(this.options.pickerPrefix.length);
  }

  set mode(label) {
    this.picker.querySelector("span").textContent = this.options.pickerPrefix + label;
  }

  /** A Radix menu in a portal at the end of the page, named by the trigger's aria-controls while open. */
  openMenu(trigger, modes, choose) {
    const wrapper = this.element("div", { "data-radix-popper-content-wrapper": "" });
    const menu = this.element("div", { role: "menu", id: "radix-menu", "data-state": "open" });
    for (const [label, description] of modes) {
      const item = this.element("div", { role: "menuitemradio", "aria-checked": "false" },
        `<div>${label}</div><div class="text-xs">${description}</div>`);
      if (this.options.itemsChoose) {
        item.addEventListener("click", () => {
          choose(label);
          this.closeMenu();
        });
      }
      menu.append(item);
    }
    if (!modes.length) menu.append(this.element("div", { role: "menuitem" }, "Войти"));
    wrapper.append(menu);
    this.document.body.append(wrapper);
    this.menu = wrapper;
    this.trigger = trigger;
    trigger.setAttribute("aria-expanded", "true");
    trigger.setAttribute("aria-controls", "radix-menu");
  }

  closeMenu() {
    this.menu?.remove();
    this.menu = null;
    this.trigger?.setAttribute("aria-expanded", "false");
    this.trigger?.removeAttribute("aria-controls");
  }

  setSubmit(kind, slot = this.form.querySelector(".submit")) {
    const buttons = {
      speech: '<button type="button" data-testid="composer-speech-button" aria-label="Запустить голосовой режим">voice</button>',
      send: '<button type="button" id="composer-submit-button" data-testid="send-button" aria-label="Отправить подсказку">send</button>',
      stop: '<button type="button" id="composer-submit-button" data-testid="stop-button" aria-label="Остановить потоковую передачу">stop</button>'
    };
    slot.innerHTML = buttons[kind];
    const button = slot.querySelector("button");
    if (kind === "send") button.addEventListener("click", () => this.takePrompt());
    if (kind === "stop") button.addEventListener("click", () => this.stop());
  }

  addUserTurn(text, id = `user-${this.nextId++}`) {
    const article = this.element("article", { "data-turn": "user", "data-testid": `conversation-turn-${this.nextId}` },
      `<h5 class="sr-only">Вы сказали:</h5><div data-message-author-role="user" data-message-id="${id}"><div class="whitespace-pre-wrap"></div></div>`);
    article.querySelector(".whitespace-pre-wrap").textContent = text;
    this.addActions(article);
    this.turns.append(article);
    return article;
  }

  addAssistantTurn(id = `answer-${this.nextId++}`) {
    const article = this.element("article", { "data-turn": "assistant", "data-testid": `conversation-turn-${this.nextId}` },
      `<h6 class="sr-only">ChatGPT сказал:</h6><div data-message-author-role="assistant" data-message-id="${id}"></div>`);
    const message = article.querySelector("[data-message-author-role]");
    const root = this.options.markdown ? this.element("div", { class: "markdown prose" }) : message;
    if (root !== message) message.append(root);
    this.turns.append(article);
    return { article, message, root };
  }

  addActions(article) {
    if (!this.options.actions) return;
    const bar = this.element("div", { class: "turn-actions" },
      '<button data-testid="copy-turn-action-button" aria-label="Копировать">c</button>' +
      '<button data-testid="good-response-turn-action-button" aria-label="Хороший ответ">+</button>');
    article.append(bar);
  }

  /** A prompt ChatGPT has already answered, as a reloaded page shows it. */
  addAnsweredTurn(prompt, userId) {
    this.addUserTurn(prompt, userId);
    const { article, root } = this.addAssistantTurn();
    root.innerHTML = '<p>Первый абзац готов. Второе предложение здесь.</p><ul><li>пункт один</li><li>пункт два</li></ul>' +
      '<pre><code class="language-js">let x = 1;</code></pre>';
    this.addActions(article);
  }

  takePrompt() {
    if (!this.options.takesPrompt) return;
    const text = this.composer.innerText;
    this.composer.replaceChildren();
    this.sent.push(text);
    void this.answer(text);
  }

  stop() {
    this.stopClicks++;
    this.stopped = true;
  }

  /**
   * Answers a prompt: the prompt's turn, "Думаю" while thinking (next to the answer's message, or inside it), the
   * answer word by word, then the end signs.
   */
  async answer(prompt) {
    this.generating = true;
    this.stopped = false;
    this.setSubmit("stop");
    this.addUserTurn(prompt);
    const article = this.element("article", { "data-turn": "assistant", "data-testid": `conversation-turn-${this.nextId++}` },
      '<h6 class="sr-only">ChatGPT сказал:</h6>');
    const message = this.element("div", { "data-message-author-role": "assistant", "data-message-id": `answer-${this.nextId++}` });
    const thinking = this.element("div", { class: "thinking" }, '<span class="loading-shimmer">Думаю</span>');
    if (this.options.placeholderInMessage) {
      message.append(thinking);
      article.append(message);
    } else {
      article.append(thinking);
    }
    this.turns.append(article);
    await this.wait(this.options.thinkingMs);

    const root = this.options.markdown ? this.element("div", { class: "markdown prose" }) : message;
    if (root !== message) message.append(root);
    thinking.remove();
    article.append(message);
    for (const block of ANSWER_BLOCKS) {
      if (this.stopped) break;
      root.insertAdjacentHTML("beforeend", block.html);
      const paragraph = root.lastElementChild;
      for (const [index, word] of (block.words ?? []).entries()) {
        if (this.stopped) break;
        paragraph.append(this.document.createTextNode(index ? ` ${word}` : word));
        await this.wait(this.options.tokenMs);
      }
      await this.wait(this.options.tokenMs);
    }
    this.finish(article);
  }

  finish(article) {
    this.generating = false;
    this.finished = true;
    if (this.options.stopAfter !== "visible") {
      this.setSubmit("speech");
      if (this.options.stopAfter === "hidden") {
        this.form.querySelector(".submit").insertAdjacentHTML("beforeend",
          '<button type="button" data-testid="stop-button" aria-label="Остановить потоковую передачу" style="display: none">stop</button>');
      }
    }
    this.addActions(article);
  }
}

/**
 * The new ChatGPT UI ("octane") as seen on chatgpt.com in September 2026: the transcript is an ol of
 * li[data-message-role] with the message id as the li's id; the answer sits in div[data-assistant-markdown] and its li
 * gets data-message-complete when done, with action buttons that have no test ids; the composer is a form with a
 * textarea and one submit button whose aria-label turns into its data-stop-label while ChatGPT writes; menus are
 * native popovers. Options vary what the logged-in page may do differently.
 */
class OctaneMock {
  constructor(window, options = {}) {
    this.window = window;
    this.document = window.document;
    this.options = {
      thinkingMs: 400,
      tokenMs: 80,
      mode: "Высокий",
      pickerHaspopup: true,     // false: the mode picker is only a popovertarget button
      userMarkdown: false,      // true: the prompt's bubble renders "- " lines as a list and "---" as a rule
      newTurnsBefore: false,    // true: new turns land in a list placed before the transcript
      trailingTemplate: false,  // true: a hidden user-message template sits at the end of the page
      turns: 2,
      ...options
    };
    this.sent = [];
    this.stopClicks = 0;
    this.finished = false;
    this.generating = false;
    this.build();
  }

  element(tag, attributes = {}, html = "") {
    const element = this.document.createElement(tag);
    for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, value);
    element.innerHTML = html;
    return element;
  }

  wait(milliseconds) {
    return new Promise(resolve => this.window.setTimeout(resolve, milliseconds));
  }

  build() {
    const haspopup = this.options.pickerHaspopup ? ' aria-haspopup="menu"' : "";
    this.document.body.innerHTML = `
      <header><button data-testid="model-switcher-dropdown-button" aria-haspopup="menu" aria-label="Селектор модели">ChatGPT</button></header>
      <section data-conversation-id="chat-1">
        <ol class="pending" aria-label="Новые"></ol>
        <div data-octane-server-owned-transcript><ol aria-label="Обсуждение" data-conversation-transcript></ol></div>
      </section>
      <form data-mobile-composer action="/conversation" method="post">
        <textarea id="mobile-composer-prompt" name="prompt" data-mobile-composer-prompt placeholder="Спросите ChatGPT"></textarea>
        <button type="button" aria-label="Добавить файлы и другое" popovertarget="composer-actions">+</button>
        <div id="composer-actions" popover="auto"><button type="button">Фото</button><button type="button">Файлы</button></div>
        <button type="button" class="mode-picker" popovertarget="mode-popover"${haspopup}></button>
        <div id="mode-popover" popover="auto"></div>
        <button type="submit" data-composer-submit aria-label="Отправить сообщение" data-send-label="Отправить сообщение"
          data-stop-label="Перестать формировать">↑</button>
      </form>`;
    if (this.options.trailingTemplate) {
      this.document.body.insertAdjacentHTML("beforeend", '<div hidden><ol><li data-message-role="user" id="template-user">' +
        'Шаблон сообщения</li><li data-message-role="assistant" id="template-answer">Шаблон ответа</li></ol></div>');
    }
    this.transcript = this.document.querySelector("[data-conversation-transcript]");
    this.newTurns = this.options.newTurnsBefore ? this.document.querySelector(".pending") : this.transcript;
    this.form = this.document.querySelector("form");
    this.composer = this.document.querySelector("textarea");
    this.submit = this.document.querySelector("[data-composer-submit]");
    this.picker = this.document.querySelector(".mode-picker");
    this.popover = this.document.getElementById("mode-popover");
    this.mode = this.options.mode;
    for (const [label, description] of MODES) {
      const item = this.element("button", { type: "button" }, `<div>${label}</div><div>${description}</div>`);
      item.addEventListener("click", () => {
        this.mode = label;
        this.popover.hidePopover();
      });
      this.popover.append(item);
    }
    for (let turn = 0; turn < this.options.turns; turn++) {
      this.addUserTurn(`Старый вопрос ${turn + 1}`, this.transcript);
      const answer = this.addAssistantTurn(this.transcript);
      answer.root.innerHTML = `<p>Старый ответ ${turn + 1}.</p>`;
      this.complete(answer.li);
    }
    this.form.addEventListener("submit", event => {
      event.preventDefault();
      if (this.generating) {
        this.stopClicks++;
        this.stopped = true;
      } else if (this.composer.value.trim()) {
        this.takePrompt();
      }
    });
  }

  get mode() {
    return this.picker.textContent;
  }

  set mode(label) {
    this.picker.textContent = label;
  }

  get menu() {
    return this.popover.matches(":popover-open") ? this.popover : null;
  }

  addUserTurn(text, list = this.newTurns) {
    const li = this.element("li", { "data-message-role": "user", id: crypto.randomUUID() },
      '<h5 class="sr-only">Вы сказали:</h5><div data-submit-message-animation-target><button type="button" data-user-message-bubble></button></div>');
    const bubble = li.querySelector("button");
    if (this.options.userMarkdown) {
      // Lines as the page might render them: "- " starts a list item and "---" is a rule.
      for (const line of text.split("\n").filter(line => line.trim())) {
        bubble.append(line.startsWith("- ") ? this.element("ul", {}, `<li></li>`) : line.trim() === "---" ? this.element("hr") : this.element("p"));
        const last = bubble.lastElementChild;
        (last.querySelector("li") ?? last).textContent = line.startsWith("- ") ? line.slice(2) : line.trim() === "---" ? "" : line;
      }
    } else {
      bubble.append(this.element("p", { "data-user-message-copy": "" }));
      bubble.firstElementChild.textContent = text;
    }
    list.append(li);
    return li;
  }

  addAssistantTurn(list = this.newTurns) {
    const li = this.element("li", { "data-message-role": "assistant", id: crypto.randomUUID() },
      '<h6 class="sr-only">ChatGPT сказал:</h6><div><div><div data-assistant-markdown></div></div></div>');
    list.append(li);
    return { li, root: li.querySelector("[data-assistant-markdown]") };
  }

  /** A finished answer: ChatGPT's completion flag on its li and the action buttons under it. */
  complete(li) {
    li.setAttribute("data-message-complete", "");
    li.append(this.element("div", {},
      '<button type="button" aria-label="Копировать ответ">c</button><button type="button" aria-label="Поделиться">s</button>'));
  }

  takePrompt() {
    const text = this.composer.value;
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set.call(this.composer, "");
    this.sent.push(text);
    void this.answer(text);
  }

  async answer(prompt) {
    this.generating = true;
    this.stopped = false;
    this.submit.setAttribute("aria-label", this.submit.dataset.stopLabel);
    this.addUserTurn(prompt);
    const { li, root } = this.addAssistantTurn();
    const status = this.element("div", { role: "status" }, "Думаю");
    li.prepend(status);
    await this.wait(this.options.thinkingMs);
    status.remove();
    li.setAttribute("data-assistant-content-started", "");
    for (const [index, block] of ANSWER_BLOCKS.entries()) {
      if (this.stopped) break;
      root.insertAdjacentHTML("beforeend", block.html);
      const part = root.lastElementChild;
      part.setAttribute("data-assistant-stream-block-index", String(index));
      for (const [position, word] of (block.words ?? []).entries()) {
        if (this.stopped) break;
        part.append(this.document.createTextNode(position ? ` ${word}` : word));
        await this.wait(this.options.tokenMs);
      }
      await this.wait(this.options.tokenMs);
    }
    this.complete(li);
    this.submit.setAttribute("aria-label", this.submit.dataset.sendLabel);
    this.generating = false;
    this.finished = true;
  }
}

/**
 * ChatGPT's page as the logged-in chat showed it in late September 2026: a turn is a div[data-turn-key] that holds
 * the prompt and its answer, each a unit named by data-chatgpt-search-unit-key ("…:0:user", "…:1:assistant") with
 * its message ids; the answer's Markdown root is typed out under data-markdown-animated; the prompt and the finished
 * answer have action buttons (.turn-action-controls) without test ids; the composer's last button turns into
 * "Остановить" while ChatGPT writes; the picker's Radix menu sets the effort on a slider, a level per arrow key.
 */
class ShellMock {
  constructor(window, options = {}) {
    this.window = window;
    this.document = window.document;
    this.options = {
      thinkingMs: 400,
      tokenMs: 80,
      mode: "Высокий",
      typedAfterStop: false, // true: Stop goes when the first paragraph is there, and the rest is typed out slowly
      busyMs: 0,             // ChatGPT is still writing an earlier answer this long when the page opens
      controlsMs: 0,         // the composer's buttons appear this long after the composer, as on a new chat's page
      turns: 2,
      ...options
    };
    this.sent = [];
    this.stopClicks = 0;
    this.finished = false;
    this.generating = false;
    this.turnCount = 0;
    this.build();
  }

  element(tag, attributes = {}, html = "") {
    const element = this.document.createElement(tag);
    for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, value);
    element.innerHTML = html;
    return element;
  }

  wait(milliseconds) {
    return new Promise(resolve => this.window.setTimeout(resolve, milliseconds));
  }

  build() {
    this.document.body.innerHTML = `
      <main data-app-shell-main-surface="browser">
        <div data-chatgpt-conversation-selection-target="true" data-thread-find-target="conversation"></div>
        <form data-chatgpt-composer data-thread-find-composer="true">
          <div contenteditable="true" role="textbox" class="ProseMirror" aria-label="Спросить ChatGPT" data-virtualkeyboard="true"></div>
          <button type="button" aria-label="Добавить файлы и другое">+</button>
          <button type="button" class="picker" aria-label="Выбрать модель ChatGPT" aria-haspopup="menu" aria-expanded="false"
            data-state="closed"><span aria-hidden="true" style="position: absolute; visibility: hidden">Уровень усилий рассуждения</span><span class="label"></span></button>
          <button type="button" aria-label="Диктовать">mic</button>
          <span class="primary"></span>
        </form>
      </main>`;
    this.thread = this.document.querySelector("[data-thread-find-target]");
    this.form = this.document.querySelector("form");
    this.composer = this.form.querySelector("[contenteditable]");
    this.picker = this.form.querySelector(".picker");
    this.mode = this.options.mode;
    for (let turn = 0; turn < this.options.turns; turn++) {
      const prompt = this.addTurn(`Старый вопрос ${turn + 1}`);
      const answer = this.addAnswer(prompt);
      answer.root.innerHTML = `<p>Старый ответ ${turn + 1}.</p>`;
      this.complete(prompt, answer);
    }
    this.setPrimary("voice");
    this.composer.addEventListener("input", () => {
      if (!this.generating) this.setPrimary(this.composer.innerText.trim() ? "send" : "voice");
    });
    this.picker.addEventListener("pointerdown", event => {
      event.preventDefault();
      if (this.menu) this.closeMenu();
      else this.openMenu();
    });
    this.document.addEventListener("keydown", event => {
      if (event.key === "Escape") this.closeMenu();
    });
    if (this.options.busyMs) void this.answer("Вопрос из самого чата", this.options.busyMs);
    if (this.options.controlsMs) {
      const controls = [...this.form.querySelectorAll(":scope > button")];
      for (const control of controls) control.remove();
      this.window.setTimeout(() => this.composer.after(...controls), this.options.controlsMs);
    }
  }

  get mode() {
    return this.picker.querySelector(".label").textContent;
  }

  set mode(label) {
    this.picker.querySelector(".label").textContent = label;
  }

  /** The effort menu: a view toggle, the slider's announcement, the slider, and the model list of the other view. */
  openMenu() {
    const levels = ["Instant", "Средний", "Высокий"];
    const wrapper = this.element("div", { "data-radix-popper-content-wrapper": "" });
    const menu = this.element("div", { role: "menu", id: "radix-effort", "data-state": "open", "data-radix-menu-content": "" }, `
      <div role="menuitem" aria-label="Выбрать модель" data-model-picker-view-toggle="true"><span class="effort"></span></div>
      <span role="status" aria-live="polite"></span>
      <span>Используйте клавиши со стрелками влево и вправо</span>
      <div role="menuitem" aria-label="Мощность" data-reasoning-slider="true" aria-keyshortcuts="ArrowLeft ArrowRight" tabindex="-1">
        <span role="slider" aria-valuemin="0" aria-valuemax="2" aria-hidden="true"></span>
      </div>
      <div aria-hidden="true" inert>
        <div role="menuitemradio" aria-checked="true">GPT-5.6 Sol</div>
        <div role="menuitemradio" aria-checked="false">GPT-5.5</div>
      </div>`);
    const show = level => {
      menu.querySelector(".effort").textContent = levels[level];
      menu.querySelector('[role="status"]').textContent = `${levels[level]}, ${level + 1} из ${levels.length}.`;
      menu.querySelector('[role="slider"]').setAttribute("aria-valuenow", String(level));
      this.mode = levels[level];
    };
    let level = Math.max(0, levels.indexOf(this.mode));
    show(level);
    menu.querySelector("[data-reasoning-slider]").addEventListener("keydown", event => {
      const next = level + (event.key === "ArrowRight" ? 1 : event.key === "ArrowLeft" ? -1 : 0);
      if (next === level || next < 0 || next >= levels.length) return;
      level = next;
      show(level);
    });
    wrapper.append(menu);
    this.document.body.append(wrapper);
    this.menu = wrapper;
    this.picker.setAttribute("aria-expanded", "true");
    this.picker.setAttribute("aria-controls", "radix-effort");
  }

  closeMenu() {
    this.menu?.remove();
    this.menu = null;
    this.picker.setAttribute("aria-expanded", "false");
    this.picker.removeAttribute("aria-controls");
  }

  setPrimary(kind) {
    const buttons = {
      voice: '<button type="button" aria-label="Начать голосовой разговор">voice</button>',
      send: '<button type="button" data-testid="send-button" aria-label="Отправить подсказку">send</button>',
      stop: '<button type="button" aria-label="Остановить">stop</button>'
    };
    const slot = this.form.querySelector(".primary");
    slot.innerHTML = buttons[kind];
    if (kind === "send") slot.firstElementChild.addEventListener("click", () => this.takePrompt());
    if (kind === "stop") {
      slot.firstElementChild.addEventListener("click", () => {
        this.stopClicks++;
        this.stopped = true;
      });
    }
  }

  /** A prompt's turn: the turn holds the prompt with its own action buttons, and later its answer. */
  addTurn(text) {
    const number = ++this.turnCount;
    const id = crypto.randomUUID();
    const turn = this.element("div", { "data-turn-key": id }, `
      <div data-content-search-turn-key="fallback-turn-${number}"><div class="contents"><div class="group">
        <div class="blocks"><div class="block">
          <h4 class="sr-only">Вы сказали:</h4>
          <div data-chatgpt-search-unit-key="fallback-turn-${number}:0:user" data-chatgpt-search-message-ids="${id}">
            <div data-user-message-bubble="true"><div class="whitespace-pre-wrap"></div></div>
            <div class="turn-action-controls"><button type="button" aria-label="Скопировать сообщение">c</button><button type="button" aria-label="Редактировать сообщение">e</button></div>
          </div>
        </div></div>
      </div></div></div>`);
    turn.querySelector(".whitespace-pre-wrap").textContent = text;
    this.thread.append(turn);
    return { turn, number };
  }

  addAnswer({ turn, number }) {
    const id = crypto.randomUUID();
    const block = this.element("div", { class: "block" }, `
      <span hidden data-chatgpt-agent-turn-start></span>
      <div data-chatgpt-search-unit-key="fallback-turn-${number}:1:assistant" data-chatgpt-search-message-ids="${id} ${id}">
        <h4 class="sr-only" data-conversation-role="assistant">ChatGPT сказал:</h4>
        <div data-chatgpt-selection-conversation-id="chat-1">
          <div data-markdown-text-style="assistant-message" class="MarkdownRoot-rZKhxa" data-markdown-animated></div>
        </div>
      </div>`);
    turn.querySelector(".blocks").append(block);
    return { id, root: block.querySelector("[data-markdown-text-style]"), selection: block.querySelector("[data-chatgpt-selection-conversation-id]") };
  }

  /** A finished answer: typed out, with its message id and its action buttons after both units. */
  complete({ turn }, answer) {
    answer.root.removeAttribute("data-markdown-animated");
    answer.selection.setAttribute("data-chatgpt-selection-message-id", answer.id);
    turn.querySelector(".group").append(this.element("div", { class: "turn-action-controls" },
      '<button type="button" aria-label="Копировать">c</button><button type="button" aria-label="Оценить ответ">r</button>'));
  }

  takePrompt() {
    const text = this.composer.innerText;
    this.composer.replaceChildren();
    this.sent.push(text);
    void this.answer(text);
  }

  /** Answers a prompt: "ChatGPT отвечает" while it thinks, then the answer typed out word by word. */
  async answer(prompt, busyMs = 0) {
    this.generating = true;
    this.stopped = false;
    this.setPrimary("stop");
    const turn = this.addTurn(prompt);
    const status = this.element("div", {}, '<span aria-busy="true" role="status"><span class="sr-only">ChatGPT отвечает</span></span>');
    turn.turn.firstElementChild.append(status);
    await this.wait(busyMs || this.options.thinkingMs);
    status.remove();
    const answer = this.addAnswer(turn);
    let pause = this.options.tokenMs;
    for (const [index, block] of ANSWER_BLOCKS.entries()) {
      if (this.stopped) break;
      answer.root.insertAdjacentHTML("beforeend", block.html);
      const part = answer.root.lastElementChild;
      for (const [position, word] of (block.words ?? []).entries()) {
        if (this.stopped) break;
        part.append(this.document.createTextNode(position ? ` ${word}` : word));
        await this.wait(pause);
      }
      if (index === 0 && this.options.typedAfterStop) {
        this.setPrimary("voice");
        pause = 2500;
      }
      await this.wait(pause);
    }
    this.complete(turn, answer);
    this.generating = false;
    this.finished = !busyMs;
    this.setPrimary(this.composer.innerText.trim() ? "send" : "voice");
  }
}
