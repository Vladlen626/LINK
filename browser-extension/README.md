# LINK ChatGPT Companion

Unpacked Chrome/Edge extension used by LINK as a local ChatGPT transport.

## Install

1. Start the LINK desktop app.
2. Open `chrome://extensions` or `edge://extensions`.
3. Enable **Developer mode** and choose **Load unpacked**.
4. Select this `browser-extension` folder. If it was already loaded, click **Reload** on its card.
5. Open the ChatGPT conversation that should be linked and keep its tab open.
6. In LINK, click the chat status row, select the discovered conversation, and click **Link**.

After an extension update, pressing **Reload** on its card is enough: since 0.7.0 the service worker starts the new content script in every open ChatGPT tab (the tab does not need to be reloaded), and a prompt that was running meanwhile carries on. The card shows the version (0.3.0 adds effort modes, Markdown extraction and background-tab fixes; 0.4.0 watches answers without polling; 0.5.0 streams every change of the answer and switches the composer's picker; 0.6.0 looks only at the composer and the last turns and answers LINK's "Check chat"; 0.7.0 makes the flow survive reloads, never counts a hidden Stop, and verifies the mode switch).

The extension talks only to the loopback bridge at `127.0.0.1:43127`. The ChatGPT tab may remain in the background or in a minimized browser window: the service worker stays alive while a conversation is registered and sends ticks to the tab while it works on a request, because Chrome throttles timers in hidden tabs to about once a minute. A tab that receives a LINK request is excluded from Memory Saver discarding.

## Request lifecycle

- **One copy per tab.** The manifest and the service worker may both start `content.js`; a copy that finds a live one in the page stays out, so a request is never handled twice. A copy started by a reloaded extension tells older copies (from 0.7.0 on) to step aside through a DOM event; copies of a reloaded extension can no longer reach it anyway.
- **Version handshake.** Before each delivery, and every 15 s for every ChatGPT tab, the service worker sends `link-hello`. A tab without a copy that answers (for example after the extension was reloaded) gets the current `content.js` injected, once: repairs of one tab never run side by side. A copy of another version is told to retire first. LINK shows "Reconnecting" while this happens.
- **Reloads only when nothing is lost.** A tab that cannot run the companion at all (discarded, crashed, an error page) is reloaded for a prompt that has not reached the chat yet, or when it is discarded; never twice within a minute, and never a live page whose prompt ChatGPT may still be answering. A page that is merely busy is left alone.
- **Redelivery.** LINK offers a request again whenever the tab has been quiet about it for 10 s. A copy still working on it answers with a heartbeat; a new copy (after a page or extension reload) picks the prompt up. Where a prompt stands is kept in `chrome.storage.local` (`request:<id>`: sending / sent, the last user message before it, the prompt's own message id), and the chat itself is checked for the prompt, so a prompt is never sent twice. A prompt that reached the chat is only watched when picked up again, and its images are not sent again.
- **No endless waiting.** LINK ends a request with an error when the tab does not answer within 20 s, or goes quiet for 45 s while it answers; the tab ends it when ChatGPT does not take the prompt (it is still in the composer 20 s after Send), when the page switches to another conversation, or after 10 minutes.
- **Stop.** Stop/Esc in LINK makes the bridge answer the tab's next event (at most 2 s later) with 404 and reason `cancelled`; only then does the tab press ChatGPT's Stop. A request LINK ended otherwise (`completed`, e.g. through Check chat, or `failed`) makes the tab stop watching without touching ChatGPT.

## Reading the answer

- The answer is the last assistant message after the prompt's own user message (or, before that message shows up, one LINK had not seen before sending).
- While ChatGPT writes, only the answer's rendered Markdown is sent to LINK, at most every 250 ms (less often for an answer slow to convert). Status lines such as "Thinking" / "Думаю", shimmering placeholders and controls are never reported as the answer.
- The answer is complete when:
  - its turn shows the action buttons (`…-turn-action-button`: copy, rate, read aloud), even if a Stop-like button lingers; or
  - ChatGPT flags it (`data-message-complete`), or no **visible** Stop button is left and the answer has not changed for 1.2 s (3 s for an answer without a Markdown root, which is then read as a whole); a hidden Stop left in the composer does not count; or
  - the answer and its turn have not changed at all for a minute (a Stop button that never goes away).
- Then the whole answer is read once more and sent. Unusual ends (no Markdown root, the one-minute rule) and errors carry a diagnostics snapshot that LINK writes to `link.log`.
- A MutationObserver wakes the watch when the page changes; it looks at most every 150 ms, and only at the composer's form and the last turns of the thread (the whole page at most once a second, only while the answer has not been found), so a long chat costs microseconds per look. Nothing runs while the page is quiet.

## Response effort

LINK's Fast / Normal / Deep setting reaches the tab as `effort`. Since June 2026 ChatGPT's mode picker sits in the composer and shows the current mode (Instant, Medium, High, Extra High; "Мгновенный", "Средний", "Высокий", "Очень высокий" in Russian). The companion looks for a menu button labelled with a mode in and around the composer's form; the header's model picker (`model-switcher-dropdown-button`, "ChatGPT") is used only when the composer has none (the older UI with Instant / Auto / Thinking). The menu is opened with `pointerdown` (Radix), then click, then Enter, and found through the picker's `aria-controls` in its portal. Fast and Deep switch to Instant / High only when the chat is in another mode; after choosing, the companion waits until the picker shows the new mode, tries Enter on the item once more, and otherwise reports "ChatGPT mode not switched (the picker still shows "…")". Normal leaves the user's own mode alone until LINK has changed it, and then gives back the mode the chat had before (kept as `mode:<conversation>`), or Medium / Auto. Labels match by their first word, so Extra High and Pro modes are never picked; a label that names its kind first ("Рассуждение · Высокий") is read by the mode. If the picker or the mode is not found, the request is still sent and LINK's status shows the reason with the labels the picker offered.

## Diagnostics

- LINK must be running before the conversation can appear in its picker.
- The URL must contain a stable `/c/<conversation-id>` segment. Send one message in a new chat first if needed.
- Extension logs are available from **Extensions → LINK ChatGPT Companion → Service worker**.
- Page-side errors are available in DevTools for the linked ChatGPT tab.
- "Check chat" in LINK sends a `read` request that the tab answers at once: the answer after the prompt (while a prompt is watched, only an answer to that prompt, with its id), whether ChatGPT is still writing, and a snapshot of what the selectors see (stop buttons with their visibility, Markdown roots, turn buttons, picker label). A waiting prompt that reached the chat is completed with a finished answer. LINK also checks by itself when a waiting prompt has had no news for 20 s.
- Image requests use ChatGPT's composer upload control. LINK waits for an attachment preview before sending; if ChatGPT changes its composer, the request fails without sending text and the image stays attached in LINK for a retry.
- Answers are converted from ChatGPT's rendered HTML back to Markdown (headings, lists, code with language, tables, quotes, links, KaTeX source), so LINK can render and copy them faithfully.
- The checks in `tests/browser-extension` run these scripts against a mock ChatGPT page; `dotnet run --project tests/Link.Desktop.Tests` runs them in headless Edge or Chrome.
