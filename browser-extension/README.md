# LINK ChatGPT Companion

The script that works with ChatGPT's page for LINK. It runs in two places:

- **LINK's embedded page (default).** LINK keeps ChatGPT in its own WebView2 window off screen and builds
  `content.js` into itself together with `embedded-host.js`, a stand-in for the part of the extension API that
  `content.js` uses. Messages go over WebView2's channel (`chrome.webview.postMessage`), each with a per-session token
  that only the stand-in's closure holds; LINK drops anything else. The script runs in each new top-level
  `https://chatgpt.com` document before the page's own scripts and starts the companion at `DOMContentLoaded`. Its
  storage lives in LINK: where a prompt stands (`request:*`) for the session, the chat modes it set (`effort:*`,
  `mode:*`) in `%LocalAppData%\Link\companion-state.json`. A change here needs a LINK rebuild.
- **This unpacked Chrome / Edge / Opera extension** — the fallback while the embedded page proves itself
  (Settings → ChatGPT → Connection → Browser extension). It talks only to LINK's loopback bridge at
  `127.0.0.1:43127`.

Both ways the requests are followed by LINK's `ChatRequestBroker`: redelivery, silence, Stop, Check chat.

## Install (browser fallback)

1. In LINK choose Settings → ChatGPT → Connection → **Browser extension** and restart LINK.
2. Open `chrome://extensions` (or `edge://extensions`), enable **Developer mode**, choose **Load unpacked** and select
   this folder.
3. Open the ChatGPT conversation to link and keep its tab open. A new chat needs one message first, so that its URL
   has `/c/<id>`.
4. In LINK, click the chat status row, pick the conversation and click **Link**.

**Updates.** The service worker reads `manifest.json` from disk every 10 s and reloads the extension when its
version changes; the new `content.js` starts in the open ChatGPT tabs, and a running prompt carries on. Bump
`version` with every change. Only the step to 0.7.2 needed **Reload** on the card.

## Request lifecycle

- **One copy per tab.** A copy that finds a live one stays out; a copy of a reloaded extension makes older ones
  retire through the DOM event `link-companion-started`.
- **Handshake.** Before each delivery and every 15 s the worker sends `link-hello`. A tab without a matching copy
  gets `content.js` injected once (a copy of another version is told to retire first); LINK shows "Reconnecting".
- **Reloads only when nothing is lost.** Only a page that cannot run the companion (discarded, crashed, error page)
  is reloaded, at most once a minute, and never a page whose prompt ChatGPT may be answering.
- **Redelivery.** LINK offers a request again after 10 s of silence: a copy working on it answers with a heartbeat,
  a new copy picks it up. Where a prompt stands is kept in `chrome.storage.local` (`request:<id>`), so it is never
  sent twice, and its images are not sent again.
- **No endless waiting.** LINK fails a request after 20 s without an answer from the tab or 45 s of silence. The tab
  fails it when ChatGPT does not take the prompt within 20 s, when the page switches to another conversation, or
  after 10 minutes.
- **Stop.** Stop/Esc in LINK makes the bridge answer the tab's next event with 404 and reason `cancelled`; the tab
  then presses ChatGPT's Stop, but only for a prompt that reached the chat. `completed` or `failed` make the tab stop
  watching without touching ChatGPT.
- **Busy chat.** While ChatGPT still writes an earlier answer, Send is not offered, and after 15 s the request fails
  with "ChatGPT is still answering the previous message". The tab asks LINK once more right before Send, so a
  stopped prompt is not sent; a prompt that never went out is taken back from ChatGPT's composer.
- **Hidden tabs.** Chrome throttles timers in hidden tabs, so the worker ticks a tab that works on a request, and
  such a tab is kept out of Memory Saver. ChatGPT itself still types an answer out slowly in a hidden tab — the reason
  for LINK's embedded page, which its engine never sees hidden.
- **Announcements.** Every 2.5 s the page says what it shows: its conversation (or none), whether it is busy, whether
  an account is signed in (`signedIn`: false when the page offers to log in) and which copy of the companion speaks.
- **New chats** (embedded page only). A prompt with an empty `conversationId` goes to a page without a conversation:
  ChatGPT's home or a project's page (`/g/g-p-<id>/project`). ChatGPT first shows the new chat under a local id
  (`/c/local-chatgpt:…`) and about two seconds later under its real one; a local id counts as no conversation. Once
  the real `/c/<id>` is there, the companion takes it as the prompt's conversation and its events carry
  `conversation`; LINK links the project to it, and a reloaded page gets the prompt in that conversation.

## Reading the answer

- **New since sending.** Before sending, the companion notes every message id on the page. The prompt is the new
  user message holding the prompt's first words (letters and digits only, so Markdown and dashes do not matter), else
  the only new one; the answer is the last new assistant message after it, wherever the chat puts it.
- **Three markups:**
  - classic: `article[data-turn]`, `[data-message-author-role]`;
  - logged-out "octane": `li[data-message-role]`, `div[data-assistant-markdown]`, `data-message-complete`, and one
    submit button whose `aria-label` turns into its `data-stop-label`;
  - logged-in, late September 2026: a `div[data-turn-key]` holds the prompt and its answer as units named by
    `data-chatgpt-search-unit-key` (`…:user`, `…:assistant`); the answer root is `[data-markdown-text-style]`,
    typed out under `data-markdown-animated`; Stop is a plain `button[aria-label="Остановить"]`. The CSS `i` flag
    folds only Latin case, so Russian labels are matched as ChatGPT writes them.
- **Streaming.** Only the answer's Markdown (headings, lists, code, tables, quotes, links, KaTeX source) goes to LINK,
  at most every 250 ms. Status lines ("Thinking", "Думаю") and placeholders never count as the answer.
- **Complete** when any of these holds:
  - the turn shows the answer's action buttons: `…-turn-action-button`, or `.turn-action-controls` outside the
    prompt;
  - ChatGPT flags it (`data-message-complete`);
  - no visible Stop is left, the answer is not being typed out, and it has not changed for 1.2 s (3 s without a
    Markdown root or in a hidden tab);
  - nothing has changed for a minute.
- **Cheap watching.** A MutationObserver wakes the watch on text, nodes and a few attributes (not classes or styles).
  It looks at most every 150 ms, and only at the composer and the last turns; the whole page is searched only until
  the answer is found.

## Response effort

LINK's Fast / Normal / Deep reaches the tab as `effort`. The companion finds the mode picker in and around the
composer (the header's model picker only when the composer has none) and switches only when the chat is in another
mode:

- menu items ("Instant / Medium / High", «Мгновенный / Средний / Высокий») in a Radix menu opened with `pointerdown`,
  or in a native popover (`popovertarget`, closed with `hidePopover()`);
- the logged-in effort slider ("Мощность", `[data-reasoning-slider]`): arrow keys move it one level at a time, read
  from its announcement ("Высокий, 3 из 3."); Fast goes to the lightest level.

It then waits until the picker shows the new mode. Normal gives back the mode the chat had before LINK changed it
(`mode:<conversation>`). Extra High and Pro modes are never picked. If switching fails, the prompt still goes, and
LINK's status says why.

## Diagnostics

- Service worker logs: **Extensions → LINK ChatGPT Companion → Service worker**; page errors: DevTools of the tab.
- **Check chat** in LINK sends a `read` request that the tab answers at once: the answer (while a prompt is watched,
  only the answer to it), whether ChatGPT is still writing, and what the selectors see — Stop buttons with their
  visibility, the composer's button labels, Markdown roots, turn buttons, the picker; the last messages when the
  prompt or its answer is missing; and, when no message is found at all, how the page marks its messages. LINK writes
  it to `link.log`, as it does the diagnostics of unusual ends and errors.
- Images go through ChatGPT's upload control. LINK waits for the attachment preview; if the composer has changed,
  the request fails without sending text, and the image stays attached in LINK.
- `tests/browser-extension` runs these scripts against mock ChatGPT pages (all three markups), and `content.js` on
  `embedded-host.js` over a fake WebView2 channel; `dotnet run --project tests/Link.Desktop.Tests` runs them in
  headless Edge or Chrome.
