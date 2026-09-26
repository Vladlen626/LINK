# LINK ChatGPT Companion

The script that works with ChatGPT's page for LINK. LINK keeps ChatGPT in its own WebView2 window off screen and
builds `content.js` into itself together with `embedded-host.js`, the companion's end of WebView2's message channel
(`chrome.webview.postMessage`). Every message carries a per-session token that only `embedded-host.js`'s closure
holds; LINK drops anything else. The script runs in each new top-level `https://chatgpt.com` document before the
page's own scripts and starts the companion at `DOMContentLoaded`. Its storage lives in LINK: where a prompt stands
(`request:*`) for the session, the chat modes it set (`effort:*`, `mode:*`) in `%LocalAppData%\Link\companion-state.json`.

A change here needs a LINK rebuild; the companion's version is LINK's.

## Request lifecycle

LINK's `ChatRequestBroker` follows every request; `ChatPageHost` hands it to the page.

- **Announcements.** Every 2.5 s the page says what it shows: its conversation (or none), whether it is busy, whether
  an account is signed in (`signedIn`: false when the page offers to log in) and which companion speaks — each
  document of the page runs its own, and LINK tells a reloaded page's new one by its id.
- **The linked chat.** A page on another conversation is taken to the linked one before the prompt is handed over;
  LINK waits up to 20 s for it to be there.
- **Never hidden, resting when unused.** The window waits off screen and is never hidden or minimized, so its engine
  does not throttle ChatGPT's typing. Half a minute after LINK last needed it, the page rests out of its engine's sight:
  it draws nothing (ChatGPT's animations would otherwise cost a tenth of a core all day) and keeps less memory, while
  its timers, the companion's announcements too, run on. LINK wakes it before handing it a request. Open chat brings
  it on screen, awake; the user can sign in or look at the chat there.
- **Redelivery.** LINK offers a request again after 10 s of silence, and at once to a new companion: one working on it
  answers with a heartbeat, a new one picks it up. Where a prompt stands is kept (`request:<id>`), so it is never sent
  twice, and its images are not sent again.
- **Reloads only when nothing is lost.** A crashed or hung page loads again, at most once a minute. A page kept for
  six hours is loaded afresh on its chat, but only while nothing waits on it, it is not busy and the user does not
  look at it.
- **No endless waiting.** LINK fails a request after 20 s without an answer from the page or 45 s of silence. The page
  fails it when ChatGPT does not take the prompt within 20 s, when the page switches to another conversation, or
  after 10 minutes.
- **Stop.** Stop/Esc in LINK answers the page's next event with 404 and reason `cancelled`; the page then presses
  ChatGPT's Stop, but only for a prompt that reached the chat. `completed` or `failed` make it stop watching without
  touching ChatGPT.
- **Busy chat.** While ChatGPT still writes an earlier answer, Send is not offered, and after 15 s the request fails
  with "ChatGPT is still answering the previous message". The page asks LINK once more right before Send, so a
  stopped prompt is not sent; a prompt that never went out is taken back from ChatGPT's composer.
- **New chats.** A prompt with an empty `conversationId` goes to a page without a conversation: ChatGPT's home or a
  project's page (`/g/g-p-<id>/project`). ChatGPT first shows the new chat under a local id
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
    Markdown root);
  - nothing has changed for a minute.
- **Cheap watching.** A MutationObserver wakes the watch on text, nodes and a few attributes (not classes or styles).
  It looks at most every 150 ms, and only at the composer and the last turns; the whole page is searched only until
  the answer is found.
- **Long chats are slow on ChatGPT's side.** In a conversation with a long history ChatGPT starts later and types a
  few characters a second even in a normal browser tab; a new chat answers at hundreds. LINK suggests a new chat
  when an answer arrives that slowly.

## Response effort

LINK's Fast / Normal / Deep reaches the page as `effort`. The companion finds the mode picker in and around the
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

- **Open chat** in LINK shows the page itself.
- **Check chat** in LINK sends a `read` request that the page answers at once: the answer (while a prompt is watched,
  only the answer to it), whether ChatGPT is still writing, and what the selectors see — Stop buttons with their
  visibility, the composer's button labels, Markdown roots, turn buttons, the picker; the last messages when the
  prompt or its answer is missing; and, when no message is found at all, how the page marks its messages. LINK writes
  it to `link.log`, as it does the diagnostics of unusual ends and errors.
- Images go through ChatGPT's upload control. LINK waits for the attachment preview; if the composer has changed,
  the request fails without sending text, and the image stays attached in LINK.
- `tests/companion` runs `content.js` on `embedded-host.js` against mock ChatGPT pages (all three markups) over a fake
  WebView2 channel; `dotnet run --project tests/Link.Desktop.Tests` runs them in headless Edge or Chrome.
