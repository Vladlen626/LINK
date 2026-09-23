const BRIDGE = "http://127.0.0.1:43127/link";
const HEADER = { "X-Link-Bridge": "1" };
const conversations = new Map();
const pollingTabs = new Set();
let loopStarted = false;

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "link-announce" && sender.tab?.id != null) {
    const conversation = { ...message.conversation, tabId: sender.tab.id };
    conversations.set(sender.tab.id, conversation);
    void announce(conversation);
    void pollConversation(conversation);
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
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.url) conversations.delete(tabId);
});
chrome.runtime.onStartup.addListener(startLoop);
chrome.runtime.onInstalled.addListener(startLoop);

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
