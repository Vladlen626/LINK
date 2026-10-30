# LINK — notes for Claude

<!-- Loaded into every Claude session (HTML comments are stripped): keep it short, specific and current.
     Product vision and decisions live in PROJECT_CONTEXT.md; other agents read AGENTS.md. -->

Windows desktop companion: a small always-on-top WPF overlay that sends a prompt plus screen, clipboard, voice
and current-app context to a linked AI chat (ChatGPT in LINK's own off-screen WebView2 page) and shows the answer.
C#, .NET 10, WPF, one assembly.

## Working with the user
- Everything in English (replies, docs, code, commits): the user's choice, it saves tokens. Briefly, result first.
- The user tests desktop UI, microphone and real ChatGPT flows themselves: finish with a short numbered checklist
  of what to try instead of long self-verification (at most ~10 minutes of own checks).

## Token hygiene
- Read only the section of PROJECT_CONTEXT.md you need (status, decisions, state and next); history is in git log.
- Locate code with Grep/Glob and read only the relevant part of big files (MainWindow.Chat.cs, companion/content.js).

## Harness
The same harness Link gives its agents, for work on this repo.
- Skills: `.agents/skills/<name>/SKILL.md`, one source for Claude and Codex; read the one that fits before the work.
  `rebuild-link` — build, test and restart the user's Link, never while an agent works in it; `preview-svg` — look at
  the README's drawings; `agent-token-review` — where an agent's tokens and turns went, from the session logs.
  `drive-link` — give the user's Link a command through UI Automation (hand a task to Claude Code), only when asked.
  `share-packs` — the packs' skills the user's agents learned (`%USERPROFILE%\LinkPacks`) into the repo for colleagues.
- Repeated work without a skill: once it has worked out, add one there (a short SKILL.md and its script).
- The environment, as met here (Windows 11, Git Bash and Windows PowerShell 5.1, Russian locale):
  - PowerShell 5.1 reads BOM-less UTF-8 as ANSI: scripts it runs stay ASCII (other characters from char codes),
    text files are read with `Get-Content -Encoding UTF8`.
  - `dotnet` prints in Russian (`Ошибок: 0`, `Предупреждений: 0`); a failed build leaves the old test exe, which
    passes: check the build line before the tests.
  - `cd` in the Bash tool moves the session's folder: use absolute paths or `git -C`.
  - In Bash, `sed` makes `\n` in a replacement a line break, and `\uXXXX` in a command may become the character:
    change code with the Edit tool.

## Build and run
- `dotnet build Link.sln -c Release` must end with 0 warnings and 0 errors; CI runs the same build.
- The user runs `src/Link.Desktop/bin/Release/net10.0-windows/Link.exe`. A running Link locks it and is
  single-instance: close it gracefully (`rebuild-link`) unless the user rebuilds it themselves, never while an agent
  works in it, and never kill it. Building the tests into the scratchpad needs no closing.
- Never run Link on the real data in `%LocalAppData%\Link` without an exact backup and restore; never leave test data there.
- `companion/content.js` and `embedded-host.js` are built into LINK: a change there needs a LINK rebuild.

## Code map
- `Models/` settings, projects, `PromptRequest`, `ContextItem`.
- `Services/` storage (`AtomicJsonFile`), request building, Whisper STT, `IChatIntegration` →
  `EmbeddedChatIntegration` (`ChatPageHost` ↔ `IChatPage`, requests followed by `ChatRequestBroker`).
- `Windows/` Win32 only: `NativeMethods`, hotkeys (RegisterHotKey, low-level hook for held keys), capture (WGC, GDI),
  waveIn audio, tray, window styles, `CurrentApplicationService` (latched work target).
- Agents: `AgentCoordinator` runs a task (check after Do, an answer file such as the map, Start over);
  `CliAgentProvider` → `ClaudeCodeProvider` / `CodexProvider` over `AgentProcess`; `AgentChanges` finds and undoes
  changes; `AgentRules`, `ProjectMap`, `ProjectSkills`,
  `ProjectRoles` are the harness, `ToolPacks` its packs shared by all projects; `KnowledgePack` puts what the agents
  learned in the packs into a `.linkpack` for a friend and takes one in, `TeamPack` a workspace's roles and skills;
  rules a script can hold are code: `src/Link.Guard` (Claude Code's PreToolUse hook,
  `docs/harness/guarded-rules.md` is their text for Codex) and `HarnessLint` (a run's map and skills); `AutoEffort`, `TaskState` (Compact), `HarnessView` (the inspector); `TeamRunner` (team
  steps), its flow in `MainWindow.Team`. The agent's rules,
  tasks and roles are `docs/harness/**/*.md`, copied beside Link.exe and editable without a rebuild. UI: `AgentPanel`
  (the sidebar; partials Card, Roles, Task with its cards, Composer) in `StudioWindow`, beside `HarnessInspector`.
- `MainWindow.*.cs` partials by concern: shell/projects/context, Chat (Send, one attempt), AskAgain, Route (where Send
  goes, effort), ChatLink (the project's chat, its connection, check), Response (the answer), Capture, Voice, Sizing,
  Panel, Agent (starting and running tasks), AgentCard (the card, setups), Thread (the task's thread, live runs), HandOff
  (compact, start over, hand over), Limits, Changes (undo, changelist), OwnAnswers (the map), Works (the tasks at work
  side by side; edits go one run at a time per folder), Studio, Share (the Harness column's Share card: knowledge
  and team packs).
- `ChatPageWindow` the embedded ChatGPT page (WebView2). Never hide or minimize the window — it waits off screen, or
  ChatGPT is throttled to a few characters a second. Only the page inside rests, out of its engine's sight, while
  LINK does not need it (`IChatPage.Rest`); it is awake for every request.
- `companion/` the script that drives the ChatGPT DOM in the embedded page: `content.js` on `embedded-host.js`.
- Remote control: `RemoteControl` (on in Settings) runs `TelegramBot`, the user's bot, over `IRemoteHost`
  (`MainWindow.Remote`); a run's review window reaches the user by the bot's link through `RemoteServer` (127.0.0.1,
  `/view/<ticket>/`) and `RemoteTunnel` (cloudflared).
  No-stop is `MainWindow.NoStop` + `Services/NoStop`; spending is `UsageReport` (ccusage).
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
- Other sessions may edit the tree: stage explicit paths and check the commit's files. Commit from Bash with a
  heredoc (`git commit -F -`): PowerShell 5.1 splits a message with quotes. Push from PowerShell: Bash does not reach
  github.com:443.
- Update PROJECT_CONTEXT.md and README.md only for accepted decisions and user-visible changes.
