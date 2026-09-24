# LINK — notes for Claude

<!-- Loaded into every Claude session (HTML comments are stripped): keep it short, specific and current.
     Product vision and decisions live in PROJECT_CONTEXT.md; other agents read AGENTS.md. -->

Windows desktop companion: a small always-on-top WPF overlay that sends a prompt plus screen, clipboard, voice
and current-app context to a linked AI chat (ChatGPT through a browser extension bridge) and shows the answer.
C#, .NET 10, WPF, one assembly.

## Working with the user
- Reply in Russian, briefly, result first. Code, comments and commit messages stay in English.
- The user tests desktop UI, microphone and real ChatGPT flows themselves: finish with a short numbered checklist
  of what to try instead of long self-verification (at most ~10 minutes of own checks).

## Token hygiene
- PROJECT_CONTEXT.md is long: grep for the section you need (status, milestone, decision) instead of reading it all.
- Locate code with Grep/Glob and read only the relevant part of big files (MainWindow.Chat.cs, browser-extension/content.js).

## Build and run
- `dotnet build Link.sln -c Release` must end with 0 warnings and 0 errors; CI runs the same build.
- The user runs `src/Link.Desktop/bin/Release/net10.0-windows/Link.exe`. A running Link locks it and is
  single-instance: ask the user to quit via tray → Exit before rebuilding; never kill their Link without asking.
- Never run Link on the real data in `%LocalAppData%\Link` without an exact backup and restore; never leave test data there.
- After changing `browser-extension/*` the user must press Reload in chrome://extensions and reload the ChatGPT tab.

## Code map
- `Models/` settings, projects, `PromptRequest`, `ContextItem`.
- `Services/` storage (`AtomicJsonFile`), request building, Whisper STT, `IChatIntegration` → `BrowserChatIntegration`
  → `BrowserBridgeServer` (loopback 127.0.0.1:43127).
- `Windows/` Win32 only: `NativeMethods`, hotkeys (RegisterHotKey, low-level hook for push-to-talk), GDI capture,
  waveIn audio, tray, window styles, `CurrentApplicationService` (latched work target).
- `MainWindow.*.cs` partials by concern: shell/projects/context, Chat, Capture, Voice, Sizing.
- `browser-extension/` MV3 companion that drives the ChatGPT DOM.
- User data: `%LocalAppData%\Link` — settings.json, projects.json, Screenshots, Recordings, Models, link.log.

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
