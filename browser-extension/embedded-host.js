// LINK's embedded ChatGPT page runs this companion without the extension. This is the part of the extension API that
// content.js uses, carried to LINK over WebView2's message channel. LINK puts this function and content.js into one
// script that every new document of the page runs before the page's own scripts; content.js takes this API in place
// of `chrome`.
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
  // LINK answers at once; a call it leaves unanswered this long fails, as a call to a gone extension does.
  const REPLY_TIMEOUT_MS = 10000;
  const SENDER = { id: "link-embedded-page" };
  const calls = new Map();
  const listeners = [];
  let nextId = 1;

  /** Posts to LINK. The message has no prototype, so a toJSON the page puts on Object.prototype never sees the token. */
  function send(fields) {
    postToLink(stringify({ __proto__: null, ...fields, token: config.token }));
  }

  /** A call LINK answers: an extension message or storage. */
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
      // As chrome.runtime.onMessage delivers it: every listener gets the message, the first answer counts.
      let answered = false;
      const respond = response => {
        if (answered) return;
        answered = true;
        send({ kind: "reply", replyTo: data.id, response: response ?? null });
      };
      for (const listener of [...listeners]) listener(data.message, SENDER, respond);
    }
  });

  return {
    runtime: {
      id: SENDER.id,
      getManifest: () => ({ version: config.version }),
      onMessage: {
        addListener: listener => {
          listeners.push(listener);
        },
        removeListener: listener => {
          const index = listeners.indexOf(listener);
          if (index >= 0) listeners.splice(index, 1);
        }
      },
      sendMessage: message => call("send", { message })
    },
    storage: {
      local: {
        get: keys => call("storage", { op: "get", keys: keys ?? null }).then(items => items ?? {}),
        set: items => call("storage", { op: "set", items }).then(() => {}),
        remove: keys => call("storage", { op: "remove", keys }).then(() => {})
      }
    }
  };
}
