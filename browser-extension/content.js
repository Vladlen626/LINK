(() => {
const ANNOUNCE_INTERVAL_MS = 2500;
const RESPONSE_TIMEOUT_MS = 10 * 60 * 1000;
const HEARTBEAT_INTERVAL_MS = 2000;
// ChatGPT re-renders the answer on every token. While the page changes, LINK looks at it at most this often, and
// only at the composer and the last turns, so even a long chat stays cheap...
const CHECK_INTERVAL_MS = 150;
// ...and turns the growing answer into Markdown at most this often, for the overlay and reading aloud.
const PROGRESS_INTERVAL_MS = 250;
// Without ChatGPT's own sign of completion, a quiet period after its Stop button disappears ends the answer.
const SETTLE_MS = 1200;
const MARKDOWN_ROOTS = "[data-assistant-markdown], .markdown, [class*='markdown']";
const ASSISTANT_MESSAGES = '[data-message-author-role="assistant"], article[data-turn="assistant"], li[data-message-role="assistant"]';
const USER_MESSAGES = '[data-message-author-role="user"], article[data-turn="user"], li[data-message-role="user"]';
// LINK's Fast / Normal / Deep on ChatGPT's mode picker. Since June 2026 it sits in the composer and offers Instant,
// Medium, High and Extra High; the older header picker offered Instant, Auto and Thinking. Labels are matched by
// their first word, so "Extra High" / "Очень высокий" never pass for High.
const EFFORT_MODES = {
  fast: { label: "Instant", patterns: [/^(instant|мгновен|быстр)/i] },
  normal: { label: "Medium", patterns: [/^(medium|средн|auto|авто)/i] },
  deep: { label: "High", patterns: [/^(high|высок|thinking|думающ)/i] }
};
const MODE_LABELS = /instant|medium|high|auto|thinking|мгновен|средн|высок|авто|думающ/i;
const EXCLUDED_MODES = /\bpro\b|upgrade|legacy|улучш|устаревш/i;
const SKIPPED_NODES = 'button, svg, style, script, [hidden], [aria-hidden="true"], .sr-only, ' +
  "[data-message-content-controls], [data-message-actions], [data-message-attribution], [data-assistant-placeholder-slot]";
const INLINE_TAGS = new Set(["A", "ABBR", "B", "BR", "CODE", "DEL", "EM", "I", "IMG", "KBD", "MARK", "S", "SMALL", "SPAN", "STRONG", "SUB", "SUP", "U"]);
const pendingDelays = new Set();
let processingRequestId = null;
let announceTimer = null;

class CancelledError extends Error {}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "link-discover") {
    sendResponse({ conversation: getConversation() });
    announceConversation();
    return;
  }
  if (message?.type === "link-request") {
    sendResponse({ accepted: true });
    // "Check chat" only reads the page, so it works while an answer is still awaited, or stuck.
    if (message.request?.kind === "read") void handleRead(message.request);
    else void handleRequest(message.request);
    return;
  }
  if (message?.type === "link-tick") {
    // Hidden tabs get their timers throttled down to once a minute; the service worker's ticks keep waits on time.
    flushDelays();
  }
});

function announceConversation() {
  if (!chrome.runtime?.id) {
    // The extension was reloaded; this orphaned copy can no longer reach it.
    clearInterval(announceTimer);
    return;
  }
  const conversation = getConversation();
  if (!conversation) return;
  void chrome.runtime.sendMessage({ type: "link-announce", conversation }).catch(() => {});
}

function getConversation() {
  const conversationId = getConversationId();
  return conversationId ? { conversationId, title: getConversationTitle(), url: location.href } : null;
}

async function handleRequest(request) {
  if (processingRequestId) {
    await report(request.requestId, "error", { error: "This ChatGPT tab is already processing another LINK request." })
      .catch(() => {});
    return;
  }

  if (getConversationId() !== request.conversationId) {
    await report(request.requestId, "error", { error: "The linked ChatGPT tab navigated to another conversation." })
      .catch(() => {});
    return;
  }

  processingRequestId = request.requestId;
  try {
    await report(request.requestId, "sending");
    await applyEffort(request);
    const composer = await waitFor(findComposer, 15000, "ChatGPT message composer was not found.");
    if (request.attachments?.length) await attachImages(composer, request.attachments);
    setComposerText(composer, request.prompt);
    const messages = getAssistantMessages(document);
    const last = messages.at(-1) ?? null;
    const baseline = {
      keys: new Set(messages.map(messageKey).filter(Boolean)),
      last,
      text: last ? readAssistantMarkdown(last) : ""
    };

    const sendButton = await waitFor(findEnabledSendButton, 15000, "ChatGPT Send button did not become available.");
    sendButton.click();
    await report(request.requestId, "waiting");
    const response = await waitForAssistantResponse(request.requestId, baseline);
    await report(request.requestId, "completed", { text: response });
  } catch (error) {
    if (error instanceof CancelledError) {
      // LINK no longer waits for this answer (Stop/Esc): free the chat for the next request.
      findStopButton(findComposer()?.closest("form"))?.click();
      return;
    }
    try {
      await report(request.requestId, "error", { error: error instanceof Error ? error.message : String(error) });
    } catch {
      // LINK may have closed while the ChatGPT request was in progress.
    }
  } finally {
    processingRequestId = null;
  }
}

async function handleRead(request) {
  try {
    await report(request.requestId, "snapshot", readSnapshot());
  } catch {
    // LINK may have stopped waiting for the check.
  }
}

/**
 * The answer to the last prompt as the page shows it now, whether ChatGPT is still writing it, and what LINK's
 * selectors see, so a missed answer can be taken over and its cause found in link.log.
 */
function readSnapshot() {
  const answer = getAssistantMessages(document).at(-1) ?? null;
  const prompt = [...document.querySelectorAll(USER_MESSAGES)].at(-1) ?? null;
  // An answer counts only after the last prompt; before that ChatGPT has not answered it yet.
  const current = answer && (!prompt || prompt.compareDocumentPosition(answer) & Node.DOCUMENT_POSITION_FOLLOWING)
    ? answer
    : null;
  const form = findComposer()?.closest("form") ?? null;
  const generating = isGenerating(form, current);
  const finished = current ? hasTurnActions(current) : false;
  const text = current ? readAssistantMarkdown(current) || (finished ? toMarkdown(current) : "") : "";
  return { text, generating: generating && !finished, diagnostics: JSON.stringify(diagnose(current, form)) };
}

function diagnose(answer, form) {
  const turn = answer?.closest("article, [data-turn-id]");
  return {
    version: chrome.runtime.getManifest().version,
    waiting: processingRequestId !== null,
    answer: answer ? {
      tag: answer.tagName.toLowerCase(),
      id: messageKey(answer),
      markdownRoots: answer.querySelectorAll(MARKDOWN_ROOTS).length,
      markdownLength: readAssistantMarkdown(answer).length,
      textLength: (answer.innerText ?? "").length,
      complete: Boolean(answer.closest("[data-message-complete]")),
      turnButtons: [...(turn?.querySelectorAll("button[data-testid]") ?? [])].map(button => button.dataset.testid).slice(0, 12)
    } : null,
    stopButton: Boolean(findStopButton(form)),
    streaming: Boolean(answer?.querySelector("[data-message-streaming], .result-streaming")),
    stopLabels: [...(form ?? document).querySelectorAll("button[aria-label]")]
      .map(button => button.getAttribute("aria-label"))
      .filter(label => /stop|останов/i.test(label)),
    picker: firstLine(findModePicker()?.innerText)
  };
}

async function applyEffort(request) {
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
  const mode = own
    ? { label: own, patterns: [new RegExp(`^${escapeRegExp(own)}$`, "i"), ...EFFORT_MODES.normal.patterns] }
    : EFFORT_MODES[effort];
  const result = await switchChatMode(mode);
  if (!result.ok) {
    await report(request.requestId, "notice", { notice: `ChatGPT mode not switched (${result.reason}); the answer hint was still sent` });
    return;
  }

  try {
    const update = { [appliedKey]: effort };
    if (applied === "normal" && result.previous) update[ownKey] = result.previous;
    await chrome.storage.local.set(update);
  } catch {
    // The chat is in the right mode anyway; the next request checks it again.
  }
  if (result.changed) await report(request.requestId, "notice", { notice: `ChatGPT mode: ${result.label}` });
}

/**
 * Puts the chat into the first mode of `mode.patterns` it offers. The picker shows the current mode, so a chat
 * already in it is left alone; the user may have changed it by hand since LINK last did.
 */
async function switchChatMode(mode) {
  const trigger = findModePicker();
  if (!trigger) return { ok: false, reason: "model picker not found" };
  const previous = firstLine(trigger.innerText);
  if (mode.patterns[0].test(previous)) return { ok: true, changed: false, label: previous, previous };

  // Radix menus open on pointerdown; a synthetic click alone does not open them.
  trigger.dispatchEvent(new PointerEvent("pointerdown", {
    bubbles: true, cancelable: true, button: 0, pointerType: "mouse", isPrimary: true
  }));
  let items = await waitFor(() => findModeItems(trigger), 1500, null);
  if (!items) {
    trigger.click();
    items = await waitFor(() => findModeItems(trigger), 1500, null);
  }
  if (!items) return { ok: false, reason: "model menu did not open" };

  const offered = items.filter(item => !isExcludedMode(item));
  const target = mode.patterns
    .map(pattern => offered.find(item => pattern.test(firstLine(item.innerText))))
    .find(Boolean);
  if (!target) {
    (document.activeElement ?? document.body).dispatchEvent(new KeyboardEvent("keydown", {
      key: "Escape", code: "Escape", bubbles: true, cancelable: true
    }));
    // The labels on offer tell what a changed picker calls its modes now.
    const labels = offered.map(item => firstLine(item.innerText)).filter(Boolean).join(", ");
    return { ok: false, reason: `${mode.label} is not offered${labels ? `: ${labels}` : ""}` };
  }

  const label = firstLine(target.innerText) || mode.label;
  target.click();
  await delay(300);
  return { ok: true, changed: true, label, previous };
}

function findModePicker() {
  const form = findComposer()?.closest("form");
  const menuButtons = root => [...(root?.querySelectorAll('button[aria-haspopup="menu"], button[aria-haspopup="listbox"]') ?? [])];
  return document.querySelector('[data-testid="model-switcher-dropdown-button"]') ??
    // The composer's picker is labelled with the current mode ("Высокий"), the header's with "model".
    menuButtons(form).find(button => MODE_LABELS.test(firstLine(button.innerText))) ??
    menuButtons(document).find(button => /model|модел/i.test(button.getAttribute("aria-label") ?? "")) ??
    null;
}

function firstLine(text) {
  return (text ?? "").trim().split("\n")[0].trim();
}

function escapeRegExp(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function findModeItems(trigger) {
  const items = [...document.querySelectorAll('[role="menu"], [role="listbox"], [data-radix-popper-content-wrapper]')]
    .flatMap(menu => [...menu.querySelectorAll('[role^="menuitem"], [role="option"], [data-testid^="model-switcher-"]')])
    .filter(item => item !== trigger && !item.contains(trigger) && item.getAttribute("aria-haspopup") !== "menu");
  return items.length ? items : null;
}

function isExcludedMode(item) {
  return EXCLUDED_MODES.test(`${item.getAttribute("data-testid") ?? ""} ${item.innerText ?? ""}`);
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

// The composer's form is searched when known: a long chat holds tens of thousands of nodes.
function findStopButton(form) {
  const root = form?.isConnected ? form : document;
  const classic = root.querySelector('button[data-testid="stop-button"]');
  if (classic instanceof HTMLButtonElement) return classic;
  const submit = root.querySelector("button[data-composer-submit]");
  return submit instanceof HTMLButtonElement && isStopState(submit) ? submit : null;
}

function isStopState(button) {
  const stopLabel = button.getAttribute("data-stop-label");
  return Boolean(stopLabel) && button.getAttribute("aria-label") === stopLabel;
}

// ChatGPT still writes while its composer shows Stop or the answer is flagged as streaming.
function isGenerating(form, answer) {
  return Boolean(findStopButton(form) ||
    answer?.querySelector("[data-message-streaming], .result-streaming") ||
    // Only the composer's own button: "Stop" elsewhere (e.g. reading aloud) says nothing about the answer.
    (form?.isConnected ? form : document)
      .querySelector('form button[aria-label*="stop" i], form button[aria-label*="останов" i]'));
}

function getAssistantMessages(root) {
  return [...new Set(root.querySelectorAll(ASSISTANT_MESSAGES))];
}

/** The element that holds the turns: the nearest one around both the last prompt and the last answer. */
function findThread() {
  const answer = getAssistantMessages(document).at(-1);
  const prompt = [...document.querySelectorAll(USER_MESSAGES)].at(-1);
  let thread = answer && prompt ? answer.parentElement : null;
  while (thread && !thread.contains(prompt)) thread = thread.parentElement;
  return thread;
}

/** The last assistant message. New turns land at the end of the thread, so only its last children are searched. */
function latestAssistantMessage(thread) {
  if (!thread?.isConnected) return getAssistantMessages(document).at(-1) ?? null;
  let child = thread.lastElementChild;
  for (let looked = 0; child && looked < 4; looked++, child = child.previousElementSibling) {
    const message = getAssistantMessages(child).at(-1) ?? (child.matches(ASSISTANT_MESSAGES) ? child : null);
    if (message) return message;
  }
  return getAssistantMessages(thread).at(-1) ?? null;
}

// Message ids survive re-renders, so an old answer that React re-mounts is not mistaken for the new one.
function messageKey(element) {
  return element.getAttribute("data-message-id") ??
    (element.matches("li[data-message-role]") ? element.id || null : null) ??
    element.querySelector("[data-message-id]")?.getAttribute("data-message-id") ?? null;
}

function isNewResponse(element, baseline) {
  const key = messageKey(element);
  if (key) return !baseline.keys.has(key);
  return element !== baseline.last && readAssistantMarkdown(element) !== baseline.text;
}

// ChatGPT gives a finished turn its action buttons (copy, rate…); a turn still being written has none.
function hasTurnActions(message) {
  return Boolean(message.closest("article, [data-turn-id]")?.querySelector('[data-testid="copy-turn-action-button"]'));
}

/**
 * Waits for the answer without polling the page: a MutationObserver wakes the loop when ChatGPT changes something,
 * and while it does, the composer and the last turns are looked at every CHECK_INTERVAL_MS at most. The complete
 * answer is read once more at the end, the moment ChatGPT is done with it.
 */
async function waitForAssistantResponse(requestId, baseline) {
  const page = watchPage();
  try {
    const startedAt = Date.now();
    const thread = findThread();
    let form = findComposer()?.closest("form") ?? null;
    let candidate = null;
    let response = null;
    let stale = true;
    let text = "";
    let convertedAt = 0;
    let changedAt = Date.now();
    let reportedText = "";
    let eventAt = Date.now();

    while (Date.now() - startedAt < RESPONSE_TIMEOUT_MS) {
      stale = page.takeChanges() || stale;
      if (!form?.isConnected) form = findComposer()?.closest("form") ?? null;
      const latest = latestAssistantMessage(thread);
      if (latest !== candidate) {
        // Telling a new answer from an old one can take reading it, so each element is judged once.
        candidate = latest;
        response = latest && isNewResponse(latest, baseline) ? latest : null;
        stale = true;
      }

      const generating = isGenerating(form, response);
      if (response && stale && (!generating || Date.now() - convertedAt >= PROGRESS_INTERVAL_MS)) {
        stale = false;
        convertedAt = Date.now();
        const next = readAssistantMarkdown(response);
        if (next !== text) {
          text = next;
          changedAt = Date.now();
        }
      }

      // A finished turn settles it even if some Stop-like button lingers. Otherwise only the answer's own text
      // completes it, never a status such as "Thinking", and only once ChatGPT has stopped writing.
      const finished = response !== null && hasTurnActions(response);
      if (finished || (text && !generating && (response.closest("[data-message-complete]") || Date.now() - changedAt >= SETTLE_MS))) {
        const answer = readAssistantMarkdown(response) || (finished ? toMarkdown(response) : "") || text;
        if (answer) return answer;
        throw new Error("ChatGPT's answer has no text LINK can show. Look at the ChatGPT tab.");
      }

      if (text && text !== reportedText) {
        reportedText = text;
        eventAt = Date.now();
        await report(requestId, "receiving", { text });
      } else if (Date.now() - eventAt >= HEARTBEAT_INTERVAL_MS) {
        // A cancelled request answers 404, which ends the wait through CancelledError.
        eventAt = Date.now();
        await report(requestId, "heartbeat");
      }

      const now = Date.now();
      let wait = HEARTBEAT_INTERVAL_MS - (now - eventAt);
      if (response && stale) wait = Math.min(wait, PROGRESS_INTERVAL_MS - (now - convertedAt));
      if (text && !generating) wait = Math.min(wait, SETTLE_MS - (now - changedAt));
      await page.next(wait);
    }

    throw new Error("Timed out waiting for ChatGPT to finish the response.");
  } finally {
    page.disconnect();
  }
}

function watchPage() {
  let changed = false;
  let wake = null;
  const observer = new MutationObserver(() => {
    changed = true;
    wake?.();
  });
  // Text streams in as nodes and characters; the Stop button and ChatGPT's completion flag change attributes.
  observer.observe(document.body, {
    subtree: true,
    childList: true,
    characterData: true,
    attributes: true,
    attributeFilter: ["aria-label", "data-testid", "data-message-complete"]
  });
  return {
    takeChanges() {
      const result = changed;
      changed = false;
      return result;
    },
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

// ChatGPT renders Markdown to HTML; LINK needs the Markdown back, so code, lists and tables keep their shape.
// Only rendered answer Markdown counts: status lines ("Thinking", "Думаю") and placeholders around it are not the answer.
function readAssistantMarkdown(message) {
  const roots = [...message.querySelectorAll(MARKDOWN_ROOTS)]
    .filter(root => !root.parentElement?.closest(MARKDOWN_ROOTS));
  return roots.length ? toMarkdown(roots.at(-1)) : "";
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

async function report(requestId, state, details = {}) {
  const result = await chrome.runtime.sendMessage({
    type: "link-event",
    event: { requestId, state, ...details }
  });
  if (result?.status === 404) throw new CancelledError("LINK no longer waits for this request.");
  if (!result?.ok) throw new Error(result?.error ?? "LINK bridge did not accept the event.");
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

announceConversation();
announceTimer = setInterval(announceConversation, ANNOUNCE_INTERVAL_MS);
})();
