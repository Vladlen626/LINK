const BRIDGE = "http://127.0.0.1:43127/link";
const HEADER = { "X-Link-Bridge": "1" };
const CHAT_URLS = ["https://chatgpt.com/*", "https://chat.openai.com/*"];
const conversations = new Map();
const pollingTabs = new Set();
let loopStarted = false;

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "link-announce" && sender.tab?.id != null) {
    registerConversation(sender.tab.id, message.conversation);
    startLoop();
    sendResponse({ ok: true });
    return;
  }

  if (message?.type === "link-event") {
    void postJson("/event", message.event).then(
      () => sendResponse({ ok: true }),
      error => sendResponse({ ok: false, error: error.message }));
    return true;
  }
});

chrome.tabs.onRemoved.addListener((tabId) => conversations.delete(tabId));
chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.url) conversations.delete(tabId);
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
  const conversation = { ...details, tabId };
  conversations.set(tabId, conversation);
  void announce(conversation);
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
    await Promise.allSettled([...conversations.values()].map(async conversation => {
      await announce(conversation);
      await pollConversation(conversation);
    }));
    await delay(750);
  }
}

async function announce(conversation) {
  try {
    await postJson("/announce", {
      conversationId: conversation.conversationId,
      title: conversation.title,
      url: conversation.url
    });
  } catch {
    // LINK may not be running yet. The next content-script heartbeat retries.
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
      await chrome.tabs.sendMessage(conversation.tabId, { type: "link-request", request });
    } catch (error) {
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
  if (!response.ok) throw new Error(`LINK bridge returned HTTP ${response.status}.`);
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
