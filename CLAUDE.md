# LINK — notes for Claude

<!-- Loaded into every Claude session (HTML comments are stripped): keep it short, specific and current.
     Product vision and decisions live in PROJECT_CONTEXT.md; other agents read AGENTS.md. -->

Windows desktop companion: a small always-on-top WPF overlay that sends a prompt plus screen, clipboard, voice
and current-app context to a linked AI chat (ChatGPT in LINK's own off-screen WebView2 page; a browser extension
bridge remains as a transitional fallback) and shows the answer.
C#, .NET 10, WPF, one assembly.

## Working with the user
- Reply in Russian, briefly, result first. Code, comments and commit messages stay in English.
- The user tests desktop UI, microphone and real ChatGPT flows themselves: finish with a short numbered checklist
  of what to try instead of long self-verification (at most ~10 minutes of own checks).

## Token hygiene
- Read only the section of PROJECT_CONTEXT.md you need (status, decisions, open problems); history is in git log.
- Locate code with Grep/Glob and read only the relevant part of big files (MainWindow.Chat.cs, browser-extension/content.js).

## Build and run
- `dotnet build Link.sln -c Release` must end with 0 warnings and 0 errors; CI runs the same build.
- The user runs `src/Link.Desktop/bin/Release/net10.0-windows/Link.exe`. A running Link locks it and is
  single-instance: ask the user to quit via tray → Exit before rebuilding; never kill their Link without asking.
- Never run Link on the real data in `%LocalAppData%\Link` without an exact backup and restore; never leave test data there.
- After changing `browser-extension/*`, bump `version` in its `manifest.json`: the extension reloads itself from disk
  within ~10 s (no Reload in chrome://extensions, no tab refresh); the embedded page gets `content.js` and
  `embedded-host.js` built into LINK, so it needs a LINK rebuild.

## Code map
- `Models/` settings, projects, `PromptRequest`, `ContextItem`.
- `Services/` storage (`AtomicJsonFile`), request building, Whisper STT, `IChatIntegration` →
  `EmbeddedChatIntegration` (`ChatPageHost` ↔ `IChatPage`) or the fallback `BrowserChatIntegration` →
  `BrowserBridgeServer` (loopback 127.0.0.1:43127); both follow requests with `ChatRequestBroker`.
- `Windows/` Win32 only: `NativeMethods`, hotkeys (RegisterHotKey, low-level hook for push-to-talk), GDI capture,
  waveIn audio, tray, window styles, `CurrentApplicationService` (latched work target).
- `MainWindow.*.cs` partials by concern: shell/projects/context, Chat, Capture, Voice, Sizing.
- `ChatPageWindow` the embedded ChatGPT page (WebView2). It must stay visible to its engine: never hide or minimize
  it — it waits off screen, or ChatGPT is throttled to a few characters a second.
- `browser-extension/` companion that drives the ChatGPT DOM: `content.js` on `embedded-host.js` in the embedded page,
  and the MV3 extension.
- User data: `%LocalAppData%\Link` — settings.json, projects.json, Screenshots, Recordings, Models, link.log,
  WebView2 (the ChatGPT session: never log or copy it), companion-state.json.

## Rules
- Simple, readable code; the smallest change that works and is easy to extend. No speculative abstractions,
  dead code, temporary hacks or unexplained TODOs. Look for existing code before adding new.
- Keep UI, app logic and Win32 apart. Provider and browser details stay behind `IChatIntegration`.
- Services are wired by hand in `App.OnStartup` (no DI container). No new dependency unless .NET or the
  Windows SDK can't do it — ask the user first.
- It is an all-day tray app: watch startup time and memory. The overlay is a layered window, so every redraw
  is costly — keep animations cheap.
- Never block the UI thread; dispose native resources; P/Invoke declarations go in `NativeMethods`.
- Match the surrounding style: `ImplicitUsings` off (see GlobalUsings.cs), nullable on, short comments that say why.
- Settings or model changes: defaults plus `Normalize()`; older JSON must still load. Persist with `AtomicJsonFile`.
- Context sent to the AI stays visible and user-controlled: no screen or microphone capture without an explicit
  action or mode. A voice transcript is user text, never a separate context item.

## Git
- Branch `LeN`. Commit only when asked and report the SHA; push only when asked.
- Update PROJECT_CONTEXT.md and README.md only for accepted decisions and user-visible changes.
