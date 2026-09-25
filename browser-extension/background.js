const BRIDGE = "http://127.0.0.1:43127/link";
const HEADER = { "X-Link-Bridge": "1" };
const VERSION = chrome.runtime.getManifest().version;
const CHAT_URLS = ["https://chatgpt.com/*", "https://chat.openai.com/*"];
const IDLE_POLL_MS = 750;
const ACTIVE_POLL_MS = 250;
const ANNOUNCE_INTERVAL_MS = 5000;
// Every ChatGPT tab is checked this often for a working companion, e.g. one whose page script died.
const TAB_CHECK_INTERVAL_MS = 15000;
// A long chat can keep its page busy for a few seconds; a companion that answers later is still alive.
const HELLO_TIMEOUT_MS = 5000;
// A tab that cannot be reached is reloaded at most once in this period, so a broken page never reloads in a loop.
const RELOAD_INTERVAL_MS = 60000;
const RELOAD_TIMEOUT_MS = 30000;
// The unpacked companion checks this often whether its files on disk are a newer version, and then reloads itself.
const UPDATE_CHECK_INTERVAL_MS = 10000;
const conversations = new Map();
const pollingTabs = new Set();
// Tabs that are working on a LINK request and need ticks while Chrome throttles their timers.
const activeTabs = new Set();
// One repair per tab at a time: two injections side by side would leave two copies answering every request.
const repairs = new Map();
const reloadedAt = new Map();
let loopStarted = false;
let tabsCheck = null;
let tabsCheckedAt = 0;
let updateCheckedAt = 0;

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const tabId = sender.tab?.id;
  if (message?.type === "link-announce" && tabId != null) {
    registerConversation(tabId, message.conversation);
    // After a restart of this worker the tab's announcement brings back the ticks its prompt needs.
    if (message.busy) activeTabs.add(tabId);
    startLoop();
    sendResponse({ ok: true });
    return;
  }

  if (message?.type === "link-event") {
    const state = message.event?.state;
    if (tabId != null && state !== "snapshot") {
      if (state === "completed" || state === "error") activeTabs.delete(tabId);
      else activeTabs.add(tabId);
    }
    void postJson("/event", message.event).then(
      () => sendResponse({ ok: true }),
      error => {
        if (error.status === 404 && tabId != null) activeTabs.delete(tabId);
        sendResponse({ ok: false, status: error.status, reason: error.reason, error: error.message });
      });
    return true;
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  conversations.delete(tabId);
  activeTabs.delete(tabId);
  reloadedAt.delete(tabId);
});
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.url) conversations.delete(tabId);
  if (changeInfo.status === "complete" && isChatUrl(tab.url)) void ensureCompanion(tabId);
});
chrome.runtime.onStartup.addListener(() => {
  startLoop();
  void checkTabs();
});
chrome.runtime.onInstalled.addListener(() => {
  startLoop();
  void checkTabs();
});

function registerConversation(tabId, details) {
  if (!details?.conversationId) return;
  const previous = conversations.get(tabId);
  const sameConversation = previous?.conversationId === details.conversationId;
  const conversation = { ...details, tabId, announcedAt: sameConversation ? previous.announcedAt : 0 };
  conversations.set(tabId, conversation);
  if (!sameConversation) void announce(conversation);
  void pollConversation(conversation);
}

/** Makes sure every ChatGPT tab runs the current companion, e.g. right after the extension was reloaded. */
function checkTabs() {
  tabsCheck ??= (async () => {
    try {
      const tabs = await chrome.tabs.query({ url: CHAT_URLS });
      // A discarded tab runs nothing; it is woken up only for a request.
      await Promise.allSettled(tabs.filter(tab => tab.id != null && !tab.discarded).map(tab => ensureCompanion(tab.id)));
    } finally {
      tabsCheckedAt = Date.now();
      tabsCheck = null;
    }
  })();
  return tabsCheck;
}

/**
 * The tab's companion, started when it is missing: a page opened before the extension was reloaded keeps only
 * copies that can no longer reach it. A copy of another version steps aside first. `reply` is the working companion's
 * handshake; `unreachable` says the page cannot run one at all (discarded, crashed, an error page).
 */
function ensureCompanion(tabId) {
  let repair = repairs.get(tabId);
  if (!repair) {
    repair = repairTab(tabId).finally(() => repairs.delete(tabId));
    repairs.set(tabId, repair);
  }
  return repair;
}

async function repairTab(tabId) {
  const reply = await hello(tabId);
  if (reply?.version === VERSION) return { reply: trackCompanion(tabId, reply) };
  if (reply) await chrome.tabs.sendMessage(tabId, { type: "link-retire" }).catch(() => {});
  try {
    await chrome.scripting.executeScript({ target: { tabId }, files: ["content.js"] });
  } catch (error) {
    console.warn(`LINK companion could not start in tab ${tabId}: ${error.message}`);
    return { unreachable: true };
  }
  const started = await hello(tabId);
  return started?.version === VERSION ? { reply: trackCompanion(tabId, started) } : {};
}

/** The version handshake: which companion runs in the tab, if any answers in time. */
async function hello(tabId) {
  let timer;
  try {
    const reply = await Promise.race([
      chrome.tabs.sendMessage(tabId, { type: "link-hello" }),
      new Promise(resolve => {
        timer = setTimeout(resolve, HELLO_TIMEOUT_MS, null);
      })
    ]);
    return reply?.version ? reply : null;
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

/** Registers the tab's conversation from its companion's handshake, and the ticks a prompt there needs. */
function trackCompanion(tabId, reply) {
  registerConversation(tabId, reply.conversation);
  if (reply.busy) activeTabs.add(tabId);
  return reply;
}

/** Reloads a tab LINK cannot reach otherwise and waits for its page's companion; never twice within a minute. */
async function reloadTab(tabId) {
  if (Date.now() - (reloadedAt.get(tabId) ?? 0) < RELOAD_INTERVAL_MS) return null;
  reloadedAt.set(tabId, Date.now());
  try {
    await chrome.tabs.reload(tabId);
  } catch {
    return null;
  }
  const deadline = Date.now() + RELOAD_TIMEOUT_MS;
  while (Date.now() < deadline) {
    await delay(500);
    const reply = await hello(tabId);
    if (reply?.version === VERSION && reply.conversation) return trackCompanion(tabId, reply);
  }
  return null;
}

/**
 * Whether a tab may be reloaded for a prompt: a discarded tab runs nothing, and a prompt that has not reached the
 * chat loses nothing. A live page that took the prompt is never reloaded: ChatGPT may still be answering there.
 */
async function mayReload(tabId, request) {
  try {
    const tab = await chrome.tabs.get(tabId);
    return tab.discarded || !request.sent;
  } catch {
    return false;
  }
}

/**
 * Reloads the extension when its manifest on disk names another version: an unpacked companion updated in place then
 * takes effect without Reload in chrome://extensions. The new worker starts the new content script in the open tabs,
 * and a prompt on its way is picked up again from LINK.
 */
async function reloadIfUpdated() {
  try {
    const response = await fetch(chrome.runtime.getURL("manifest.json"), { cache: "no-store" });
    const { version } = await response.json();
    if (typeof version === "string" && version !== VERSION) chrome.runtime.reload();
  } catch {
    // A manifest being written just now is read again at the next check.
  }
}

function isChatUrl(url) {
  return typeof url === "string" &&
    (url.startsWith("https://chatgpt.com/") || url.startsWith("https://chat.openai.com/"));
}

function startLoop() {
  if (loopStarted) return;
  loopStarted = true;
  void pollLoop();
}

async function pollLoop() {
  while (true) {
    if (conversations.size > 0) {
      // Extension API calls reset the service worker idle timer. Without this the worker dies
      // whenever Chrome throttles the ChatGPT tab's own heartbeat, and LINK loses the conversation.
      await chrome.runtime.getPlatformInfo().catch(() => {});
    }

    for (const tabId of activeTabs) {
      void chrome.tabs.sendMessage(tabId, { type: "link-tick" }).catch(() => activeTabs.delete(tabId));
    }

    if (Date.now() - tabsCheckedAt >= TAB_CHECK_INTERVAL_MS) void checkTabs();
    if (Date.now() - updateCheckedAt >= UPDATE_CHECK_INTERVAL_MS) {
      updateCheckedAt = Date.now();
      void reloadIfUpdated();
    }
    await Promise.allSettled([...conversations.values()].map(async conversation => {
      if (Date.now() - conversation.announcedAt >= ANNOUNCE_INTERVAL_MS) await announce(conversation);
      await pollConversation(conversation);
    }));
    await delay(activeTabs.size > 0 ? ACTIVE_POLL_MS : IDLE_POLL_MS);
  }
}

async function announce(conversation) {
  try {
    await postJson("/announce", {
      conversationId: conversation.conversationId,
      title: conversation.title,
      url: conversation.url
    });
    conversation.announcedAt = Date.now();
  } catch {
    // LINK may not be running yet. The next loop iteration retries.
  }
}

async function pollConversation(conversation) {
  if (pollingTabs.has(conversation.tabId)) return;
  pollingTabs.add(conversation.tabId);
  try {
    const response = await fetch(
      `${BRIDGE}/request?conversationId=${encodeURIComponent(conversation.conversationId)}`,
      { headers: HEADER, cache: "no-store" });
    if (response.status === 204) return;
    if (!response.ok) throw new Error(`LINK bridge returned HTTP ${response.status}.`);
    await deliver(conversation.tabId, await response.json());
  } catch {
    // A stopped desktop bridge is normal while LINK is closed.
  } finally {
    pollingTabs.delete(conversation.tabId);
  }
}

/**
 * Hands a request to the tab's companion, restoring the companion first when the tab has none that answers: after an
 * extension reload the page's old copy can no longer be reached. LINK offers a request again while it hears nothing
 * about it, and the companion never sends a prompt twice, so a request lost on the way is simply delivered again.
 */
async function deliver(tabId, request) {
  const prompt = request.kind !== "read";
  if (prompt) {
    activeTabs.add(tabId);
    // Memory Saver must not discard the tab LINK talks to.
    void chrome.tabs.update(tabId, { autoDiscardable: false }).catch(() => {});
  }

  let companion = await hello(tabId);
  if (companion?.version !== VERSION) {
    if (prompt) await postJson("/event", { requestId: request.requestId, state: "reconnecting" }).catch(() => {});
    const repair = await ensureCompanion(tabId);
    companion = repair.reply ?? null;
    // Only a page that cannot run the companion is reloaded; one that is merely busy is left alone.
    if (!companion && repair.unreachable && prompt && await mayReload(tabId, request)) companion = await reloadTab(tabId);
  }
  if (!companion) {
    activeTabs.delete(tabId);
    await reportError(request.requestId, "The ChatGPT tab could not be reached. Reload it and try again.");
    return;
  }

  try {
    const reply = await chrome.tabs.sendMessage(tabId, { type: "link-request", request });
    if (!reply?.accepted) throw new Error("the page did not accept it");
  } catch (error) {
    activeTabs.delete(tabId);
    await reportError(request.requestId, `The ChatGPT tab could not receive the LINK request: ${error.message}`);
  }
}

async function postJson(path, body) {
  const response = await fetch(`${BRIDGE}${path}`, {
    method: "POST",
    headers: { ...HEADER, "Content-Type": "application/json" },
    body: JSON.stringify(body),
    cache: "no-store"
  });
  if (!response.ok) {
    // The bridge says why it dropped a request: the companion stops ChatGPT only when the user cancelled.
    const details = await response.json().catch(() => ({}));
    throw Object.assign(new Error(`LINK bridge returned HTTP ${response.status}.`),
      { status: response.status, reason: details.reason });
  }
  return response;
}

async function reportError(requestId, error) {
  try {
    await postJson("/event", { requestId, state: "error", error });
  } catch {
    // The desktop app may have closed after dispatching the request.
  }
}

function delay(milliseconds) {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

startLoop();
void checkTabs();
