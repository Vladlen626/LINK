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
const PLAIN_SETTLE_MS = 3000;
// An answer that has not changed at all for this long is complete even if a Stop-like button lingers.
const STALL_MS = 60 * 1000;
// A prompt still in the composer this long after Send was not taken by ChatGPT.
const SEND_CONFIRM_MS = 20 * 1000;
// A prompt picked up again (after a reload) that the chat does not show by then has gone from the page.
const RESUME_TIMEOUT_MS = 20 * 1000;
// LINK counts as gone when no event of a prompt has reached it for this long.
const BRIDGE_TIMEOUT_MS = 30 * 1000;
const REQUEST_STATE_TTL_MS = 60 * 60 * 1000;
const MARKDOWN_ROOTS = "[data-assistant-markdown], .markdown, [class*='markdown']";
const ASSISTANT_MESSAGES = '[data-message-author-role="assistant"], article[data-turn="assistant"], li[data-message-role="assistant"]';
const USER_MESSAGES = '[data-message-author-role="user"], article[data-turn="user"], li[data-message-role="user"]';
const TURNS = "article, [data-turn-id], [data-testid^='conversation-turn']";
// ChatGPT gives a finished turn its action buttons (copy, rate, read aloud…); a turn still being written has none.
const TURN_ACTIONS = '[data-testid$="turn-action-button"]';
const STREAMING = '[data-message-streaming]:not([data-message-streaming="false"]), .result-streaming';
const STOP_BUTTONS = 'button[data-testid="stop-button"], button[data-composer-submit], ' +
  'button[aria-label*="stop" i], button[aria-label*="останов" i]';
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
const PICKER_BUTTONS = 'button[aria-haspopup], [role="combobox"]';
const MENUS = '[role="menu"], [role="listbox"], [data-radix-popper-content-wrapper]';
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
    // How the prompt starts, to find it among the chat's messages.
    head: normalize(request.prompt.split("\n").find(line => line.trim()) ?? "").slice(0, 80),
    stopReason: null,
    wake: null,
    resumed: false,
    sentAt: 0,
    baseline: null,
    lastUserKey: null,
    prompt: null,
    promptKey: null,
    thread: null,
    response: null,
    turn: null
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
    if (error instanceof EndedError) {
      // Stop/Esc in LINK stops ChatGPT too; an answer LINK has taken otherwise is left alone.
      if (error.reason === "cancelled") findStopButton(composerForm())?.click();
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
  await applyEffort(request, composer.closest("form"));
  if (request.attachments?.length) await attachImages(composer, request.attachments);
  setComposerText(composer, request.prompt);
  job.baseline = takeBaseline();
  job.lastUserKey = job.baseline.lastUser ? messageKey(job.baseline.lastUser) : null;
  job.thread = findThread();
  await saveRequestState(request.requestId, { phase: "sending", lastUserKey: job.lastUserKey });

  const sendButton = await waitFor(findEnabledSendButton, 15000, "ChatGPT Send button did not become available.");
  // A copy replaced meanwhile sends nothing: the newer one does.
  if (job.stopReason) throw job.stopReason;
  if (!chrome.runtime?.id) throw new RetiredError("The extension was reloaded.");
  sendButton.click();
  job.sentAt = Date.now();
  await saveRequestState(request.requestId, { phase: "sent", lastUserKey: job.lastUserKey });
  await report(request.requestId, "waiting");
}

function takeBaseline() {
  const messages = getAssistantMessages(document);
  const last = messages.at(-1) ?? null;
  return {
    keys: new Set(messages.map(messageKey).filter(Boolean)),
    last,
    text: last ? readAnswer(last).markdown : "",
    lastUser: getUserMessages(document).at(-1) ?? null
  };
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
  const prompt = job ? (job.prompt?.isConnected ? job.prompt : null) : getUserMessages(document).at(-1) ?? null;
  const latest = getAssistantMessages(document).at(-1) ?? null;
  const response = latest && (job ? prompt && follows(prompt, latest) : !prompt || follows(prompt, latest)) ? latest : null;
  const signs = answerSigns(response, response ? turnOf(response, prompt) : null, composerForm());
  const read = response ? readAnswer(response) : { markdown: "", plain: "" };
  const generating = !signs.actions && Boolean(signs.stop || signs.streaming);
  return {
    answerTo: job && prompt ? job.requestId : null,
    text: read.markdown || (!generating && !isStatusLine(read.plain) ? read.plain : ""),
    generating,
    diagnostics: JSON.stringify(diagnose(job, response))
  };
}

function diagnose(job, response = job?.response) {
  const form = composerForm();
  const answer = response?.isConnected ? response : null;
  const turn = answer ? turnOf(answer, job?.prompt) : null;
  const read = answer ? readAnswer(answer) : null;
  return {
    version: VERSION,
    busy: current?.requestId ?? null,
    resumed: job?.resumed ?? null,
    prompt: job ? (job.prompt?.isConnected ? messageKey(job.prompt) ?? "found" : "not found") : null,
    answer: answer ? {
      tag: answer.tagName.toLowerCase(),
      id: messageKey(answer),
      markdownRoots: markdownRoots(answer).length,
      markdownLength: read.markdown.length,
      plainLength: read.plain.length,
      complete: Boolean(answer.closest("[data-message-complete]")),
      streaming: Boolean(answer.matches(STREAMING) || answer.querySelector(STREAMING)),
      turnButtons: [...turn.querySelectorAll("button[data-testid]")].map(button => button.dataset.testid).slice(0, 12)
    } : null,
    stopButtons: [...(form ?? document).querySelectorAll(STOP_BUTTONS)].slice(0, 6).map(button =>
      `${button.dataset.testid ?? ""}|${button.getAttribute("aria-label") ?? ""}|${isShown(button) ? "shown" : "hidden"}`),
    picker: pickerLabel(findModePicker(form))
  };
}

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
  if (effort === "normal" && applied === "normal") return;

  const own = effort === "normal" ? saved[ownKey] : null;
  const targets = own ? [modeOf(own) ?? own, ...EFFORT_MODES.normal] : EFFORT_MODES[effort];
  const result = await switchChatMode(form, targets);
  if (!result.ok) {
    await report(request.requestId, "notice", { notice: `ChatGPT mode not switched (${result.reason}); the answer hint was still sent` });
    return;
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
  const opened = () => {
    const id = picker.getAttribute("aria-controls");
    const own = id ? document.getElementById(id) : null;
    if (own) return own;
    const menus = [...document.querySelectorAll(MENUS)];
    return menus.find(menu => !before.has(menu)) ??
      (picker.getAttribute("aria-expanded") === "true" ? menus.find(isShown) : null) ?? null;
  };
  // Radix menus open on pointerdown, and a synthetic click alone does not open them; other menus open on click or Enter.
  const ways = [
    () => picker.dispatchEvent(new PointerEvent("pointerdown", {
      bubbles: true, cancelable: true, button: 0, pointerType: "mouse", isPrimary: true
    })),
    () => picker.click(),
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
  closeMenu(picker);
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

function closeMenu(menu) {
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

function findComposer() {
  const selectors = [
    "#prompt-textarea",
    'textarea[data-testid="prompt-textarea"]',
    'textarea[name="prompt"]',
    "[data-mobile-composer-prompt]",
    'textarea[placeholder*="Message"]',
    '[contenteditable="true"][data-virtualkeyboard]'
  ];
  const candidates = selectors.map(selector => document.querySelector(selector)).filter(Boolean);
  return candidates.find(element => element.getClientRects().length > 0) ?? candidates[0] ?? null;
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
  const selectors = [
    'button[data-testid="send-button"]',
    "button[data-composer-submit]",
    'button[aria-label="Send prompt"]',
    'form button[type="submit"]'
  ];
  const button = selectors.map(selector => document.querySelector(selector)).find(Boolean);
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
    actions: Boolean(turn?.querySelector(TURN_ACTIONS)),
    flagged: Boolean(response?.closest("[data-message-complete]"))
  };
}

function getAssistantMessages(root) {
  return [...new Set(root.querySelectorAll(ASSISTANT_MESSAGES))];
}

function getUserMessages(root) {
  return [...new Set(root.querySelectorAll(USER_MESSAGES))];
}

/** The element that holds the turns: the nearest one around both the last prompt and the last answer. */
function findThread() {
  const answer = getAssistantMessages(document).at(-1);
  const prompt = getUserMessages(document).at(-1);
  return answer && prompt ? commonAncestor(answer, prompt) : null;
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
    const message = getAssistantMessages(child).at(-1) ?? (child.matches(ASSISTANT_MESSAGES) ? child : null);
    if (message) return message;
  }
  return null;
}

// Message ids survive re-renders, so an old answer that React re-mounts is not mistaken for the new one.
function messageKey(element) {
  return element.getAttribute("data-message-id") ??
    (element.matches("li[data-message-role]") ? element.id || null : null) ??
    element.querySelector("[data-message-id]")?.getAttribute("data-message-id") ?? null;
}

function follows(first, second) {
  return Boolean(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING);
}

/** The chat's message with the job's prompt: the newest user message sent after the messages LINK saw before. */
function findPromptMessage(job) {
  const users = getUserMessages(document);
  for (let index = users.length - 1; index >= Math.max(0, users.length - 6); index--) {
    const user = users[index];
    const key = messageKey(user);
    if (job.promptKey && key === job.promptKey) return user;
    if (user === job.baseline?.lastUser || (key && key === job.lastUserKey)) return null;
    if (job.head && normalize(user.textContent).includes(job.head)) return user;
  }
  return null;
}

/** Whether a message answers the job's prompt: it comes after the prompt, or is new since LINK sent it. */
function isAnswerTo(message, job) {
  if (job.prompt?.isConnected) return follows(job.prompt, message);
  if (!job.baseline) return false;
  const key = messageKey(message);
  if (key) return !job.baseline.keys.has(key);
  return message !== job.baseline.last && readAnswer(message).markdown !== job.baseline.text;
}

/** The answer searched for on the whole page; the thread follows it, so the next looks find it cheaply. */
function searchResponse(job) {
  const latest = getAssistantMessages(document).at(-1);
  if (!latest || !isAnswerTo(latest, job)) return null;
  const anchor = job.prompt?.isConnected ? job.prompt : job.baseline?.last;
  if (anchor?.isConnected) job.thread = commonAncestor(latest, anchor) ?? job.thread;
  return latest;
}

/**
 * The answer's turn: the largest element around it without the prompt, so the buttons in it are the answer's own.
 * Without the prompt, the turn ChatGPT marks.
 */
function turnOf(response, prompt) {
  const marked = () => response.closest(TURNS) ?? response;
  if (!prompt?.isConnected) return marked();
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
      if (!response && now - searchedAt >= 1000) {
        searchedAt = now;
        response = searchResponse(job);
      }
      if (!response && job.response?.isConnected && isAnswerTo(job.response, job)) response = job.response;
      if (response !== job.response) {
        job.response = response;
        stale = true;
      }
      const turn = response ? turnOf(response, job.prompt) : null;
      if (turn !== job.turn) {
        job.turn = turn;
        page.follow(turn);
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
      let reason = null;
      const settle = document.hidden ? PLAIN_SETTLE_MS : SETTLE_MS;
      if (response && signs.actions) reason = "turn actions";
      else if ((text || plain) && !generating && signs.flagged) reason = "completion flag";
      else if (text && !generating && now - changedAt >= settle) reason = "settled";
      else if (plain && !generating && now - changedAt >= PLAIN_SETTLE_MS && !isStatusLine(plain)) reason = "settled without Markdown";
      else if (text && now - changedAt >= STALL_MS && now - page.turnChangedAt() >= STALL_MS) reason = "unchanged for a minute";
      if (reason) return finishAnswer(job, response, reason, text || plain);

      if (!job.resumed && !job.prompt && !response && !signs.stop && now - job.sentAt >= SEND_CONFIRM_MS) {
        const composer = findComposer();
        if (composer && job.head && normalize(composerText(composer)).includes(job.head)) {
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
      if ((text || plain) && !generating) wait = Math.min(wait, changedAt + (text ? settle : PLAIN_SETTLE_MS) - now);
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
  // Text streams in as nodes and characters; Stop, its visibility and ChatGPT's completion flag change attributes.
  observer.observe(document.body, {
    subtree: true,
    childList: true,
    characterData: true,
    attributes: true,
    attributeFilter: ["aria-label", "data-testid", "data-message-complete", "class", "hidden", "style"]
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

function markdownRoots(message) {
  return [...message.querySelectorAll(MARKDOWN_ROOTS)].filter(root => !root.parentElement?.closest(MARKDOWN_ROOTS));
}

/**
 * The answer as Markdown. ChatGPT renders Markdown to HTML; LINK needs the Markdown back, so code, lists and tables
 * keep their shape. Only rendered answer Markdown streams: status lines ("Thinking", "Думаю") and placeholders around
 * it are not the answer. A message without a Markdown root is read whole, as `plain`, for ChatGPT's end signs only.
 */
function readAnswer(message) {
  const roots = markdownRoots(message);
  return roots.length ? { markdown: toMarkdown(roots.at(-1)), plain: "" } : { markdown: "", plain: toMarkdown(message) };
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
