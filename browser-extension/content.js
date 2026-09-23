(() => {
const ANNOUNCE_INTERVAL_MS = 2500;
const RESPONSE_TIMEOUT_MS = 10 * 60 * 1000;
let processingRequestId = null;

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (message?.type === "link-discover") {
    sendResponse({ conversation: getConversation() });
    announceConversation();
    return;
  }
  if (message?.type === "link-request") {
    sendResponse({ accepted: true });
    void handleRequest(message.request);
  }
});

function announceConversation() {
  const conversation = getConversation();
  if (!conversation) return;
  void chrome.runtime.sendMessage({
    type: "link-announce",
    conversation
  }).catch(() => {});
}

function getConversation() {
  const conversationId = getConversationId();
  return conversationId ? { conversationId, title: getConversationTitle(), url: location.href } : null;
}

async function handleRequest(request) {
  if (processingRequestId) {
    await report(request.requestId, "error", null, "This ChatGPT tab is already processing another LINK request.");
    return;
  }

  if (getConversationId() !== request.conversationId) {
    await report(request.requestId, "error", null, "The linked ChatGPT tab navigated to another conversation.");
    return;
  }

  processingRequestId = request.requestId;
  try {
    await report(request.requestId, "sending");
    const composer = await waitFor(findComposer, 15000, "ChatGPT message composer was not found.");
    if (request.attachments?.length) await attachImages(composer, request.attachments);
    setComposerText(composer, request.prompt);
    const baseline = getAssistantMessages();
    const baselineLast = baseline.at(-1) ?? null;
    const baselineText = baselineLast ? readAssistantText(baselineLast) : "";

    const sendButton = await waitFor(findEnabledSendButton, 15000, "ChatGPT Send button did not become available.");
    sendButton.click();
    await report(request.requestId, "waiting");
    const response = await waitForAssistantResponse(request.requestId, baselineLast, baselineText);
    await report(request.requestId, "completed", response);
  } catch (error) {
    try {
      await report(request.requestId, "error", null, error instanceof Error ? error.message : String(error));
    } catch {
      // LINK may have closed while the ChatGPT request was in progress.
    }
  } finally {
    processingRequestId = null;
  }
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
    'textarea[placeholder*="Message"]',
    '[contenteditable="true"][data-virtualkeyboard]'
  ];
  return selectors.map(selector => document.querySelector(selector)).find(Boolean) ?? null;
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
    'button[aria-label="Send prompt"]',
    'form button[type="submit"]'
  ];
  const button = selectors.map(selector => document.querySelector(selector)).find(Boolean);
  return button instanceof HTMLButtonElement && !button.disabled ? button : null;
}

function getAssistantMessages() {
  const nodes = document.querySelectorAll(
    '[data-message-author-role="assistant"], article[data-turn="assistant"]');
  return [...new Set(nodes)];
}

function readAssistantText(message) {
  const content = message.querySelector('.markdown, [class*="markdown"]');
  return (content?.innerText ?? message.innerText ?? "").trim();
}

function isGenerating() {
  return Boolean(document.querySelector(
    'button[data-testid="stop-button"], button[aria-label*="Stop"], button[aria-label*="stop"]'));
}

async function waitForAssistantResponse(requestId, baselineLast, baselineText) {
  const startedAt = Date.now();
  let responseElement = null;
  let lastText = "";
  let lastChangeAt = Date.now();
  let lastReportedText = "";

  while (Date.now() - startedAt < RESPONSE_TIMEOUT_MS) {
    const messages = getAssistantMessages();
    const latest = messages.at(-1) ?? null;
    if (latest && (latest !== baselineLast || readAssistantText(latest) !== baselineText)) {
      responseElement = latest;
    }

    if (responseElement) {
      const text = readAssistantText(responseElement);
      if (text !== lastText) {
        lastText = text;
        lastChangeAt = Date.now();
      }
      if (text && text !== lastReportedText) {
        lastReportedText = text;
        await report(requestId, "receiving", text);
      }
      if (text && !isGenerating() && Date.now() - lastChangeAt >= 2500) return text;
    }

    await delay(250);
  }

  throw new Error("Timed out waiting for ChatGPT to finish the response.");
}

async function report(requestId, state, text = null, error = null) {
  const result = await chrome.runtime.sendMessage({
    type: "link-event",
    event: { requestId, state, text, error }
  });
  if (!result?.ok) throw new Error(result?.error ?? "LINK bridge did not accept the event.");
}

async function waitFor(producer, timeoutMs, errorMessage) {
  const startedAt = Date.now();
  while (Date.now() - startedAt < timeoutMs) {
    const value = producer();
    if (value) return value;
    await delay(100);
  }
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
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

announceConversation();
setInterval(announceConversation, ANNOUNCE_INTERVAL_MS);
})();
