(() => {
// This copy's own extension API: when the extension is reloaded it goes dead here, and a newer copy gets a new one.
const chrome = globalThis.chrome;
const VERSION = chrome.runtime.getManifest().version;
// One working copy per page: the manifest and the service worker may both start this script, and two copies would
// both answer every LINK request.
if (globalThis.linkCompanion?.alive()) return;

const ANNOUNCE_INTERVAL_MS = 2500;
const RESPONSE_TIMEOUT_MS = 10 * 60 * 1000;
const HEARTBEAT_INTERVAL_MS = 2000;
// ChatGPT re-renders the answer on every token. While the page changes, LINK looks at it at most this often, and
// only at the composer and the last turns, so even a long chat stays cheap...
const CHECK_INTERVAL_MS = 150;
// ...and turns the growing answer into Markdown at most this often (less often for an answer slow to convert), for the
// overlay and reading aloud.
const PROGRESS_INTERVAL_MS = 250;
// Without ChatGPT's own sign of completion, a quiet period after its Stop button disappears ends the answer; a longer
// one in a hidden tab, whose throttled timers may still be putting the last words on the page, and for an answer
// without a Markdown root, so that a status line cannot pass for it.
const SETTLE_MS = 1200;
const LONG_SETTLE_MS = 3000;
// An answer that has not changed at all for this long is complete even if a Stop-like button lingers.
const STALL_MS = 60 * 1000;
// The whole page is searched for the answer this often while it has not turned up where new turns land; seldom when
// the prompt itself landed there, since its answer will follow it.
const SEARCH_INTERVAL_MS = 1000;
const SEARCH_INTERVAL_IN_THREAD_MS = 5000;
// A prompt still in the composer this long after Send was not taken by ChatGPT.
const SEND_CONFIRM_MS = 20 * 1000;
// A prompt picked up again (after a reload) that the chat does not show by then has gone from the page.
const RESUME_TIMEOUT_MS = 20 * 1000;
// The chat shows a sent prompt at once; an answer is taken without the prompt in sight only after this long.
const PROMPT_GRACE_MS = 5000;
// LINK counts as gone when no event of a prompt has reached it for this long.
const BRIDGE_TIMEOUT_MS = 30 * 1000;
const REQUEST_STATE_TTL_MS = 60 * 60 * 1000;
const MARKDOWN_ROOTS = "[data-assistant-markdown], [data-markdown-text-style], .markdown, [class*='markdown']";
const ASSISTANT_MESSAGES = '[data-message-author-role="assistant"], article[data-turn="assistant"], li[data-message-role="assistant"], ' +
  '[data-chatgpt-search-unit-key$=":assistant"]';
const USER_MESSAGES = '[data-message-author-role="user"], article[data-turn="user"], li[data-message-role="user"], ' +
  '[data-chatgpt-search-unit-key$=":user"]';
const TURNS = "article, [data-turn-id], [data-testid^='conversation-turn'], [data-turn-key]";
// ChatGPT gives a finished turn its action buttons (copy, rate, read aloud…); a turn still being written has none.
// The newest UI has no test ids on them, and its prompt has action buttons of its own in the same turn.
const TURN_ACTIONS = '[data-testid$="turn-action-button"], .turn-action-controls button';
// The newest UI types a received answer out over time, and its Stop button goes before it has finished typing.
const STREAMING = '[data-message-streaming]:not([data-message-streaming="false"]), .result-streaming, [data-markdown-animated]';
// In order of preference: the classic composer's id first, which the browser finds without searching the page.
const COMPOSERS = ["#prompt-textarea", 'textarea[data-testid="prompt-textarea"]', 'textarea[name="prompt"]',
  "[data-mobile-composer-prompt]", 'textarea[placeholder*="Message"]', '[contenteditable="true"][data-virtualkeyboard]'];
const SEND_BUTTONS = ['button[data-testid="send-button"]', "button[data-composer-submit]", 'button[aria-label="Send prompt"]',
  'form button[type="submit"]'];
// The `i` flag folds only Latin case, so the Russian label is matched as ChatGPT writes it ("Остановить").
const STOP_BUTTONS = 'button[data-testid="stop-button"], button[data-composer-submit], ' +
  'button[aria-label*="stop" i], button[aria-label*="Останов"], button[aria-label*="останов"]';
// Status lines that stand in for an answer while ChatGPT thinks or searches.
const STATUS_LINE = /^(thinking|thought|reasoning|searching|analy[sz]|думаю|думал|размышл|рассужд|ищу|поиск|анализ)/i;
// ChatGPT's answer modes as its picker names them. Since June 2026 the picker sits in the composer (Instant, Medium,
// High, Extra High; "Мгновенный", "Средний", "Высокий", "Очень высокий"); the older header picker offered Instant,
// Auto and Thinking. Extra High is tried first, so it never passes for High.
const MODES = [
  ["extra", /^(extra[\s-]*high|очень\s+высок|максимал)/i],
  ["instant", /^(instant|мгновен|быстр)/i],
  ["medium", /^(medium|средн|standard|стандарт)/i],
  ["high", /^(high|высок)/i],
  ["auto", /^(auto|авто)/i],
  ["thinking", /^(thinking|думающ|рассужд)/i]
];
const MODE_NAMES = { extra: "Extra High", instant: "Instant", medium: "Medium", high: "High", auto: "Auto", thinking: "Thinking" };
// LINK's Fast / Normal / Deep: the chat's modes to use, the first one it offers.
const EFFORT_MODES = { fast: ["instant"], normal: ["medium", "auto"], deep: ["high", "thinking"] };
const EXCLUDED_MODES = /\bpro\b|upgrade|legacy|улучш|устаревш/i;
// The composer of the new UI ("octane") opens its menus as native popovers (popovertarget), not Radix menus.
const PICKER_BUTTONS = 'button[aria-haspopup], button[popovertarget], [role="combobox"]';
const MENUS = '[role="menu"], [role="listbox"], [data-radix-popper-content-wrapper]';
// Since September 2026 the picker's menu sets the effort on a slider ("Мощность"), a level per arrow key, and
// announces the level it is at ("Высокий, 3 из 3."). Its levels from the lightest:
const EFFORT_SLIDERS = '[data-reasoning-slider], [role="menuitem"][aria-keyshortcuts~="ArrowRight"]';
const SLIDER_ORDER = ["instant", "medium", "high", "extra"];
const SKIPPED_NODES = 'button, svg, style, script, [hidden], [aria-hidden="true"], .sr-only, [class*="shimmer"], ' +
  "[data-message-content-controls], [data-message-actions], [data-message-attribution], [data-assistant-placeholder-slot]";
const INLINE_TAGS = new Set(["A", "ABBR", "B", "BR", "CODE", "DEL", "EM", "I", "IMG", "KBD", "MARK", "S", "SMALL", "SPAN", "STRONG", "SUB", "SUP", "U"]);
const instanceId = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
const pendingDelays = new Set();
// The prompt this copy works on.
let current = null;
let retired = false;
let announceTimer = null;

/** LINK no longer waits for the request: its reason is "cancelled" after Stop/Esc, otherwise it ended elsewhere. */
class EndedError extends Error {
  constructor(reason) {
    super(`LINK no longer waits for this request (${reason}).`);
    this.reason = reason;
  }
}

/** A newer copy of the companion took over this page; it carries on with the prompt. */
class RetiredError extends Error {}

globalThis.linkCompanion = { version: VERSION, instanceId, alive: () => !retired && Boolean(chrome.runtime?.id) };
chrome.runtime.onMessage.addListener(onMessage);
document.addEventListener("link-companion-started", onCompanionStarted);
// A copy left behind by a reloaded extension runs in its own world and steps aside for this one.
document.dispatchEvent(new CustomEvent("link-companion-started", { detail: instanceId }));

function onMessage(message, _sender, sendResponse) {
  if (retired) return;
  switch (message?.type) {
    case "link-hello":
      sendResponse({ version: VERSION, instanceId, conversation: getConversation(), busy: current?.requestId ?? null });
      return;
    case "link-request":
      sendResponse({ accepted: true, version: VERSION });
      // "Check chat" only reads the page, so it works while an answer is still awaited, or stuck.
      if (message.request?.kind === "read") void handleRead(message.request);
      else void handlePrompt(message.request);
      return;
    case "link-tick":
      // Hidden tabs get their timers throttled down to once a minute; the service worker's ticks keep waits on time.
      flushDelays();
      return;
    case "link-retire":
      retire();
      sendResponse({ retired: true });
      return;
  }
}

function onCompanionStarted(event) {
  if (event.detail !== instanceId) retire();
}

/** Stops everything this copy does. Its prompt is left as it is: LINK hands it to the copy that took over. */
function retire() {
  if (retired) return;
  retired = true;
  clearInterval(announceTimer);
  document.removeEventListener("link-companion-started", onCompanionStarted);
  try {
    chrome.runtime.onMessage.removeListener(onMessage);
  } catch {
    // An orphaned copy's API may throw; it receives nothing anyway.
  }
  if (current) stopJob(current, new RetiredError("A newer copy of the LINK companion took over."));
  if (globalThis.linkCompanion?.instanceId === instanceId) delete globalThis.linkCompanion;
}

function announceConversation() {
  if (!chrome.runtime?.id) {
    // The extension was reloaded; this orphaned copy can no longer reach it.
    retire();
    return;
  }
  const conversation = getConversation();
  if (!conversation) return;
  void chrome.runtime.sendMessage({ type: "link-announce", conversation, busy: current?.requestId ?? null })
    .catch(() => {});
}

function getConversation() {
  const conversationId = getConversationId();
  return conversationId ? { conversationId, title: getConversationTitle(), url: location.href } : null;
}

async function handlePrompt(request) {
  if (current?.requestId === request.requestId) {
    // LINK offers a prompt again when it has heard nothing about it for a while; this copy is still on it.
    void reportQuietly(request.requestId, "heartbeat");
    return;
  }
  if (current) {
    // LINK sends a prompt only when it is done with the previous one; this copy may not have heard yet.
    const previous = current;
    try {
      await report(previous.requestId, "heartbeat");
      await reportQuietly(request.requestId, "error", { error: "The ChatGPT tab is still busy with the previous LINK request." });
      return;
    } catch (error) {
      stopJob(previous, error);
      await previous.done;
    }
  }
  if (retired || current) return;

  const job = {
    requestId: request.requestId,
    conversationId: request.conversationId,
    // The prompt's first words, to find it among the chat's messages however the chat renders it.
    head: looseText(request.prompt).slice(0, 60),
    stopReason: null,
    wake: null,
    resumed: false,
    sentAt: 0,
    // What became of the answer mode, for the diagnostics.
    mode: null,
    baseline: null,
    lastUserKey: null,
    prompt: null,
    promptKey: null,
    thread: null,
    response: null,
    // The answer's turn, and the answer and prompt it was worked out for.
    turn: null,
    turnFor: null,
    turnPrompt: null
  };
  current = job;
  job.done = runPrompt(request, job);
  await job.done;
  if (current === job) current = null;
}

function stopJob(job, reason) {
  job.stopReason ??= reason;
  job.wake?.();
}

async function runPrompt(request, job) {
  try {
    if (getConversationId() !== job.conversationId) {
      throw new Error("The linked ChatGPT tab shows another conversation.");
    }
    const saved = await loadRequestState(request.requestId);
    job.lastUserKey = saved?.lastUserKey ?? null;
    job.promptKey = saved?.promptKey ?? null;
    // A prompt that reached the chat before, from this page or one it replaced, is never sent twice: its answer is
    // picked up where it is. One only on its way when that page went counts as sent if the chat shows it.
    job.resumed = Boolean(request.sent || saved?.phase === "sent" ||
      (saved?.phase === "sending" && job.lastUserKey && findPromptMessage(job)));
    if (job.resumed) job.thread = findThread();
    else await sendPrompt(request, job);

    const answer = await watchAnswer(job);
    await report(request.requestId, "completed", answer, 8);
    void forgetRequestState(request.requestId);
  } catch (error) {
    if (error instanceof RetiredError) return;
    void forgetRequestState(request.requestId);
    // A prompt that never went out does not stay behind in ChatGPT's composer.
    if (!job.sentAt && !job.resumed) takeBackPrompt(job);
    if (error instanceof EndedError) {
      // Stop/Esc in LINK stops ChatGPT's answer to the prompt too; an answer LINK has taken otherwise is left alone.
      if (error.reason === "cancelled" && (job.sentAt || job.resumed)) findStopButton(composerForm())?.click();
      return;
    }
    await reportQuietly(request.requestId, "error", {
      error: error instanceof Error ? error.message : String(error),
      diagnostics: JSON.stringify(diagnose(job))
    });
  }
}

async function sendPrompt(request, job) {
  await report(request.requestId, "sending");
  const composer = await waitFor(findComposer, 15000, "ChatGPT message composer was not found.");
  job.mode = await applyEffort(request, composer.closest("form"));
  if (request.attachments?.length) await attachImages(composer, request.attachments);
  setComposerText(composer, request.prompt);
  job.baseline = takeBaseline();
  job.lastUserKey = job.baseline.lastUser ? messageKey(job.baseline.lastUser) : null;
  job.thread = findThread();
  await saveRequestState(request.requestId, { phase: "sending", lastUserKey: job.lastUserKey });

  const sendButton = await waitFor(findEnabledSendButton, 15000, null);
  if (!sendButton) {
    throw new Error(findStopButton(composerForm())
      ? "ChatGPT is still answering the previous message. Send again once it has finished, or stop it in the chat."
      : "ChatGPT Send button did not become available.");
  }
  // LINK may have been stopped while the chat was busy; a copy replaced meanwhile sends nothing: the newer one does.
  await report(request.requestId, "sending");
  if (job.stopReason) throw job.stopReason;
  if (!chrome.runtime?.id) throw new RetiredError("The extension was reloaded.");
  sendButton.click();
  job.sentAt = Date.now();
  await saveRequestState(request.requestId, { phase: "sent", lastUserKey: job.lastUserKey });
  await report(request.requestId, "waiting");
}

function takeBackPrompt(job) {
  const composer = findComposer();
  if (composer && job.head && looseText(composerText(composer)).includes(job.head)) setComposerText(composer, "");
}

/**
 * The messages LINK saw before sending, by id (or as elements, without one): whatever is new after it is the prompt
 * and its answer, wherever the chat puts them.
 */
function takeBaseline() {
  const users = [...document.querySelectorAll(USER_MESSAGES)];
  const messages = [...users, ...document.querySelectorAll(ASSISTANT_MESSAGES)];
  return {
    seen: new Set(messages.map(message => messageKey(message) ?? message)),
    lastUser: users.at(-1) ?? null
  };
}

function isNewMessage(message, baseline) {
  return !baseline.seen.has(messageKey(message) ?? message);
}

async function handleRead(request) {
  await reportQuietly(request.requestId, "snapshot", readSnapshot());
}

/**
 * The answer to the last prompt as the page shows it now, whether ChatGPT is still writing it, and what LINK's
 * selectors see, so a missed answer can be taken over and its cause found in link.log. While this copy works on a
 * prompt, only an answer to that prompt counts.
 */
function readSnapshot() {
  const job = current;
  let prompt;
  let response;
  if (job) {
    // The same search as the watch's, which may be stuck or not have looked yet.
    prompt = job.prompt?.isConnected ? job.prompt : findPromptMessage(job);
    response = job.response?.isConnected ? job.response : searchResponse({ ...job, prompt });
  } else {
    prompt = lastMatch(document, USER_MESSAGES);
    const latest = lastMatch(document, ASSISTANT_MESSAGES);
    response = latest && (!prompt || follows(prompt, latest)) ? latest : null;
  }
  const signs = answerSigns(response, response ? turnOf(response, prompt) : null, composerForm());
  const read = response ? readAnswer(response) : { markdown: "", plain: "" };
  const generating = !signs.actions && Boolean(signs.stop || signs.streaming);
  return {
    answerTo: job && response ? job.requestId : null,
    text: read.markdown || (!generating && !isStatusLine(read.plain) ? read.plain : ""),
    generating,
    diagnostics: JSON.stringify(diagnose(job, response, prompt))
  };
}

function diagnose(job, response = job?.response, prompt = job?.prompt) {
  const form = composerForm();
  const answer = response?.isConnected ? response : null;
  const turn = answer ? turnOf(answer, prompt) : null;
  const read = answer ? readAnswer(answer) : null;
  const found = Boolean(prompt?.isConnected);
  return {
    version: VERSION,
    busy: current?.requestId ?? null,
    resumed: job?.resumed ?? null,
    mode: job?.mode ?? null,
    prompt: job ? (found ? messageKey(prompt) ?? "found" : "not found") : null,
    // What the page offers as messages when the prompt or the answer is not among them, and how it marks them when
    // the selectors find none at all: how to tell them next time.
    messages: !found || !answer ? describeMessages(job) : undefined,
    markup: document.querySelector(`${USER_MESSAGES}, ${ASSISTANT_MESSAGES}`) ? undefined : describeMarkup(),
    answer: answer ? {
      tag: answer.tagName.toLowerCase(),
      id: messageKey(answer),
      markdownRoots: answer.querySelectorAll(MARKDOWN_ROOTS).length,
      markdownLength: read.markdown.length,
      plainLength: read.plain.length,
      complete: Boolean(answer.closest("[data-message-complete]")),
      streaming: Boolean(answer.matches(STREAMING) || answer.querySelector(STREAMING)),
      turnButtons: answerActions(turn).map(button => button.dataset.testid ?? button.getAttribute("aria-label")).slice(0, 12)
    } : null,
    stopButtons: [...(form ?? document).querySelectorAll(STOP_BUTTONS)].slice(0, 6).map(button =>
      `${button.dataset.testid ?? ""}|${button.getAttribute("aria-label") ?? ""}|${isShown(button) ? "shown" : "hidden"}`),
    // The composer's buttons by label: a Stop or Send the selectors miss shows up here.
    composerButtons: [...(form?.querySelectorAll("button[aria-label]") ?? [])].map(button => button.getAttribute("aria-label")),
    picker: describeElement(findModePicker(form))
  };
}

/** The last messages of each kind: their element, id, whether they are new since sending and match the prompt. */
function describeMessages(job) {
  const describe = (selector, count) => [...document.querySelectorAll(selector)].slice(-count).map(message => ({
    element: describeElement(message),
    new: job?.baseline ? isNewMessage(message, job.baseline) : null,
    matches: job ? matchesPrompt(message, job) : null,
    text: normalize(message.textContent).slice(0, 60),
    parents: [message.parentElement, message.parentElement?.parentElement].map(describeElement)
  }));
  return { head: job?.head, lastUserKey: job?.lastUserKey, users: describe(USER_MESSAGES, 4), assistants: describe(ASSISTANT_MESSAGES, 2) };
}

/**
 * How the page marks its messages when the selectors miss them: the attributes that name messages or turns, how
 * often each occurs, and the way down to the thread through the largest element, with the last ones of crowded levels.
 */
function describeMarkup() {
  const root = document.querySelector("main") ?? document.body;
  const attributes = new Map();
  for (const element of root.querySelectorAll("*")) {
    for (const { name, value } of element.attributes) {
      if (!/message|turn|author|transcript/i.test(name)) continue;
      const key = `${element.tagName.toLowerCase()}[${name}${/^[a-z-]{1,20}$/i.test(value) ? `=${value}` : ""}]`;
      attributes.set(key, (attributes.get(key) ?? 0) + 1);
    }
  }
  const path = [];
  const size = element => element.getElementsByTagName("*").length;
  for (let node = root, depth = 0; node && size(node) >= 10 && depth < 20; depth++) {
    const children = [...node.children];
    path.push(`${describeElement(node)} x${children.length}`);
    if (children.length > 2) path.push(...children.slice(-3).map(child => `  ${describeElement(child)}`));
    node = children.reduce((largest, child) => !largest || size(child) > size(largest) ? child : largest, null);
  }
  return { attributes: Object.fromEntries([...attributes].slice(0, 40)), path };
}

/** An element as a short line: its tag, label and attributes, for link.log. */
function describeElement(element) {
  if (!element) return null;
  const attributes = [...element.attributes]
    .filter(attribute => attribute.name !== "class" && attribute.name !== "style")
    .map(attribute => attribute.value ? `${attribute.name}=${attribute.value.slice(0, 40)}` : attribute.name);
  return `${element.tagName.toLowerCase()}[${attributes.join(" ")}] "${firstLine(element.textContent).slice(0, 30)}"`;
}

/** Puts the chat into the mode for LINK's effort; what became of it, in a few words. */
async function applyEffort(request, form) {
  const effort = EFFORT_MODES[request.effort] ? request.effort : "normal";
  const appliedKey = `effort:${request.conversationId}`;
  const ownKey = `mode:${request.conversationId}`;
  let saved = {};
  try {
    saved = await chrome.storage.local.get([appliedKey, ownKey]);
  } catch {
    // Without storage the chat's mode is simply checked again.
  }
  const applied = saved[appliedKey] ?? "normal";
  // Normal leaves the user's own mode alone until LINK has changed it, and then gives that mode back.
  if (effort === "normal" && applied === "normal") return "normal: the user's own mode";

  const own = effort === "normal" ? saved[ownKey] : null;
  const targets = own ? [modeOf(own) ?? own, ...EFFORT_MODES.normal] : EFFORT_MODES[effort];
  const result = await switchChatMode(form, targets);
  if (!result.ok) {
    await report(request.requestId, "notice", { notice: `ChatGPT mode not switched (${result.reason}); the answer hint was still sent` });
    return `${effort}: not switched (${result.reason})`;
  }

  try {
    const update = { [appliedKey]: effort };
    // Only a picker that names the mode tells which one was the user's.
    if (applied === "normal" && modeOf(result.previous)) update[ownKey] = result.previous;
    await chrome.storage.local.set(update);
  } catch {
    // The chat is in the right mode anyway; the next request checks it again.
  }
  if (result.changed) await report(request.requestId, "notice", { notice: `ChatGPT mode: ${result.label}` });
  return `${effort}: ${result.changed ? `switched from "${result.previous}" to` : "already"} "${result.label}"`;
}

/**
 * Puts the chat into the first of `targets` it offers (mode ids, or a mode's own label) and checks that the picker
 * shows it then. The picker shows the current mode, so a chat already in it is left alone; the user may have changed
 * it by hand since LINK last did.
 */
async function switchChatMode(form, targets) {
  const picker = findModePicker(form);
  if (!picker) return { ok: false, reason: "mode picker not found" };
  const previous = pickerLabel(picker);
  if (matchesMode(previous, targets[0])) return { ok: true, changed: false, label: previous, previous };

  const menu = await openMenu(picker);
  if (!menu) return { ok: false, reason: `the "${previous}" picker did not open` };
  const slider = menu.querySelector(EFFORT_SLIDERS);
  if (slider) return slideToMode(menu, slider, targets, previous);
  const offered = menuItems(menu);
  const choice = targets
    .map(target => [target, offered.find(item => matchesMode(firstLine(item.innerText), target) && !isDisabled(item))])
    .find(([, item]) => item);
  if (!choice) {
    closeMenu(menu);
    // The labels on offer tell what a changed picker calls its modes now.
    const labels = offered.map(item => firstLine(item.innerText)).filter(Boolean).join(", ");
    return { ok: false, reason: `${MODE_NAMES[targets[0]] ?? targets[0]} is not offered${labels ? `: ${labels}` : ""}` };
  }

  const [target, item] = choice;
  // The picker may name the chat's mode differently from the menu: the first mode on offer may be the current one.
  if (matchesMode(previous, target)) {
    closeMenu(menu);
    return { ok: true, changed: false, label: previous, previous };
  }
  const label = await chooseMode(picker, menu, item, target);
  if (!label) {
    closeMenu(menu);
    return { ok: false, reason: `the picker still shows "${pickerLabel(findModePicker(composerForm())) || previous}"` };
  }
  return { ok: true, changed: true, label, previous };
}

/**
 * Moves the menu's effort slider towards the first target it can hold, a level per arrow key, as far as the target or
 * the slider's end (for Fast, the lightest level there is), and checks that the picker shows the level then.
 */
async function slideToMode(menu, slider, targets, previous) {
  const target = targets.find(mode => SLIDER_ORDER.includes(mode));
  const level = () => [...menu.querySelectorAll('[role="status"]')]
    .map(status => firstLine(status.textContent).split(",")[0].trim())
    .find(modeOf) ?? "";
  let label = level();
  for (let step = 0; target && modeOf(label) !== target && step < SLIDER_ORDER.length; step++) {
    const at = SLIDER_ORDER.indexOf(modeOf(label));
    if (at < 0) break;
    const key = at > SLIDER_ORDER.indexOf(target) ? "ArrowLeft" : "ArrowRight";
    slider.focus();
    slider.dispatchEvent(new KeyboardEvent("keydown", { key, code: key, bubbles: true, cancelable: true }));
    const moved = await waitFor(() => level() !== label ? level() : null, 1500, null);
    if (!moved) break;
    label = moved;
  }
  closeMenu(menu);
  const reached = modeOf(label);
  if (!reached || reached === modeOf(previous) && reached !== target) {
    return { ok: false, reason: `the effort slider stays at "${label || previous}"` };
  }
  const shown = await waitFor(() => {
    const now = pickerLabel(findModePicker(composerForm()));
    return modeOf(now) === modeOf(label) ? now : null;
  }, 2000, null);
  if (!shown) return { ok: false, reason: `the picker still shows "${pickerLabel(findModePicker(composerForm())) || previous}"` };
  return { ok: true, changed: modeOf(shown) !== modeOf(previous), label: shown, previous };
}

/**
 * Chooses a menu item; the mode's label once the picker shows it, or null when it never does. A picker that does
 * not show the mode (the header's "ChatGPT") only tells that it took the choice by closing its menu.
 */
async function chooseMode(picker, menu, item, target) {
  const labelled = Boolean(modeOf(pickerLabel(picker)));
  // Read while the menu is open: a closed menu's item has no lines any more.
  const itemLabel = firstLine(item.innerText);
  const shown = () => {
    if (!labelled) return !menu.isConnected || picker.getAttribute("aria-expanded") === "false" ? itemLabel : null;
    const label = pickerLabel(findModePicker(composerForm()));
    return matchesMode(label, target) ? label : null;
  };
  item.click();
  let label = await waitFor(shown, 2000, null);
  if (!label && item.isConnected) {
    // Some menus choose on Enter rather than on a click.
    item.focus();
    item.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", code: "Enter", bubbles: true, cancelable: true }));
    label = await waitFor(shown, 1500, null);
  }
  return label;
}

/**
 * The composer's mode picker, labelled with the current mode ("Высокий"); it may sit just outside the composer's
 * form. The header's model picker ("ChatGPT") only when the composer has none: it held the modes before June 2026.
 */
function findModePicker(form) {
  for (let root = form, level = 0; root && level < 3; root = root.parentElement, level++) {
    const pickers = [...root.querySelectorAll(PICKER_BUTTONS)]
      .filter(button => !button.closest(`${TURNS}, [data-message-author-role]`) && modeOf(pickerLabel(button)));
    if (pickers.length) return pickers.find(isShown) ?? pickers[0];
  }
  return document.querySelector('[data-testid="model-switcher-dropdown-button"]');
}

function pickerLabel(picker) {
  return firstLine(picker?.innerText) || picker?.getAttribute("aria-label")?.trim() || "";
}

/** The mode a label names, by its first word; a label may name its kind first ("Reasoning · High"). */
function modeOf(label) {
  const parts = firstLine(label).split(/\s*[·:|•—–]\s*/);
  for (const part of [...parts.slice(1).reverse(), parts[0]]) {
    const mode = MODES.find(([, pattern]) => pattern.test(part));
    if (mode) return mode[0];
  }
  return null;
}

function matchesMode(label, target) {
  return MODE_NAMES[target] ? modeOf(label) === target : firstLine(label).toLowerCase() === target.toLowerCase();
}

async function openMenu(picker) {
  const before = new Set(document.querySelectorAll(MENUS));
  // A native popover is the picker's own target and shows as :popover-open; it opens on a click.
  const popover = picker.popoverTargetElement ?? null;
  const opened = () => {
    if (popover) return popover.matches(":popover-open") ? popover : null;
    const id = picker.getAttribute("aria-controls");
    const own = id ? document.getElementById(id) : null;
    if (own) return own;
    const menus = [...document.querySelectorAll(MENUS)];
    return menus.find(menu => !before.has(menu)) ??
      (picker.getAttribute("aria-expanded") === "true" ? menus.find(isShown) : null) ?? null;
  };
  // Radix menus open on pointerdown, and a synthetic click alone does not open them; other menus open on click or Enter.
  const click = () => picker.click();
  const ways = popover ? [click] : [
    () => picker.dispatchEvent(new PointerEvent("pointerdown", {
      bubbles: true, cancelable: true, button: 0, pointerType: "mouse", isPrimary: true
    })),
    click,
    () => {
      picker.focus();
      picker.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", code: "Enter", bubbles: true, cancelable: true }));
    }
  ];
  for (const open of ways) {
    if (!picker.isConnected) return null;
    open();
    const menu = await waitFor(opened, 1500, null);
    if (menu) return menu;
  }
  // A menu that opened where LINK could not see it must not stay open over the chat.
  closeMenu(popover ?? picker);
  return null;
}

function menuItems(menu) {
  const items = [...menu.querySelectorAll('[role^="menuitem"], [role="option"], [role="radio"], [data-testid^="model-switcher-"]')];
  return (items.length ? items : [...menu.querySelectorAll("button")])
    .filter(item => item.getAttribute("aria-haspopup") !== "menu" &&
      !EXCLUDED_MODES.test(`${item.getAttribute("data-testid") ?? ""} ${item.innerText ?? ""}`));
}

function isDisabled(item) {
  return item.getAttribute("aria-disabled") === "true" || item.hasAttribute("data-disabled") || item.disabled === true;
}

/** Closes a menu: a native popover by its own call, since a synthetic Escape does not close one; others by Escape. */
function closeMenu(menu) {
  if (menu.matches?.(":popover-open")) {
    menu.hidePopover();
    return;
  }
  (menu.isConnected ? menu : document.activeElement ?? document.body).dispatchEvent(new KeyboardEvent("keydown", {
    key: "Escape", code: "Escape", bubbles: true, cancelable: true
  }));
}

function firstLine(text) {
  return (text ?? "").trim().split("\n")[0].trim();
}

async function attachImages(composer, attachments) {
  const form = composer.closest("form");
  const baseline = countAttachmentPreviews(form);
  let input = findImageInput();
  if (!input) {
    const addButton = form?.querySelector('[data-testid="composer-plus-btn"]') ??
      document.querySelector('[data-testid="composer-plus-btn"]');
    if (addButton instanceof HTMLButtonElement) addButton.click();
    input = await waitFor(findImageInput, 3000,
      "ChatGPT did not expose an image upload control. Reload the linked ChatGPT tab and try again.");
  }

  const transfer = new DataTransfer();
  for (const attachment of attachments) {
    const binary = atob(attachment.base64);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
    transfer.items.add(new File([bytes], attachment.fileName, { type: attachment.mimeType }));
  }

  input.files = transfer.files;
  input.dispatchEvent(new Event("change", { bubbles: true }));
  await waitFor(() => hasAttachmentPreviews(form, attachments, baseline), 30000,
    "ChatGPT did not attach the image. The request was not sent; retry after reloading the linked tab.");
  await waitFor(() => !form?.querySelector('[role="progressbar"], [aria-label*="Uploading" i]'),
    30000, "ChatGPT did not finish uploading the image. The request was not sent.");
}

function findImageInput() {
  const inputs = [...document.querySelectorAll('input[type="file"]')];
  return inputs.find(input => input.id === "upload-files" && !input.disabled) ??
    inputs.find(input => input.id === "upload-photos" && !input.disabled) ??
    inputs.find(input => !input.disabled && (!input.accept || input.accept.includes("image/"))) ?? null;
}

function countAttachmentPreviews(form) {
  return form?.querySelectorAll('img, [data-testid*="attachment"], [data-testid*="file-thumbnail"]').length ?? 0;
}

function hasAttachmentPreviews(form, attachments, baseline) {
  if (!form) return false;
  const labels = [...form.querySelectorAll('[aria-label], [title]')]
    .map(element => `${element.getAttribute("aria-label") ?? ""} ${element.getAttribute("title") ?? ""}`)
    .join(" ");
  if (attachments.every(attachment =>
    form.textContent.includes(attachment.fileName) || labels.includes(attachment.fileName))) return true;
  return countAttachmentPreviews(form) >= baseline + attachments.length;
}

/** The first composer on screen, else the first one at all; the page is searched only as far as needed. */
function findComposer() {
  let hidden = null;
  for (const selector of COMPOSERS) {
    const composer = document.querySelector(selector);
    if (composer?.getClientRects().length) return composer;
    hidden ??= composer;
  }
  return hidden;
}

function composerForm() {
  return findComposer()?.closest("form") ?? null;
}

function composerText(composer) {
  return composer instanceof HTMLTextAreaElement ? composer.value : composer.innerText ?? "";
}

function setComposerText(composer, text) {
  composer.focus();
  if (composer instanceof HTMLTextAreaElement) {
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")?.set;
    setter?.call(composer, text);
    composer.dispatchEvent(new Event("input", { bubbles: true }));
    return;
  }

  composer.replaceChildren();
  const lines = text.split("\n");
  lines.forEach((line, index) => {
    if (index > 0) composer.append(document.createElement("br"));
    composer.append(document.createTextNode(line));
  });
  composer.dispatchEvent(new InputEvent("input", {
    bubbles: true,
    inputType: "insertText",
    data: text
  }));
}

function findEnabledSendButton() {
  let button = null;
  for (const selector of SEND_BUTTONS) {
    button = document.querySelector(selector);
    if (button) break;
  }
  // The new composer keeps one submit button that is only aria-disabled and turns into Stop while answering.
  return button instanceof HTMLButtonElement && !button.disabled &&
    button.getAttribute("aria-disabled") !== "true" && !isStopState(button) ? button : null;
}

/**
 * ChatGPT's Stop button while it writes. Only one the user can see counts: some composers keep a hidden Stop around
 * after the answer. The composer's form is searched when known: a long chat holds tens of thousands of nodes.
 */
function findStopButton(form) {
  const root = form?.isConnected ? form : document;
  for (const button of root.querySelectorAll(STOP_BUTTONS)) {
    if (isStopButton(button) && isShown(button)) return button;
  }
  return null;
}

function isStopButton(button) {
  if (button.dataset.testid === "stop-button" || isStopState(button)) return true;
  // A button labelled Stop, but not one that stops dictation, voice mode or reading aloud.
  const label = button.getAttribute("aria-label") ?? "";
  return /stop|останов/i.test(label) && !/dictat|диктов|voice|голос|record|запис|read|чтени|озвуч/i.test(label);
}

function isStopState(button) {
  const stopLabel = button.getAttribute("data-stop-label");
  return Boolean(stopLabel) && button.getAttribute("aria-label") === stopLabel;
}

function isShown(element) {
  return element.checkVisibility?.({ opacityProperty: true, visibilityProperty: true }) ??
    element.getClientRects().length > 0;
}

/**
 * ChatGPT's signs of where the answer stands: its Stop button or streaming marks while it writes, the finished
 * turn's action buttons and the new UI's completion flag when it is done.
 */
function answerSigns(response, turn, form) {
  return {
    stop: findStopButton(form),
    streaming: Boolean(response?.matches(STREAMING) || response?.querySelector(STREAMING)),
    actions: answerActions(turn).length > 0,
    flagged: Boolean(response?.closest("[data-message-complete]"))
  };
}

/** The action buttons of the answer's turn: those of a prompt in the same turn are not the answer's. */
function answerActions(turn) {
  return turn ? [...turn.querySelectorAll(TURN_ACTIONS)].filter(button => !button.closest(USER_MESSAGES)) : [];
}

/** The last element under `root` that a selector matches; a selector list matches each element once, in page order. */
function lastMatch(root, selector) {
  const matches = root.querySelectorAll(selector);
  return matches[matches.length - 1] ?? null;
}

/**
 * The element that holds the turns: the nearest one around both the last prompt and the last answer, or around the
 * turn that holds them both (the newest UI), where the next turn will land.
 */
function findThread() {
  const answer = lastMatch(document, ASSISTANT_MESSAGES);
  const prompt = lastMatch(document, USER_MESSAGES);
  const ancestor = answer && prompt ? commonAncestor(answer, prompt) : null;
  return ancestor?.closest(TURNS)?.parentElement ?? ancestor;
}

function commonAncestor(first, second) {
  let ancestor = first.parentElement;
  while (ancestor && !ancestor.contains(second)) ancestor = ancestor.parentElement;
  return ancestor;
}

/** The last assistant message at the end of the thread, where new turns land: only its last children are searched. */
function latestAssistantMessage(thread) {
  if (!thread?.isConnected) return null;
  let child = thread.lastElementChild;
  for (let looked = 0; child && looked < 4; looked++, child = child.previousElementSibling) {
    const message = lastMatch(child, ASSISTANT_MESSAGES) ?? (child.matches(ASSISTANT_MESSAGES) ? child : null);
    if (message) return message;
  }
  return null;
}

// Message ids survive re-renders, so an old answer that React re-mounts is not mistaken for the new one.
function messageKey(element) {
  return element.getAttribute("data-message-id") ??
    (element.matches("li[data-message-role]") ? element.id || null : null) ??
    element.getAttribute("data-chatgpt-search-message-ids")?.split(" ")[0] ??
    element.querySelector("[data-message-id]")?.getAttribute("data-message-id") ?? null;
}

function follows(first, second) {
  return Boolean(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING);
}

/**
 * The chat's message with the job's prompt: a user message new since LINK looked before sending, the one that holds
 * the prompt's words or else the only new one, wherever the chat put it and however it renders it. Picked up after a
 * reload, it is the message seen as the prompt before, or the newest one with its words after those from before it.
 */
function findPromptMessage(job) {
  // Searched from the end, where a new prompt usually is. A count of messages would not do to skip the search: a chat
  // that keeps only its latest messages on the page drops one as it adds one.
  const users = document.querySelectorAll(USER_MESSAGES);
  const fresh = new Map();
  for (let index = users.length - 1; index >= 0; index--) {
    const user = users[index];
    const key = messageKey(user);
    if (job.promptKey && key === job.promptKey) return user;
    if (job.baseline) {
      if (!isNewMessage(user, job.baseline)) continue;
      if (matchesPrompt(user, job)) return user;
      if (!fresh.has(key ?? user)) fresh.set(key ?? user, user);
    } else {
      if (key && key === job.lastUserKey) return null;
      if (matchesPrompt(user, job)) return user;
    }
  }
  return fresh.size === 1 ? fresh.values().next().value : null;
}

function matchesPrompt(message, job) {
  return Boolean(job.head) && looseText(message.textContent).includes(job.head);
}

/**
 * Whether a message answers the job's prompt: it is new since LINK sent the prompt and comes after it. Until the
 * prompt shows up, a new message counts only a while after sending, so that an older answer the chat re-renders
 * under a new id cannot pass for it.
 */
function isAnswerTo(message, job) {
  if (job.baseline && !isNewMessage(message, job.baseline)) return false;
  if (job.prompt?.isConnected) return follows(job.prompt, message);
  return Boolean(job.baseline) && Date.now() - job.sentAt >= PROMPT_GRACE_MS;
}

/**
 * The answer searched for on the whole page, from its end; the thread follows it, so the next looks find it cheaply.
 * In page order everything before the prompt is older than it, so the search stops there.
 */
function searchResponse(job) {
  const messages = document.querySelectorAll(ASSISTANT_MESSAGES);
  for (let index = messages.length - 1; index >= 0; index--) {
    const message = messages[index];
    if (isAnswerTo(message, job)) {
      const anchor = job.prompt?.isConnected ? job.prompt : job.baseline?.lastUser;
      if (anchor?.isConnected) job.thread = commonAncestor(message, anchor) ?? job.thread;
      return message;
    }
    if (job.prompt?.isConnected && follows(message, job.prompt)) break;
  }
  return null;
}

/**
 * The answer's turn: the largest element around it without the prompt, so the buttons in it are the answer's own.
 * Without the prompt, the turn ChatGPT marks; also when that turn holds the prompt and its answer (the newest UI),
 * whose buttons then sit around both.
 */
function turnOf(response, prompt) {
  const marked = () => response.closest(TURNS) ?? response;
  if (!prompt?.isConnected) return marked();
  const shared = response.closest(TURNS);
  if (shared?.contains(prompt) && shared.querySelectorAll(USER_MESSAGES).length === 1) return shared;
  let turn = response;
  while (turn.parentElement && turn.parentElement !== document.body && !turn.parentElement.contains(prompt)) {
    turn = turn.parentElement;
  }
  // A turn holding older prompts spans the chat before it, so its buttons are not the answer's.
  return turn.querySelector(USER_MESSAGES) ? marked() : turn;
}

/**
 * Waits for the answer without polling the page: a MutationObserver wakes the loop when ChatGPT changes something,
 * and while it does, the composer and the last turns are looked at every CHECK_INTERVAL_MS at most. The complete
 * answer is read once more at the end, the moment ChatGPT is done with it.
 */
async function watchAnswer(job) {
  const page = watchPage();
  job.wake = page.wake;
  const startedAt = Date.now();
  let bridgeAt = startedAt;
  // Events LINK does not take are tried again on the next look; only a long silence of LINK ends the wait.
  const tell = async (state, details) => {
    try {
      await report(job.requestId, state, details, 1);
      bridgeAt = Date.now();
      return true;
    } catch (error) {
      if (error instanceof EndedError || error instanceof RetiredError || Date.now() - bridgeAt > BRIDGE_TIMEOUT_MS) throw error;
      return false;
    }
  };

  try {
    let form = null;
    let text = "";
    let plain = "";
    let changedAt = startedAt;
    let readAt = 0;
    let readInterval = PROGRESS_INTERVAL_MS;
    let stale = true;
    let reported = "";
    let eventAt = startedAt;
    // A prompt picked up again first tells LINK where its answer stands.
    let told = !job.resumed;
    let promptLookAt = 0;
    let searchedAt = 0;

    while (true) {
      if (job.stopReason) throw job.stopReason;
      const now = Date.now();
      if (now - startedAt > RESPONSE_TIMEOUT_MS) throw new Error("Timed out waiting for ChatGPT to finish the response.");
      if (getConversationId() !== job.conversationId) {
        throw new Error("The linked ChatGPT tab switched to another conversation; the answer stays in that chat.");
      }
      stale = page.takeChanges() || stale;
      if (!form?.isConnected) form = composerForm();

      if (!job.prompt?.isConnected && now - promptLookAt >= 500) {
        promptLookAt = now;
        job.prompt = findPromptMessage(job);
        const key = job.prompt ? messageKey(job.prompt) : null;
        if (key && key !== job.promptKey) {
          job.promptKey = key;
          void saveRequestState(job.requestId, { phase: "sent", lastUserKey: job.lastUserKey, promptKey: key });
        }
      }

      // New turns land at the end of the thread; the whole page is searched only now and then, for a chat that put
      // them elsewhere, and in between the answer found last is kept.
      let response = latestAssistantMessage(job.thread);
      if (response && !isAnswerTo(response, job)) response = null;
      const searchInterval = job.prompt?.isConnected && job.thread?.contains(job.prompt)
        ? SEARCH_INTERVAL_IN_THREAD_MS
        : SEARCH_INTERVAL_MS;
      if (!response && now - searchedAt >= searchInterval) {
        searchedAt = now;
        response = searchResponse(job);
      }
      if (!response && job.response?.isConnected && isAnswerTo(job.response, job)) response = job.response;
      if (response !== job.response) {
        job.response = response;
        stale = true;
      }
      // Working the answer's turn out walks up the page and through the turn, so it is redone only when the answer or
      // the prompt changes.
      if (response !== job.turnFor || job.prompt !== job.turnPrompt || (response && !job.turn?.contains(response))) {
        job.turnFor = response;
        job.turnPrompt = job.prompt;
        const turn = response ? turnOf(response, job.prompt) : null;
        if (turn !== job.turn) {
          job.turn = turn;
          page.follow(turn);
        }
      }

      const signs = answerSigns(response, job.turn, form);
      const generating = Boolean(signs.stop || signs.streaming);
      if (response && stale && (!generating || now - readAt >= readInterval)) {
        stale = false;
        readAt = now;
        const started = performance.now();
        const read = readAnswer(response);
        // A long answer is converted less often, so the page keeps its time for ChatGPT.
        readInterval = Math.max(PROGRESS_INTERVAL_MS, (performance.now() - started) * 10);
        if (read.markdown !== text || read.plain !== plain) {
          text = read.markdown;
          plain = read.plain;
          changedAt = now;
        }
      }

      // A finished turn settles it even if some Stop-like button lingers. Otherwise only the answer's own text
      // completes it, never a status such as "Thinking", and only once ChatGPT has stopped writing.
      const answered = Boolean(text || (plain && !isStatusLine(plain)));
      const settle = text && !document.hidden ? SETTLE_MS : LONG_SETTLE_MS;
      let reason = null;
      if (response && signs.actions) reason = "turn actions";
      else if (answered && !generating && signs.flagged) reason = "completion flag";
      else if (answered && !generating && now - changedAt >= settle) reason = text ? "settled" : "settled without Markdown";
      else if (text && now - changedAt >= STALL_MS && now - page.turnChangedAt() >= STALL_MS) reason = "unchanged for a minute";
      if (reason) return finishAnswer(job, response, reason, text || plain);

      if (!job.resumed && !job.prompt && !response && !signs.stop && now - job.sentAt >= SEND_CONFIRM_MS) {
        const composer = findComposer();
        if (composer && job.head && looseText(composerText(composer)).includes(job.head)) {
          throw new Error("ChatGPT did not take the prompt: it is still in the ChatGPT composer and was not sent.");
        }
      }
      if (job.resumed && !job.prompt && !response && now - startedAt >= RESUME_TIMEOUT_MS) {
        throw new Error("The prompt is not in the linked chat any more. Check chat reads the chat's last answer.");
      }

      if (text && text !== reported) {
        if (await tell("receiving", { text })) {
          reported = text;
          told = true;
          eventAt = now;
        }
      } else if (!told) {
        if (await tell("waiting")) {
          told = true;
          eventAt = now;
        }
      } else if (now - eventAt >= HEARTBEAT_INTERVAL_MS) {
        // A cancelled request answers 404, which ends the wait through EndedError.
        if (await tell("heartbeat")) eventAt = now;
      }

      let wait = HEARTBEAT_INTERVAL_MS - (Date.now() - eventAt);
      if (response && stale) wait = Math.min(wait, readAt + readInterval - now);
      if (answered && !generating) wait = Math.min(wait, changedAt + settle - now);
      if (!job.prompt || !response) wait = Math.min(wait, 500);
      await page.next(wait);
    }
  } finally {
    job.wake = null;
    page.disconnect();
  }
}

/** The complete answer, read once more; what LINK logs when ChatGPT's own signs of the end were missing. */
function finishAnswer(job, response, reason, seen) {
  const read = readAnswer(response);
  const text = read.markdown || read.plain || seen;
  if (!text) throw new Error("ChatGPT's answer has no text LINK can show. Look at the ChatGPT tab.");
  const usual = (reason === "turn actions" || reason === "completion flag" || reason === "settled") && read.markdown;
  return usual ? { text } : { text, diagnostics: JSON.stringify({ completedBy: reason, ...diagnose(job, response) }) };
}

function isStatusLine(text) {
  return text.length < 80 && STATUS_LINE.test(text);
}

function watchPage() {
  let changed = false;
  let wake = null;
  let turn = null;
  let turnChangedAt = Date.now();
  const observer = new MutationObserver(records => {
    changed = true;
    if (turn && records.some(record => turn.contains(record.target))) turnChangedAt = Date.now();
    wake?.();
  });
  // Text streams in as nodes and characters; Stop and ChatGPT's completion flags change attributes. Classes and styles
  // change all the time on the page (hover, animations) and would only wake the watch: a Stop hidden by them is seen
  // at the next heartbeat.
  observer.observe(document.body, {
    subtree: true,
    childList: true,
    characterData: true,
    attributes: true,
    attributeFilter: ["aria-label", "data-testid", "data-message-complete", "data-markdown-animated", "hidden"]
  });
  return {
    takeChanges() {
      const result = changed;
      changed = false;
      return result;
    },
    /** The answer's turn: how long it has not changed tells a finished answer whose Stop lingers. */
    follow(element) {
      turn = element;
      turnChangedAt = Date.now();
    },
    turnChangedAt: () => turnChangedAt,
    wake: () => wake?.(),
    // Until the page changes or `ms` pass, but never less than CHECK_INTERVAL_MS: a busy page costs a few looks a second.
    async next(ms) {
      await delay(CHECK_INTERVAL_MS);
      if (changed || ms <= CHECK_INTERVAL_MS) return;
      await new Promise(resolve => {
        wake = resolve;
        void delay(ms - CHECK_INTERVAL_MS).then(resolve);
      });
      wake = null;
    },
    disconnect() {
      observer.disconnect();
      wake?.();
    }
  };
}

/** The message's last Markdown root that is not inside another one: found from the end, the first such one. */
function markdownRoot(message) {
  const roots = message.querySelectorAll(MARKDOWN_ROOTS);
  for (let index = roots.length - 1; index >= 0; index--) {
    if (!roots[index].parentElement?.closest(MARKDOWN_ROOTS)) return roots[index];
  }
  return null;
}

/**
 * The answer as Markdown. ChatGPT renders Markdown to HTML; LINK needs the Markdown back, so code, lists and tables
 * keep their shape. Only rendered answer Markdown streams: status lines ("Thinking", "Думаю") and placeholders around
 * it are not the answer. A message without a Markdown root is read whole, as `plain`, for ChatGPT's end signs only.
 */
function readAnswer(message) {
  const root = markdownRoot(message);
  return root ? { markdown: toMarkdown(root), plain: "" } : { markdown: "", plain: toMarkdown(message) };
}

function toMarkdown(root) {
  const blocks = [];
  collectBlocks(root, blocks);
  return blocks.join("\n\n").trim();
}

function collectBlocks(parent, blocks) {
  let inline = "";
  const flush = () => {
    const text = inline.replace(/[ \t]*\n[ \t]*/g, "\n").trim();
    if (text) blocks.push(text);
    inline = "";
  };

  for (const node of parent.childNodes) {
    if (node.nodeType === Node.TEXT_NODE) {
      inline += collapseWhitespace(node.textContent);
      continue;
    }
    if (!(node instanceof Element) || node.matches(SKIPPED_NODES)) continue;
    if (INLINE_TAGS.has(node.tagName) && !node.classList.contains("katex-display")) {
      inline += renderInlineNode(node);
      continue;
    }

    flush();
    const block = renderBlock(node);
    if (block === null) collectBlocks(node, blocks);
    else if (block) blocks.push(block);
  }
  flush();
}

function renderBlock(element) {
  if (element.classList.contains("katex-display")) return `$$${readTex(element)}$$`;
  switch (element.tagName) {
    case "H1": case "H2": case "H3": case "H4": case "H5": case "H6":
      return `${"#".repeat(Number(element.tagName[1]))} ${renderInline(element).trim()}`;
    case "P":
      return renderInline(element).trim();
    case "PRE":
      return renderCode(element);
    case "UL": case "OL":
      return renderList(element, 0);
    case "BLOCKQUOTE":
      return toMarkdown(element).split("\n").map(line => `> ${line}`.trimEnd()).join("\n");
    case "TABLE":
      return renderTable(element);
    case "HR":
      return "---";
    default:
      return null;
  }
}

function renderInline(parent) {
  let output = "";
  for (const node of parent.childNodes) output += renderInlineNode(node);
  return output;
}

function renderInlineNode(node) {
  if (node.nodeType === Node.TEXT_NODE) return collapseWhitespace(node.textContent);
  if (!(node instanceof Element) || node.matches(SKIPPED_NODES)) return "";
  if (node.classList.contains("katex-display")) return `\n$$${readTex(node)}$$\n`;
  if (node.classList.contains("katex")) return `$${readTex(node)}$`;
  switch (node.tagName) {
    case "BR":
      return "\n";
    case "CODE":
      return node.textContent.includes("`") ? node.textContent : `\`${node.textContent}\``;
    case "STRONG": case "B":
      return wrap(renderInline(node), "**");
    case "A":
      return renderLink(node);
    case "IMG":
      return node.getAttribute("alt") ?? "";
    default:
      // Italics and other inline styles stay plain: LINK renders code, bold and links only.
      return renderInline(node);
  }
}

function renderCode(pre) {
  const code = pre.querySelector("code");
  const language = [...(code?.classList ?? [])].find(name => name.startsWith("language-"))?.slice(9) ?? "";
  const text = (code ?? pre).textContent.replace(/\n+$/, "");
  return "```" + language + "\n" + text + "\n```";
}

function renderList(list, depth) {
  const ordered = list.tagName === "OL";
  let number = Number(list.getAttribute("start")) || 1;
  const indent = "  ".repeat(depth);
  const lines = [];
  for (const item of list.children) {
    if (item.tagName !== "LI") continue;
    const marker = ordered ? `${number++}. ` : "- ";
    let text = "";
    const nested = [];
    for (const node of item.childNodes) {
      if (node instanceof Element && (node.tagName === "UL" || node.tagName === "OL")) {
        nested.push(renderList(node, depth + 1));
      } else if (node instanceof Element && node.tagName === "PRE") {
        nested.push(renderCode(node).split("\n").map(line => `${indent}  ${line}`).join("\n"));
      } else if (node instanceof Element && node.tagName === "P") {
        text += ` ${renderInline(node)}`;
      } else {
        text += renderInlineNode(node);
      }
    }
    lines.push(`${indent}${marker}${text.replace(/\s*\n\s*/g, " ").trim()}`);
    lines.push(...nested);
  }
  return lines.join("\n");
}

function renderTable(table) {
  const rows = [...table.querySelectorAll("tr")].map(row =>
    [...row.children].map(cell => renderInline(cell).replace(/\s+/g, " ").trim().replace(/\|/g, "\\|")));
  if (rows.length === 0) return "";
  const width = Math.max(...rows.map(row => row.length));
  const line = cells => `| ${[...cells, ...Array(width - cells.length).fill("")].join(" | ")} |`;
  return [line(rows[0]), line(Array(width).fill("---")), ...rows.slice(1).map(line)].join("\n");
}

function renderLink(anchor) {
  const text = renderInline(anchor).trim();
  if (!text) return "";
  const href = anchor.href;
  return /^https?:/i.test(href) && href !== text
    ? `[${text.replace(/[[\]]/g, "")}](${href.replace(/\)/g, "%29")})`
    : text;
}

function readTex(element) {
  return (element.querySelector('annotation[encoding="application/x-tex"]')?.textContent ?? element.textContent).trim();
}

function wrap(text, marker) {
  const trimmed = text.trim();
  if (!trimmed) return text;
  return `${text.match(/^\s*/)[0]}${marker}${trimmed}${marker}${text.match(/\s*$/)[0]}`;
}

function collapseWhitespace(text) {
  return text.replace(/\s+/g, " ");
}

function normalize(text) {
  return (text ?? "").replace(/\s+/g, " ").trim();
}

/** Letters and digits only: however a chat renders text (Markdown, typographic dashes, line breaks), its words stay. */
function looseText(text) {
  return (text ?? "").toLowerCase().replace(/[^\p{L}\p{N}]+/gu, "");
}

/**
 * Tells LINK about a request. The service worker may be starting or LINK busy for a moment, so a failed event is
 * sent again up to `attempts` times; a request LINK has dropped ends the caller through EndedError.
 */
async function report(requestId, state, details = {}, attempts = 3) {
  for (let attempt = 1; ; attempt++) {
    if (retired || !chrome.runtime?.id) throw new RetiredError("The extension was reloaded.");
    let result;
    try {
      result = await chrome.runtime.sendMessage({ type: "link-event", event: { requestId, state, ...details } });
    } catch (error) {
      result = { ok: false, error: error instanceof Error ? error.message : String(error) };
    }
    if (result?.ok) return;
    if (result?.status === 404) throw new EndedError(result.reason ?? "unknown");
    if (attempt >= attempts) throw new Error(result?.error ?? "LINK bridge did not accept the event.");
    await delay(400 * attempt);
  }
}

function reportQuietly(requestId, state, details) {
  return report(requestId, state, details).catch(() => {
    // LINK may have stopped waiting, or closed.
  });
}

// Where a prompt stands, kept outside the page's memory: a copy started after an extension or page reload picks the
// prompt up from here instead of sending it again.
async function loadRequestState(requestId) {
  try {
    return (await chrome.storage.local.get(`request:${requestId}`))[`request:${requestId}`] ?? null;
  } catch {
    return null;
  }
}

async function saveRequestState(requestId, state) {
  try {
    await chrome.storage.local.set({ [`request:${requestId}`]: { ...state, savedAt: Date.now() } });
  } catch {
    // Without storage a reloaded page finds the prompt by its text.
  }
}

async function forgetRequestState(requestId) {
  try {
    await chrome.storage.local.remove(`request:${requestId}`);
  } catch {
    // Old entries are pruned when a copy starts.
  }
}

async function pruneRequestStates() {
  try {
    const saved = await chrome.storage.local.get(null);
    const old = Object.entries(saved)
      .filter(([key, state]) => key.startsWith("request:") && !(Date.now() - (state?.savedAt ?? 0) < REQUEST_STATE_TTL_MS))
      .map(([key]) => key);
    if (old.length) await chrome.storage.local.remove(old);
  } catch {
    // Pruning is housekeeping only.
  }
}

async function waitFor(producer, timeoutMs, errorMessage) {
  const startedAt = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    const value = producer();
    if (value) return value;
    await delay(100);
  }
  // A throttled timer can oversleep the whole timeout, so look once more before giving up.
  const value = producer();
  if (value || errorMessage === null) return value || null;
  throw new Error(errorMessage);
}

function getConversationId() {
  return location.pathname.match(/\/c\/([^/?#]+)/)?.[1] ?? null;
}

function getConversationTitle() {
  return document.title
    .replace(/\s*[—-]\s*ChatGPT\s*$/i, "")
    .replace(/^ChatGPT\s*[—-]\s*/i, "")
    .trim() || "ChatGPT conversation";
}

function delay(milliseconds) {
  return new Promise(resolve => {
    const entry = { deadline: Date.now() + milliseconds, resolve };
    pendingDelays.add(entry);
    setTimeout(() => settleDelay(entry), milliseconds);
  });
}

function settleDelay(entry) {
  if (pendingDelays.delete(entry)) entry.resolve();
}

function flushDelays() {
  const now = Date.now();
  for (const entry of [...pendingDelays]) {
    if (entry.deadline <= now) settleDelay(entry);
  }
}

void pruneRequestStates();
announceConversation();
announceTimer = setInterval(announceConversation, ANNOUNCE_INTERVAL_MS);
})();
