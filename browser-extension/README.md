# LINK ChatGPT Companion

Unpacked Chrome/Edge extension used by LINK as a local ChatGPT transport.

## Install

1. Start the LINK desktop app.
2. Open `chrome://extensions` or `edge://extensions`.
3. Enable **Developer mode** and choose **Load unpacked**.
4. Select this `browser-extension` folder. If it was already loaded, click **Reload** on its card.
5. Open or reload the ChatGPT conversation that should be linked and keep its tab open.
6. In LINK, click the chat status row, select the discovered conversation, and click **Link**.

After each extension update, press **Reload** on its extension card and reload the linked ChatGPT tab. An already open tab keeps the previous content script until it is reloaded. The card shows the version (0.3.0 adds effort modes, Markdown extraction and background-tab fixes; 0.4.0 watches answers without polling and never takes ChatGPT's "Thinking" status for the answer; 0.5.0 streams every change of the answer and switches the composer's Instant / Medium / High picker; 0.6.0 looks only at the composer and the last turns, finishes on the turn's action buttons and answers LINK's "Check chat").

The extension talks only to the loopback bridge at `127.0.0.1:43127`. The ChatGPT tab may remain in the background or in a minimized browser window: the service worker stays alive while a conversation is registered and sends ticks to the tab while it works on a request, because Chrome throttles timers in hidden tabs to about once a minute. A tab that receives a LINK request is excluded from Memory Saver discarding.

## Response effort

LINK's Fast / Normal / Deep setting reaches the tab as `effort`. Since June 2026 ChatGPT's mode picker sits in the composer and shows the current mode (Instant, Medium, High, Extra High; "Мгновенный", "Средний", "Высокий"… in Russian); the older header picker offered Instant, Auto and Thinking. Fast and Deep read the picker before every request and switch to Instant / High only when the chat is in another mode, so a mode changed by hand in ChatGPT is corrected too. Normal leaves the user's own mode alone until LINK has changed it, and then gives back the mode the chat had before (kept in `chrome.storage.local`), or Medium / Auto. Labels match by their first word, so Extra High and Pro modes are never picked. If the picker or the mode is not found, the request is still sent and LINK's status shows "ChatGPT mode not switched (…)" with the labels the picker offered.

## Diagnostics

- LINK must be running before the conversation can appear in its picker.
- The URL must contain a stable `/c/<conversation-id>` segment. Send one message in a new chat first if needed.
- Extension logs are available from **Extensions → LINK ChatGPT Companion → Service worker**.
- Page-side errors are available in DevTools for the linked ChatGPT tab.
- Image requests use ChatGPT's composer upload control. LINK waits for an attachment preview before sending; if ChatGPT changes its composer, the request fails without sending text and the image stays attached in LINK for a retry.
- Answers are converted from ChatGPT's rendered HTML back to Markdown (headings, lists, code with language, tables, quotes, links, KaTeX source), so LINK can render and copy them faithfully. Only the answer's rendered Markdown counts: status lines such as "Thinking" / "Думаю" and placeholders are never reported as the answer.
- While an answer streams, a MutationObserver wakes the companion when the page changes; it looks at most every 150 ms, and only at the composer's form and the last few turns of the thread, so a long chat costs microseconds per look instead of milliseconds. The growing answer is sent at most every 250 ms (for the overlay and reading aloud). Nothing runs while the page is quiet, e.g. while ChatGPT thinks.
- The turn's action buttons (copy, rate…) end the answer even if some Stop-like button lingers; an answer without a recognised Markdown root is then read as a whole.
- "Check chat" in LINK sends a `read` request that the tab answers at once, even while a prompt is still awaited or its watch is stuck: the answer after the last prompt, whether ChatGPT is still writing, and a snapshot of what the selectors see (stop button, "stop" labels, Markdown roots, turn buttons, picker label). LINK writes that snapshot to `link.log`, and a waiting prompt that reached the chat is completed with a finished answer. LINK also checks by itself when a waiting prompt has heard nothing for 20 s. The answer is complete when ChatGPT's Stop button is gone and the turn shows its action buttons (or the new UI's `data-message-complete`); then the whole answer is read once more and sent at once. Without those signs a 1.2 s quiet period after the Stop button disappears ends it.
- Stop/Esc in LINK makes the bridge answer 404 to the tab's next heartbeat (every 2 s); the tab then stops ChatGPT's generation and is free for the next request.
- "The ChatGPT tab did not respond" means no event arrived within 20 s of dispatch: reload the tab, and after extension updates also press Reload on the extension card.
