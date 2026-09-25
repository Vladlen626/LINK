// The service worker over fake ChatGPT tabs: how it keeps a working companion in each tab and delivers requests.

function chatTab(id, extra = {}) {
  return { id, url: `https://chatgpt.com/c/chat-${id}`, conversationId: `chat-${id}`, companion: "live", discarded: false, ...extra };
}

check("an extension reload starts one companion per tab, even when asked twice at once", async () => {
  const browser = fakeBrowser([chatTab(1, { companion: "none" }), chatTab(2)]);
  const bridge = fakeHttpBridge();
  await startWorker(browser, bridge);
  // Chrome runs onInstalled right after the worker's own start.
  for (const listener of browser.listeners.installed) listener({ reason: "update" });
  await until("both tabs are announced", () => bridge.announced.has("chat-1") && bridge.announced.has("chat-2"));
  expect("the tab without a reachable copy gets one, once", browser.log.injections, [1]);
});

check("a request goes to a tab whose companion died after the companion is started again", async () => {
  const tabs = [chatTab(3)];
  const browser = fakeBrowser(tabs);
  const bridge = fakeHttpBridge();
  await startWorker(browser, bridge);
  await until("the tab is announced", () => bridge.announced.has("chat-3"));
  tabs[0].companion = "none";
  bridge.offer(promptRequest("chat-3", "Вопрос"));
  const delivered = await until("the request reaches the tab", () => browser.log.delivered[0]);
  expect("it is the prompt", delivered?.request.prompt, "Вопрос");
  expect("LINK saw the reconnection", bridge.events.map(event => event.state), ["reconnecting"]);
  expect("the companion was started once", browser.log.injections, [3]);
  expect("the tab was not reloaded", browser.log.reloads, []);
});

check("a discarded tab is reloaded for a prompt, but never twice within a minute", async () => {
  const tabs = [chatTab(4)];
  const browser = fakeBrowser(tabs);
  const bridge = fakeHttpBridge();
  await startWorker(browser, bridge);
  await until("the tab is announced", () => bridge.announced.has("chat-4"));
  tabs[0].discarded = true;
  bridge.offer(promptRequest("chat-4", "Первый"));
  await until("the reloaded tab gets the request", () => browser.log.delivered.length === 1, 20000);
  expect("reloaded once", browser.log.reloads, [4]);

  tabs[0].discarded = true;
  bridge.offer(promptRequest("chat-4", "Второй"));
  const error = await until("an error is reported", () => bridge.events.find(event => event.state === "error"), 20000);
  expect("no reload loop", browser.log.reloads, [4]);
  expect("the error says the tab is out of reach", error?.error, "The ChatGPT tab could not be reached. Reload it and try again.");
  expect("the second prompt was not delivered", browser.log.delivered.length, 1);
});

check("a live page that has the prompt is never reloaded", async () => {
  const tabs = [chatTab(5, { injectable: false })];
  const browser = fakeBrowser(tabs);
  const bridge = fakeHttpBridge();
  await startWorker(browser, bridge);
  await until("the tab is announced", () => bridge.announced.has("chat-5"));
  tabs[0].companion = "none";
  bridge.offer(promptRequest("chat-5", "Вопрос", { attempt: 2, sent: true }));
  await until("an error is reported", () => bridge.events.find(event => event.state === "error"), 20000);
  expect("the tab was not reloaded", browser.log.reloads, []);
});

check("a page too busy to answer is not reloaded", async () => {
  const tabs = [chatTab(7)];
  const browser = fakeBrowser(tabs);
  const bridge = fakeHttpBridge();
  await startWorker(browser, bridge);
  await until("the tab is announced", () => bridge.announced.has("chat-7"));
  tabs[0].companion = "busy";
  bridge.offer(promptRequest("chat-7", "Вопрос"));
  await until("an error is reported", () => bridge.events.find(event => event.state === "error"), 30000);
  expect("the tab was not reloaded", browser.log.reloads, []);
});

check("a companion of another version steps aside for the current one", async () => {
  const browser = fakeBrowser([chatTab(6, { companion: "old" })]);
  const bridge = fakeHttpBridge();
  await startWorker(browser, bridge);
  await until("the tab is announced", () => bridge.announced.has("chat-6"));
  expect("the old copy was retired", browser.log.retired, [6]);
  expect("the current one was started", browser.log.injections, [6]);
});
