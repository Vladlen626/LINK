# LINK ChatGPT Companion

Unpacked Chrome/Edge extension used by LINK as a local ChatGPT transport.

## Install

1. Start the LINK desktop app.
2. Open `chrome://extensions` or `edge://extensions`.
3. Enable **Developer mode** and choose **Load unpacked**.
4. Select this `browser-extension` folder. If it was already loaded, click **Reload** on its card.
5. Open or reload the ChatGPT conversation that should be linked and keep its tab open.
6. In LINK, click the chat status row, select the discovered conversation, and click **Link**.

After each extension update, press **Reload** on its extension card and reload the linked ChatGPT tab. An already open tab keeps the previous content script until it is reloaded. The card shows the version (0.3.0 adds effort modes, Markdown extraction and background-tab fixes).

The extension talks only to the loopback bridge at `127.0.0.1:43127`. The ChatGPT tab may remain in the background or in a minimized browser window: the service worker stays alive while a conversation is registered and sends ticks to the tab while it works on a request, because Chrome throttles timers in hidden tabs to about once a minute. A tab that receives a LINK request is excluded from Memory Saver discarding.

## Response effort

LINK's Fast / Normal / Deep setting reaches the tab as `effort`. When it differs from the mode LINK last applied to this conversation (kept in `chrome.storage.local`), the companion opens ChatGPT's model picker and selects Instant, Auto or Thinking. Normal leaves the user's own model choice untouched until LINK has changed it. If the picker or the mode is not found, the request is still sent and LINK's status shows "ChatGPT mode not switched (…)".

## Diagnostics

- LINK must be running before the conversation can appear in its picker.
- The URL must contain a stable `/c/<conversation-id>` segment. Send one message in a new chat first if needed.
- Extension logs are available from **Extensions → LINK ChatGPT Companion → Service worker**.
- Page-side errors are available in DevTools for the linked ChatGPT tab.
- Image requests use ChatGPT's composer upload control. LINK waits for an attachment preview before sending; if ChatGPT changes its composer, the request fails without sending text and the image stays attached in LINK for a retry.
- Answers are converted from ChatGPT's rendered HTML back to Markdown (headings, lists, code with language, tables, quotes, links, KaTeX source), so LINK can render and copy them faithfully.
- Stop/Esc in LINK makes the bridge answer 404 to the tab's next heartbeat (every 2 s); the tab then stops ChatGPT's generation and is free for the next request.
- "The ChatGPT tab did not respond" means no event arrived within 20 s of dispatch: reload the tab, and after extension updates also press Reload on the extension card.
