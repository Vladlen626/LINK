# LINK ChatGPT Companion

Unpacked Chrome/Edge extension used by LINK as a local ChatGPT transport.

## Install

1. Start the LINK desktop app.
2. Open `chrome://extensions` or `edge://extensions`.
3. Enable **Developer mode** and choose **Load unpacked**.
4. Select this `browser-extension` folder. If it was already loaded, click **Reload** on its card.
5. Open or reload the ChatGPT conversation that should be linked and keep its tab open.
6. In LINK, click the chat status row, select the discovered conversation, and click **Link**.

The extension talks only to the loopback bridge at `127.0.0.1:43127`. The ChatGPT tab may remain in the background or in a minimized browser window.

## Diagnostics

- LINK must be running before the conversation can appear in its picker.
- The URL must contain a stable `/c/<conversation-id>` segment. Send one message in a new chat first if needed.
- Extension logs are available from **Extensions → LINK ChatGPT Companion → Service worker**.
- Page-side errors are available in DevTools for the linked ChatGPT tab.
