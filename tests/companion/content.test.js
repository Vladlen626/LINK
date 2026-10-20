// The content script in a mock ChatGPT conversation: how it sends a prompt, watches the answer and ends it.

async function startCompanion(page, bridge = fakeBridge(), storage = memoryStorage()) {
  const api = hostApi(bridge, storage);
  await page.start(api);
  return { page, api, bridge, storage };
}

async function ask(api, request) {
  return sendToPage(api, { type: "link-request", request });
}

function completedOf(bridge, request, timeout = 20000) {
  return until("the answer completes", () => bridge.of(request.requestId, "completed")[0], timeout);
}

// A prompt as LINK formats it: the user's words, the effort hint, the context block.
const LINK_PROMPT = "- Ты работаешь? - Ты работаешь?\n\n[Fast answer: reply briefly and directly — the answer first, " +
  "a few sentences or a short list at most, no preamble or recap, no extended reasoning.]\n\n---\nContext\n" +
  "Application: explorer\n\nWindow: net10.0-windows — проводник";

/** Sends LINK's prompt and waits for its answer, which must be the new one, whole. */
async function expectAnswer(page, what) {
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, LINK_PROMPT);
  await ask(api, request);
  const completed = await completedOf(bridge, request);
  expect(what, completed?.text, ANSWER_MARKDOWN);
  expect("the prompt is sent once", page.chat.sent, [LINK_PROMPT]);
  return { bridge, request, completed };
}

/** Sends a prompt with a response effort and waits for its answer; the mode notices LINK got on the way. */
async function askWithEffort({ page, api, bridge }, effort, prompt = `Вопрос ${effort}`) {
  const request = promptRequest(page.conversationId, prompt, { effort });
  await ask(api, request);
  await completedOf(bridge, request);
  return bridge.of(request.requestId, "notice").map(event => event.notice);
}

check("streams the answer and completes it", async () => {
  const page = await openChat();
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Как дела?");
  expect("the page takes the request", await ask(api, request), { accepted: true, version: VERSION });
  const completed = await completedOf(bridge, request);
  expect("the complete answer comes as Markdown", completed?.text, ANSWER_MARKDOWN);
  expect("a usual end needs no diagnostics", completed?.diagnostics, undefined);
  const states = bridge.of(request.requestId).map(event => event.state).filter(state => state !== "heartbeat");
  expect("the request goes through its states", [...new Set(states)], ["sending", "waiting", "receiving", "completed"]);
  const streamed = bridge.of(request.requestId, "receiving").map(event => event.text);
  expect("the answer streams in steps before it ends", streamed.length >= 3, true);
  expect("each step is the answer so far", streamed.every(text => ANSWER_MARKDOWN.startsWith(text)), true);
  expect("the thinking status is never the answer", streamed.some(text => text.includes("Думаю")), false);
  expect("the prompt is sent once", page.chat.sent, ["Как дела?"]);
});

check("a hidden Stop left in the composer does not hold the answer", async () => {
  const page = await openChat({ actions: false, stopAfter: "hidden" });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  await until("ChatGPT finishes", () => page.chat.finished);
  const finishedAt = Date.now();
  const completed = await completedOf(bridge, request);
  expect("the answer completes", completed?.text, ANSWER_MARKDOWN);
  expect("within seconds of ChatGPT finishing", Date.now() - finishedAt < 3000, true);
});

check("a stuck visible Stop does not hold an answer whose turn has its buttons", async () => {
  const page = await openChat({ stopAfter: "visible" });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  await until("ChatGPT finishes", () => page.chat.finished);
  const finishedAt = Date.now();
  const completed = await completedOf(bridge, request);
  expect("the answer completes", completed?.text, ANSWER_MARKDOWN);
  expect("at once", Date.now() - finishedAt < 1000, true);
  expect("the stuck Stop is not pressed", page.chat.stopClicks, 0);
});

check("an answer without a Markdown root completes on its turn's buttons", async () => {
  const page = await openChat({ markdown: false });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  const completed = await completedOf(bridge, request);
  expect("the whole message is the answer", completed?.text, ANSWER_MARKDOWN);
  expect("nothing unsure streams meanwhile", bridge.of(request.requestId, "receiving").length, 0);
  expect("the unusual end is explained for link.log", JSON.parse(completed?.diagnostics ?? "{}").answer?.markdownRoots, 0);
});

check("an answer without a Markdown root or buttons completes once Stop is gone, never on Думаю", async () => {
  const page = await openChat({ markdown: false, actions: false, placeholderInMessage: true, thinkingMs: 5000 });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  await until("ChatGPT finishes", () => page.chat.finished, 30000);
  expect("the status in the message never ended it", bridge.of(request.requestId, "completed").length, 0);
  const completed = await completedOf(bridge, request);
  expect("the whole message is the answer", completed?.text, ANSWER_MARKDOWN);
  expect("why it ended is kept", JSON.parse(completed?.diagnostics ?? "{}").completedBy, "settled without Markdown");
});

check("a picture is announced while ChatGPT draws it and completes the answer, staying in the page", async () => {
  const page = await openChat({ image: true });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Нарисуй колесо");
  await ask(api, request);
  const completed = await completedOf(bridge, request);
  const announced = bridge.of(request.requestId, "image");
  expect("LINK is told once that a picture is coming, with no picture data", announced.map(event => event.image), [undefined]);
  expect("the answer is a picture without text", [completed?.text, completed?.picture], ["", true]);
  expect("it is a usual end", completed?.diagnostics, undefined);
  expect("the status line never streams as the answer", bridge.of(request.requestId, "receiving").length, 0);
});

check("a done picture whose turn lost the answer's mark still completes the answer", async () => {
  const page = await openChat({ image: true, imageUnmarked: true });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Нарисуй колесо");
  await ask(api, request);
  const completed = await completedOf(bridge, request);
  expect("LINK was told a picture is coming", bridge.of(request.requestId, "image").length, 1);
  expect("the answer is a picture without text", [completed?.text, completed?.picture], ["", true]);
  expect("why it ended is kept", JSON.parse(completed?.diagnostics ?? "{}").completedBy, "picture made");
});

check("an answer that stops changing completes even if Stop never goes away", async () => {
  const page = await openChat({ actions: false, stopAfter: "visible" });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  await until("ChatGPT finishes", () => page.chat.finished);
  const finishedAt = Date.now();
  expect("a quiet answer under Stop is not cut short", await poll(() => bridge.of(request.requestId, "completed")[0], 50000), null);
  const completed = await completedOf(bridge, request, 30000);
  expect("after a minute without change", Date.now() - finishedAt >= 55000, true);
  expect("the answer", completed?.text, ANSWER_MARKDOWN);
  expect("why it ended is kept", JSON.parse(completed?.diagnostics ?? "{}").completedBy, "unchanged for a minute");
});

check("an offer of the same prompt again does not send it twice", async () => {
  const page = await openChat();
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Один раз");
  await ask(api, request);
  await until("the prompt is sent", () => page.chat.sent.length === 1);
  const beats = bridge.of(request.requestId, "heartbeat").length;
  await ask(api, { ...request, attempt: 2 });
  await until("the companion says it is still on it", () => bridge.of(request.requestId, "heartbeat").length > beats, 500);
  await completedOf(bridge, request);
  expect("the prompt is sent once", page.chat.sent, ["Один раз"]);
});

check("after a page reload the prompt's answer is picked up where it is", async () => {
  const bridge = fakeBridge();
  const storage = memoryStorage();
  const first = await openChat({ tokenMs: 400 });
  const firstApi = hostApi(bridge, storage);
  await first.start(firstApi);
  const request = promptRequest(first.conversationId, "Длинный вопрос");
  await ask(firstApi, request);
  await until("the answer streams", () => bridge.of(request.requestId, "receiving").length);
  const kept = await until("the prompt's place is kept", () => storage.data.get(`request:${request.requestId}`)?.promptKey);

  // The page reloads; ChatGPT shows the prompt with its answer, finished meanwhile.
  first.frame.remove();
  const second = await openChat();
  second.chat.addAnsweredTurn("Длинный вопрос", kept);
  const { api } = await startCompanion(second, bridge, storage);
  // Offered again without "sent" (LINK had not heard it): the kept state still says it was.
  await ask(api, { ...request, attempt: 2 });
  const completed = await completedOf(bridge, request);
  expect("the answer is picked up", completed?.text, ANSWER_MARKDOWN);
  expect("nothing is sent from the reloaded page", second.chat.sent, []);
  expect("the kept state is gone with the answer", await poll(() => !storage.data.has(`request:${request.requestId}`), 1000), true);
});

check("a prompt that reached the chat just before its page went is not sent again", async () => {
  const page = await openChat();
  const bridge = fakeBridge();
  const storage = memoryStorage();
  const request = promptRequest(page.conversationId, "Уже отправлено");
  const lastUserKey = [...page.frame.contentDocument.querySelectorAll('[data-message-author-role="user"]')].at(-1)
    .getAttribute("data-message-id");
  // The page before had clicked Send when it was reloaded: ChatGPT answers the prompt.
  await storage.set({ [`request:${request.requestId}`]: { phase: "sending", lastUserKey, savedAt: Date.now() } });
  void page.chat.answer("Уже отправлено");
  const { api } = await startCompanion(page, bridge, storage);
  await ask(api, { ...request, attempt: 2 });
  const completed = await completedOf(bridge, request);
  expect("its answer completes", completed?.text, ANSWER_MARKDOWN);
  expect("the prompt is not sent again", page.chat.sent, []);

  // A prompt that had not reached the chat is sent.
  const next = promptRequest(page.conversationId, "Ещё не отправлено");
  const current = [...page.frame.contentDocument.querySelectorAll('[data-message-author-role="user"]')].at(-1)
    .getAttribute("data-message-id");
  await storage.set({ [`request:${next.requestId}`]: { phase: "sending", lastUserKey: current, savedAt: Date.now() } });
  await ask(api, { ...next, attempt: 2 });
  await completedOf(bridge, next);
  expect("an unsent prompt is sent once", page.chat.sent, ["Ещё не отправлено"]);
});

check("Fast switches the composer's picker from Высокий to Мгновенный", async () => {
  const page = await openChat({ mode: "Высокий" });
  const companion = await startCompanion(page);
  expect("LINK is told the mode", await askWithEffort(companion, "fast", "Быстрый вопрос"), ["ChatGPT mode: Мгновенный"]);
  expect("the picker shows Мгновенный", page.chat.mode, "Мгновенный");
  expect("the header's model picker is left alone", page.chat.headerOpens, 0);
  expect("the menu is closed", page.chat.menu, null);
  expect("the prompt is sent", page.chat.sent, ["Быстрый вопрос"]);
});

check("Fast, Deep and Normal follow the picker; Normal gives the user's mode back", async () => {
  const page = await openChat({ mode: "Средний", tokenMs: 20 });
  const companion = await startCompanion(page);
  const run = async effort => [await askWithEffort(companion, effort), page.chat.mode];
  expect("Fast", await run("fast"), [["ChatGPT mode: Мгновенный"], "Мгновенный"]);
  expect("the user's own mode is kept", companion.storage.data.get(`mode:${page.conversationId}`), "Средний");
  // The user picks High by hand; Fast puts Instant back.
  page.chat.mode = "Высокий";
  expect("Fast again", await run("fast"), [["ChatGPT mode: Мгновенный"], "Мгновенный"]);
  expect("Deep", await run("deep"), [["ChatGPT mode: Высокий"], "Высокий"]);
  expect("Deep again changes nothing", await run("deep"), [[], "Высокий"]);
  expect("Normal", await run("normal"), [["ChatGPT mode: Средний"], "Средний"]);
  expect("Normal again leaves it alone", await run("normal"), [[], "Средний"]);
});

check("a picker that names its kind before the mode is read by the mode", async () => {
  const page = await openChat({ mode: "Высокий", pickerPrefix: "Рассуждение · " });
  const companion = await startCompanion(page);
  expect("LINK is told the mode", await askWithEffort(companion, "fast"), ["ChatGPT mode: Рассуждение · Мгновенный"]);
  expect("the picker shows Мгновенный", page.chat.mode, "Мгновенный");
});

check("the older header picker is used when the composer has none", async () => {
  const page = await openChat({ composerPicker: false, tokenMs: 20 });
  const companion = await startCompanion(page);
  const run = async effort => [await askWithEffort(companion, effort), page.chat.headerMode];
  expect("Fast picks Instant there", await run("fast"), [["ChatGPT mode: Instant"], "Instant"]);
  expect("its label names no mode to keep", companion.storage.data.has(`mode:${page.conversationId}`), false);
  expect("Normal picks Auto", await run("normal"), [["ChatGPT mode: Auto"], "Auto"]);
});

check("a mode the picker does not take is reported, and the prompt still goes", async () => {
  const page = await openChat({ mode: "Высокий", itemsChoose: false });
  const companion = await startCompanion(page);
  expect("the notice says what the picker shows", await askWithEffort(companion, "fast", "Вопрос"),
    ['ChatGPT mode not switched (the picker still shows "Высокий"); the answer hint was still sent']);
  expect("the menu is closed", page.chat.menu, null);
  expect("the prompt is sent", page.chat.sent, ["Вопрос"]);
});

check("Stop in LINK stops ChatGPT; an answer LINK took elsewhere is left alone", async () => {
  let page = await openChat({ tokenMs: 400 });
  let { api, bridge } = await startCompanion(page);
  let request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  await until("the answer streams", () => bridge.of(request.requestId, "receiving").length);
  bridge.ended.set(request.requestId, "cancelled");
  await until("ChatGPT is stopped", () => page.chat.stopClicks === 1, 5000);
  expect("no answer is reported after Stop", bridge.of(request.requestId, "completed").length, 0);
  page.frame.remove();

  page = await openChat({ tokenMs: 400 });
  ({ api, bridge } = await startCompanion(page));
  request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  await until("the answer streams", () => bridge.of(request.requestId, "receiving").length);
  bridge.ended.set(request.requestId, "completed");
  // Its next event tells the companion; after that it reports nothing more.
  await sleep(3000);
  const events = bridge.events.length;
  await sleep(3000);
  expect("the companion stopped reporting", bridge.events.length, events);
  expect("ChatGPT is not stopped", page.chat.stopClicks, 0);
});

check("a prompt ChatGPT does not take ends with an error instead of waiting", async () => {
  const page = await openChat({ takesPrompt: false });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  const error = await until("an error is reported", () => bridge.of(request.requestId, "error")[0], 30000);
  expect("it says the prompt was not sent", error?.error.startsWith("ChatGPT did not take the prompt"), true);
  expect("with what the page saw", JSON.parse(error?.diagnostics ?? "{}").prompt, "not found");
});

check("Check chat reads the answer and says which prompt it belongs to", async () => {
  const page = await openChat({ tokenMs: 300 });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  await until("the answer streams", () => bridge.of(request.requestId, "receiving").length);
  const read = { requestId: crypto.randomUUID(), conversationId: page.conversationId, kind: "read" };
  await sendToPage(api, { type: "link-request", request: read });
  const during = await until("a snapshot comes", () => bridge.of(read.requestId, "snapshot")[0]);
  expect("while ChatGPT writes", [during?.generating, during?.answerTo, ANSWER_MARKDOWN.startsWith(during?.text)],
    [true, request.requestId, true]);

  await completedOf(bridge, request);
  const after = { requestId: crypto.randomUUID(), conversationId: page.conversationId, kind: "read" };
  await sendToPage(api, { type: "link-request", request: after });
  const snapshot = await until("a snapshot comes", () => bridge.of(after.requestId, "snapshot")[0]);
  expect("once it is done", [snapshot?.generating, snapshot?.answerTo, snapshot?.text], [false, null, ANSWER_MARKDOWN]);
});

check("octane: streams the answer and completes it on ChatGPT's completion flag", async () => {
  const page = await openChat({ ui: "octane" });
  const { bridge, request, completed } = await expectAnswer(page, "the complete answer comes as Markdown");
  expect("a usual end needs no diagnostics", completed?.diagnostics, undefined);
  const streamed = bridge.of(request.requestId, "receiving").map(event => event.text);
  expect("the answer streams in steps before it ends", streamed.length >= 3, true);
  expect("the thinking status is never the answer", streamed.some(text => text.includes("Думаю")), false);
});

check("octane: Fast switches the composer's popover picker from Высокий to Мгновенный", async () => {
  for (const pickerHaspopup of [true, false]) {
    const page = await openChat({ ui: "octane", pickerHaspopup });
    const companion = await startCompanion(page);
    const kind = pickerHaspopup ? "a menu button" : "a plain popover button";
    expect(`LINK is told the mode (${kind})`, await askWithEffort(companion, "fast"), ["ChatGPT mode: Мгновенный"]);
    expect(`the picker shows Мгновенный (${kind})`, page.chat.mode, "Мгновенный");
    expect(`the popover is closed (${kind})`, page.chat.menu, null);
    page.frame.remove();
  }
});

check("octane: a prompt the chat renders as Markdown is still found", async () => {
  await expectAnswer(await openChat({ ui: "octane", userMarkdown: true }), "its answer, not an older one");
});

check("octane: new turns that land before the transcript are found", async () => {
  await expectAnswer(await openChat({ ui: "octane", newTurnsBefore: true }), "the new answer, not the transcript's last one");
});

check("a hidden message template at the end of the page does not hide the prompt", async () => {
  await expectAnswer(await openChat({ ui: "octane", trailingTemplate: true }), "the answer to the prompt");
});

check("shell: the prompt and its answer are found in the newest UI, and the answer's own buttons complete it", async () => {
  const page = await openChat({ ui: "shell" });
  // The prompt's own action buttons share the turn from the start; they must not cut the answer short.
  const { bridge, request, completed } = await expectAnswer(page, "the complete answer comes as Markdown");
  expect("a usual end needs no diagnostics", completed?.diagnostics, undefined);
  const streamed = bridge.of(request.requestId, "receiving").map(event => event.text);
  expect("the answer streams in steps before it ends", streamed.length >= 3, true);
});

check("shell: an answer still being typed out when Stop has gone is taken whole", async () => {
  await expectAnswer(await openChat({ ui: "shell", typedAfterStop: true }), "the whole answer, not the part there when Stop went");
});

check("shell: Fast, Deep and Normal move the picker's effort slider", async () => {
  const page = await openChat({ ui: "shell", mode: "Высокий", tokenMs: 20 });
  const companion = await startCompanion(page);
  const run = async effort => [await askWithEffort(companion, effort), page.chat.mode];
  expect("Fast", await run("fast"), [["ChatGPT mode: Instant"], "Instant"]);
  expect("the menu is closed", page.chat.menu, null);
  expect("Deep", await run("deep"), [["ChatGPT mode: Высокий"], "Высокий"]);
  expect("Fast again", await run("fast"), [["ChatGPT mode: Instant"], "Instant"]);
  expect("Normal gives the user's mode back", await run("normal"), [["ChatGPT mode: Высокий"], "Высокий"]);
});

check("shell: Check chat sees ChatGPT writing, and Stop in LINK presses its Остановить", async () => {
  const page = await openChat({ ui: "shell", tokenMs: 400 });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  await until("the answer streams", () => bridge.of(request.requestId, "receiving").length);
  const read = { requestId: crypto.randomUUID(), conversationId: page.conversationId, kind: "read" };
  await sendToPage(api, { type: "link-request", request: read });
  const snapshot = await until("a snapshot comes", () => bridge.of(read.requestId, "snapshot")[0]);
  expect("ChatGPT is still writing", [snapshot?.generating, snapshot?.answerTo], [true, request.requestId]);
  bridge.ended.set(request.requestId, "cancelled");
  await until("ChatGPT is stopped", () => page.chat.stopClicks === 1, 5000);
});

// ChatGPT then gives the chat its /c/<id> address, which a file:// page cannot take on; LINK's checks cover that part.
check("shell: a new chat's page takes the prompt that starts it", async () => {
  const page = await openChat({ ui: "shell", src: "new-chat.html", turns: 0 });
  expect("a new chat's page has no conversation", page.conversationId, null);
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest("", "Первый вопрос нового чата");
  expect("the page takes the prompt", await ask(api, request), { accepted: true, version: VERSION });
  const completed = await completedOf(bridge, request);
  expect("the answer completes", completed?.text, ANSWER_MARKDOWN);
  expect("the prompt is sent once", page.chat.sent, ["Первый вопрос нового чата"]);
});

check("shell: a new chat's picker is waited for while the composer's buttons are not there yet", async () => {
  const page = await openChat({ ui: "shell", src: "new-chat.html", turns: 0, controlsMs: 600 });
  const companion = await startCompanion(page);
  expect("LINK is told the mode", await askWithEffort(companion, "fast"), ["ChatGPT mode: Instant"]);
  expect("the picker shows Instant", page.chat.mode, "Instant");
});

check("a prompt for a linked chat is not sent on a new chat's page", async () => {
  const page = await openChat({ ui: "shell", src: "new-chat.html", turns: 0 });
  const { api, bridge } = await startCompanion(page);
  const request = promptRequest("chat-1.html", "Вопрос");
  await ask(api, request);
  const error = await until("an error is reported", () => bridge.of(request.requestId, "error")[0]);
  expect("it says the page shows another conversation", error?.error, "The linked ChatGPT page shows another conversation.");
  expect("nothing is sent", page.chat.sent, []);
});

check("a chat still writing an earlier answer gets no prompt, and LINK's words leave its composer", async () => {
  let page = await openChat({ ui: "shell", busyMs: 60000 });
  let { api, bridge } = await startCompanion(page);
  let request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  const error = await until("an error is reported", () => bridge.of(request.requestId, "error")[0], 30000);
  expect("it says ChatGPT is still answering", error?.error.startsWith("ChatGPT is still answering the previous message"), true);
  expect("the prompt is not sent", page.chat.sent, []);
  expect("its words are taken back from the composer", page.chat.composer.innerText.trim(), "");
  expect("the earlier answer is not stopped", page.chat.stopClicks, 0);
  page.frame.remove();

  // Stopped in LINK while the chat is busy: once the chat is free, nothing is sent and nothing is stopped.
  page = await openChat({ ui: "shell", busyMs: 3000 });
  ({ api, bridge } = await startCompanion(page));
  request = promptRequest(page.conversationId, "Вопрос");
  await ask(api, request);
  await sleep(1000);
  bridge.ended.set(request.requestId, "cancelled");
  await sleep(8000);
  expect("the stopped prompt is not sent", page.chat.sent, []);
  expect("nor left in the composer", page.chat.composer.innerText.trim(), "");
  expect("the chat's own answer is not stopped", page.chat.stopClicks, 0);
});
