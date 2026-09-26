// The companion in LINK's embedded ChatGPT page: content.js on the stand-in API from embedded-host.js, talking to
// LINK over WebView2's message channel instead of an extension.

/**
 * WebView2's chrome.webview in the embedded page, with LINK's end played by the check: a message that carries the
 * session's token is answered from the fake bridge and storage, as LINK's host answers it.
 */
function fakeWebview(bridge, storage, token) {
  const listeners = [];
  const waiting = new Map();
  let nextId = 1;

  function deliver(data) {
    // WebView2 hands each of LINK's messages to the page as a task of its own.
    setTimeout(() => {
      for (const listener of [...listeners]) listener({ data: JSON.parse(JSON.stringify(data)) });
    }, 0);
  }

  async function answer(message) {
    if (message.kind === "send") {
      return message.message.type === "link-event" ? bridge.event(message.message.event) : { ok: true };
    }
    if (message.op === "get") return storage.get(message.keys);
    if (message.op === "set") await storage.set(message.items);
    if (message.op === "remove") await storage.remove(message.keys);
    return null;
  }

  const webview = {
    posted: [],
    postMessage(text) {
      const message = JSON.parse(text);
      webview.posted.push(message);
      if (message.token !== token) return;
      if (message.kind === "reply") {
        waiting.get(message.replyTo)?.(message.response);
        return;
      }
      void answer(message).then(response => deliver({ kind: "reply", replyTo: message.id, response }));
    },
    addEventListener(type, listener) {
      if (type === "message") listeners.push(listener);
    },
    /** LINK hands the page's companion a message; its answer. */
    ask(message) {
      const id = nextId++;
      return new Promise(resolve => {
        waiting.set(id, resolve);
        deliver({ kind: "message", id, message });
      });
    }
  };
  return webview;
}

check("embedded: the companion works over LINK's page channel, and the page never sees the token", async () => {
  const page = await openChat({ ui: "shell", mode: "Высокий", tokenMs: 20 });
  const bridge = fakeBridge();
  const storage = memoryStorage();
  const webview = fakeWebview(bridge, storage, "check-token");
  const win = page.frame.contentWindow;
  // As LINK composes the page's script: the stand-in API is made before the page's scripts run, content.js takes it.
  await loadScript(page.frame, { webview }, `${COMPANION}embedded-host.js`);
  win.linkHostApi = win.createLinkHostApi({ token: "check-token", version: VERSION });
  // A page script that runs later and looks at everything the companion turns into JSON.
  let looked = 0;
  let leaked = false;
  Object.defineProperty(win.Object.prototype, "toJSON", {
    configurable: true,
    value() {
      looked++;
      if (Object.hasOwn(this, "token")) leaked = true;
      return this;
    }
  });
  await loadScript(page.frame, { webview }, `${COMPANION}content.js`);

  const announced = await until("the companion announces its page",
    () => webview.posted.find(message => message.message?.type === "link-announce"));
  expect("with its conversation, signed in",
    [announced?.message.conversation?.conversationId, announced?.message.signedIn], [page.conversationId, true]);

  const request = promptRequest(page.conversationId, "Вопрос", { effort: "fast" });
  expect("the page takes LINK's request", await webview.ask({ type: "link-request", request }), { accepted: true, version: VERSION });
  const completed = await completedOf(bridge, request);
  expect("the answer comes back over the channel", completed?.text, ANSWER_MARKDOWN);
  expect("Fast switched the chat's mode", page.chat.mode, "Instant");
  expect("the mode LINK set is kept in LINK's storage", storage.data.get(`effort:${page.conversationId}`), "fast");
  await until("where the prompt stood is forgotten once it is answered",
    () => ![...storage.data.keys()].some(key => key.startsWith("request:")));
  expect("every message to LINK carries the token", webview.posted.every(message => message.token === "check-token"), true);
  expect("the page's toJSON saw the companion's messages", looked > 0, true);
  expect("but never the token", leaked, false);
  delete win.Object.prototype.toJSON;
});

check("embedded: a page offering to log in is announced as signed out", async () => {
  const page = await openChat({ ui: "octane" });
  const login = page.frame.contentDocument.createElement("button");
  login.setAttribute("data-login-button", "");
  login.textContent = "Войти";
  page.frame.contentDocument.body.append(login);
  const webview = fakeWebview(fakeBridge(), memoryStorage(), "check-token");
  const win = page.frame.contentWindow;
  await loadScript(page.frame, { webview }, `${COMPANION}embedded-host.js`);
  win.linkHostApi = win.createLinkHostApi({ token: "check-token", version: VERSION });
  await loadScript(page.frame, { webview }, `${COMPANION}content.js`);
  const announced = await until("the companion announces its page",
    () => webview.posted.find(message => message.message?.type === "link-announce"));
  expect("not signed in", announced?.message.signedIn, false);
});
