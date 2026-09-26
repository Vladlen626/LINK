// LINK's end of its embedded ChatGPT page, for the companion (content.js): LINK's messages to the page, the page's
// messages and storage calls to LINK, over WebView2's message channel. LINK puts this function and content.js into one
// script that every new document of the page runs before the page's own scripts.
//
// The page's scripts share the page with the companion, so the channel is guarded: every message to LINK carries the
// session's token, which only this closure holds, and the functions it is posted with are taken before any page script
// can replace them. LINK drops a message without the token.
function createLinkHostApi(config) {
  const webview = globalThis.chrome?.webview;
  if (!webview) return null;
  const postToLink = webview.postMessage.bind(webview);
  const listen = webview.addEventListener.bind(webview);
  const stringify = JSON.stringify;
  const later = setTimeout;
  const cancel = clearTimeout;
  // LINK answers at once; a call it leaves unanswered this long fails.
  const REPLY_TIMEOUT_MS = 10000;
  const calls = new Map();
  const listeners = [];
  let nextId = 1;

  /** Posts to LINK. The message has no prototype, so a toJSON the page puts on Object.prototype never sees the token. */
  function send(fields) {
    postToLink(stringify({ __proto__: null, ...fields, token: config.token }));
  }

  /** A call LINK answers: a message or storage. */
  function call(kind, fields) {
    const id = nextId++;
    return new Promise((resolve, reject) => {
      const timer = later(() => {
        calls.delete(id);
        reject(new Error("LINK did not answer."));
      }, REPLY_TIMEOUT_MS);
      calls.set(id, { resolve, timer });
      send({ ...fields, kind, id });
    });
  }

  listen("message", event => {
    const data = event.data;
    if (data?.kind === "reply") {
      const waiting = calls.get(data.replyTo);
      if (!waiting) return;
      calls.delete(data.replyTo);
      cancel(waiting.timer);
      waiting.resolve(data.response);
    } else if (data?.kind === "message") {
      // Every listener gets LINK's message; the first answer counts.
      let answered = false;
      const respond = response => {
        if (answered) return;
        answered = true;
        send({ kind: "reply", replyTo: data.id, response: response ?? null });
      };
      for (const listener of [...listeners]) listener(data.message, respond);
    }
  });

  return {
    version: config.version,
    /** Listens to LINK's messages: the listener gets each one and a function to answer it with. */
    onMessage: listener => {
      listeners.push(listener);
    },
    sendMessage: message => call("send", { message }),
    storage: {
      get: keys => call("storage", { op: "get", keys: keys ?? null }).then(items => items ?? {}),
      set: items => call("storage", { op: "set", items }).then(() => {}),
      remove: keys => call("storage", { op: "remove", keys }).then(() => {})
    }
  };
}
