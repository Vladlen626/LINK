const BRIDGE = "http://127.0.0.1:43127/link";
const HEADER = { "X-Link-Bridge": "1" };
const CHAT_URLS = ["https://chatgpt.com/*", "https://chat.openai.com/*"];
const IDLE_POLL_MS = 750;
const ACTIVE_POLL_MS = 250;
const ANNOUNCE_INTERVAL_MS = 5000;
const conversations = new Map();
const pollingTabs = new Set();
// Tabs that are working on a LINK request and need ticks while Chrome throttles their timers.
const activeTabs = new Set();
let loopStarted = false;

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "link-announce" && sender.tab?.id != null) {
    registerConversation(sender.tab.id, message.conversation);
    startLoop();
    sendResponse({ ok: true });
    return;
  }

  if (message?.type === "link-event") {
    const tabId = sender.tab?.id;
    if (tabId != null && (message.event?.state === "completed" || message.event?.state === "error")) {
      activeTabs.delete(tabId);
    }
    void postJson("/event", message.event).then(
      () => sendResponse({ ok: true }),
      error => {
        if (error.status === 404 && tabId != null) activeTabs.delete(tabId);
        sendResponse({ ok: false, status: error.status, error: error.message });
      });
    return true;
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  conversations.delete(tabId);
  activeTabs.delete(tabId);
});
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.url) {
    conversations.delete(tabId);
    activeTabs.delete(tabId);
  }
  if (changeInfo.status === "complete" && isChatUrl(tab.url)) void discoverTab(tab, false);
});
chrome.runtime.onStartup.addListener(() => {
  startLoop();
  void discoverTabs(true);
});
chrome.runtime.onInstalled.addListener(() => {
  startLoop();
  void discoverTabs(true);
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

async function discoverTabs(injectMissing) {
  const tabs = await chrome.tabs.query({ url: CHAT_URLS });
  await Promise.allSettled(tabs.map(tab => discoverTab(tab, injectMissing)));
}

async function discoverTab(tab, injectMissing) {
  if (tab.id == null || !isChatUrl(tab.url)) return;
  let result;
  try {
    result = await chrome.tabs.sendMessage(tab.id, { type: "link-discover" });
  } catch {
    if (!injectMissing) return;
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ["content.js"] });
    await delay(100);
    result = await chrome.tabs.sendMessage(tab.id, { type: "link-discover" });
  }
  registerConversation(tab.id, result?.conversation);
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

    const request = await response.json();
    try {
      // A read answers at once; only a prompt keeps the tab waiting on timers for a while.
      if (request.kind !== "read") activeTabs.add(conversation.tabId);
      // Memory Saver must not discard the tab LINK talks to.
      void chrome.tabs.update(conversation.tabId, { autoDiscardable: false }).catch(() => {});
      await chrome.tabs.sendMessage(conversation.tabId, { type: "link-request", request });
    } catch (error) {
      activeTabs.delete(conversation.tabId);
      await reportError(request.requestId, `The ChatGPT tab could not receive the LINK request: ${error.message}`);
    }
  } catch {
    // A stopped desktop bridge is normal while LINK is closed.
  } finally {
    pollingTabs.delete(conversation.tabId);
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
    throw Object.assign(new Error(`LINK bridge returned HTTP ${response.status}.`), { status: response.status });
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
void discoverTabs(true);
