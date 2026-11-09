<p align="center">
  <img src="docs/images/overview.svg" width="880" alt="Link over Blender: the Chat tab with its chats, a question and its answer">
</p>

<h1 align="center">Link</h1>

<p align="center">
  <b>Ask about what is on your screen. Hand the work to an agent. Stay in your app.</b>
</p>

<p align="center">
  <img alt="Windows 11 x64" src="https://img.shields.io/badge/Windows%2011-x64-0078D4">
  <img alt=".NET 10, WPF" src="https://img.shields.io/badge/.NET%2010-WPF-512BD4">
  <img alt="Agents: Claude Code and Codex" src="https://img.shields.io/badge/agents-Claude%20Code%20%C2%B7%20Codex-D95E0E">
  <a href="LICENSE"><img alt="MIT license" src="https://img.shields.io/badge/license-MIT-3FB950"></a>
</p>

Link is one window for Windows with two tabs. You work in Unreal, Unity, Blender, an IDE or a browser; `Ctrl+Alt+Space`
brings Link up on its **Chat** tab, and your question goes to your ChatGPT conversation together with what you choose (a
frame of the window you work in, the clipboard, the project note, your voice). When the answer is work in your folder,
**Claude Code** or **Codex** does it on the **Studio** tab, under a harness that keeps them on track.

- **Ask in place.** Type or hold a key and talk; the Chat box shows what the model sees, and the answer streams into
  the chat's thread.
- **Talk.** Local speech recognition while you hold a key, answers read aloud.
- **Agents as conversations.** A task is one thread from the brief to the result: its plan, steps, pictures, changes
  and cost. Several tasks run side by side.
- **A harness that holds.** Rules, the project's map and skills, a guard that refuses waste, a check after every run
  and a review of the whole plan: the same for Claude and Codex.
- **Away from the desk.** A Telegram bot tells you when a task needs you and takes your answer.

## Quick start

1. You need Windows 11 x64 and a ChatGPT account (.NET comes inside the build). For agents:
   [Claude Code](https://docs.claude.com/en/docs/claude-code) and/or [Codex CLI](https://github.com/openai/codex) on
   your subscription. For voice: [Visual C++ Redistributable 2022 x64](https://aka.ms/vs/17/release/vc_redist.x64.exe).
2. Download `Link-<version>-win-x64.zip` (~100 MB) from [Releases](../../releases), unzip it to a folder of your own
   (not Program Files) and run `Link.exe`. The build is not signed: SmartScreen may warn, More info → Run anyway.
3. **Welcome to LINK** checks your agents. Then, on the **Chat** tab, the line under **New chat** opens Link's ChatGPT
   page: sign in once. **+** on the project tabs opens a folder as a project. Claude Code signs in from
   Settings → Agents → **Sign in**, Codex with `codex login`.

Optional: paste [docs/harness/chat-instructions.md](docs/harness/chat-instructions.md) into the instructions of the
ChatGPT project Link talks to.

## Ask

- **The Chat box.** Write, or hold `Ctrl+Alt+V` and talk; `Enter` sends. **ChatGPT · ‹chat› ▾** says where it goes (a
  chat, or the project's agent).
- **What goes along is in sight.** **Sees** shows the frame (a window, a monitor or a region; or a fresh frame of the
  window you work in on every send), and chips show the project note and the clipboard; `×` leaves one out. Above the
  answer, **Saw** lists what really went.
- **The answer** streams in as Markdown at the end of the chat's thread; **Listen** reads it aloud, **Check chat** takes
  it from the chat if it did not arrive. Under it, **→ Agent task**. An earlier answer opens whole with **Show whole**.
- **Chats on the left**, as in ChatGPT: the project's chats, **New chat** (with a summary of the current one if you
  like) and the line that opens Link's ChatGPT page. `Ctrl+K` goes to the list, `Ctrl+B` folds it.
- **Two tabs** (`Ctrl+1`, `Ctrl+2`): **Chat**, and **Studio** with the agents' tasks. Under them, a tab for each
  project; the lit one's menu opens its column (its note, folder, map, skills and check command) on either tab. `Esc`
  hides Link to the tray.
- ChatGPT runs in Link's own page (WebView2) off screen, so it types at full speed with no browser open.

## Agents on the Studio tab

<p align="center">
  <img src="docs/images/studio.svg" width="880" alt="Link on its Studio tab: the task list, a task's thread with its plan, and the Harness column">
</p>

- **Start a task** with `Ctrl+Enter` in the Chat box, **New task** in Studio (`Ctrl+N`) or **→ Agent task** under an
  answer. A card shows the brief, the agent, model, effort and access; nothing runs before **Run**. **Plan first**
  has the agent only read and propose. A card left without Run keeps its words and pasted pictures as a draft, also
  across a restart: the next **New task** starts from it.
- **Several projects at once.** Each project is a tab; a dot on it says its agent works or waits for you. The window
  shows one project's chats and tasks, and the agents of the others keep working.
- **Link picks the specialists.** From the brief, Link gives a task one to three specialists, the lead first, and at
  most four skills to read first: there is nothing to choose. Each specialist and each tool pack is a jellyfish on the
  card and on every run, pale while Link has only given it and bright once the agent opens its role or one of its
  skills. Hover for its skills, click to read its files in Link.
- **One task, one thread.** Each run shows its steps folded ("Ran 3 commands, read 4 files…"), the pictures it looked
  at, its answer, the files it changed and what it cost. The agent's own task list is the **Plan** at the top.
- **Talk while it works.** `Enter` is **Tell now**: Claude reads your words at its next step, nothing stops. Codex
  gets **Steer now**. `Tab` keeps your words for after the run; `↑` in an empty box brings back your last words.
  What waits shows above the box: words the agent has not read yet (gone once it reads them, "read your words" in
  the status line) and words for after the run, which **Take back** returns into the box.
- **Talk to the agents.** Hold the microphone on the card or in a task's box, or `Ctrl+Alt+V` on the Studio tab: your
  words go into the brief or the box in view, and `Enter` (`Ctrl+Enter` on the card) while you speak sends them once
  written. Over the task list push-to-talk turns to the Chat tab.
- **See it in Link.** A file an answer names, a jellyfish, a skill, the project's map and **Memory** (the map and
  Claude's notes on the project) open in Link's viewer over the tab: Markdown, pictures (a changed one beside its
  last commit), OBJ and STL models to turn with the mouse, and any text. Nothing opened there runs; `Esc` goes back.
  On the Studio tab, with a task open, pick words or click a point of a picture and write a comment: **Send to the
  task** gives them all to its agent as your next words, the pictures with them.
- **Checked work.** Your check command (a build, tests) runs after every run with edits; a failure goes back to the
  agent, twice at most. When a plan of three steps or more is done, **Check the work** has a fresh session review the
  whole diff step by step, and **Fix them** sends its findings back.
- **Steer the task.** **Hand off ▾** to the other agent, **Compact** (a new session from the task's state); at a
  usage limit, **Continue at 15:45**.
- **Review and undo.** **Review** opens the diff: comment on lines, **Send review**. **Undo agent changes** in Git;
  in Perforce and without a VCS Link shows what changed.
- **Access.** **Edit** asks before commands, **Auto** asks nothing. **Night mode**, for while you sleep, lets the agent do
  anything (push and submit too), takes its recommended choice after 10 s and keeps the computer awake while it works.
- **Spend** (`Ctrl+Shift+U`): the usage windows first (how much is used, this task's part, whether it lasts to the
  reset), then where a task's tokens went, tips to save with a **Fix**, the context curve and history; today's and this
  month's cost by [ccusage](https://ccusage.com).
- A Windows notification of an agent (it finished, failed or asks) opens its task on the Studio tab.

## The harness

What every agent session gets, shown in the **Harness** column (`Ctrl+Shift+H`):

| Part | What it does |
|---|---|
| **Rules** | `harness/agent-rules.md` beside `Link.exe`, at the start of every session, in any folder; editable without a rebuild. |
| **Project memory** | `docs/map.md` (what is where, how to build and check) and skills in `.agents/skills/<name>/SKILL.md`, one source for both agents. Agents keep them current; a long session that kept nothing is asked once, before it ends. |
| **Guard** | `Link.Guard`, Claude Code's hook, refuses waste with what to do instead: a big file read whole, a check's leftovers in the project, long waits, long inline scripts, the same picture twice, 15 searches without a change, edits to Link's data or the agents' settings. Codex gets the same rules as text. |
| **Roles and packs** | Specialists (Gameplay Developer, 3D Artist, QA…) and tool packs (Unreal, Unity, Godot, Blender, Cascadeur, Perforce) in `%USERPROFILE%\LinkPacks`, shared by all your games. **Share with all games** passes a lesson on. **Share ▾** in the Harness column saves what your agents learned to a `.linkpack` for a friend (its README says how to use it without Link), or your workspace's roles and skills for a teammate; **Open a pack…** takes either in. |
| **Helpers** | `mechanic` on Haiku for mechanical edits. On the ChatGPT subscription: `scout`, Codex bringing facts from the code or the web in a few lines instead of Claude's own search; Codex for clear pieces of code; Codex as the final check's second reader. Scout and review only read. |
| **Effort and context** | **Effort Auto** picks each run's effort and says why; one context budget for both agents; MCP servers only from the project's `.mcp.json`. |

More: [docs/LOOP_PLAN.md](docs/LOOP_PLAN.md) (how a task runs), [docs/PORTABLE_HARNESS_PLAN.md](docs/PORTABLE_HARNESS_PLAN.md)
(the same harness for other models), [bench/](bench/README.md) (Link Bench: harnesses and models on one small game).

## Away from the desk

A Telegram bot of your own: `@BotFather` → `/newbot` gives a token; Settings → Remote control → paste it, **Pair
Telegram**. Each task gets one card that updates quietly; when it needs you, a new card notifies with buttons (the
agent's options, Allow, Deny, Stop, Night mode, Changes as a `.patch`). Words or a photo answer the task, also while it
works. `/new`, `/plan`, `/night`, `/tasks`, `/spent`. A model's review window opens in any browser through a Cloudflare
quick tunnel while Link runs.

## Hotkeys

| Action | Default |
|---|---|
| Show or hide Link | `Ctrl+Alt+Space` |
| Push-to-talk | `Ctrl+Alt+V` |
| Send | `Ctrl+Alt+Enter` |
| Whole desktop · active window · region | `Ctrl+Alt+D` · `Ctrl+Alt+W` · `Ctrl+Alt+Shift+R` |
| New agent task (in the Chat box) | `Ctrl+Enter` |

All change in Settings. `F13`–`F24` work without modifiers: handy on a macro pad or a pedal.

## Build and test

```powershell
dotnet build Link.sln -c Release
dotnet run --project tests/Link.Desktop.Tests -c Release
src\Link.Desktop\bin\Release\net10.0-windows\Link.exe
```

The tests need no microphone and no real ChatGPT; the companion script runs on mock ChatGPT pages in headless Edge.
A running Link locks its exe: Exit it from the tray before you rebuild.

## Your data

Everything stays in `%LocalAppData%\Link`: `settings.json`, `projects.json` and `Projects\<id>\` (history, agent
tasks), `Screenshots\`, `Recordings\`, `Models\` (speech), `WebView2\` (the ChatGPT sign-in), `usage-history.json`,
`link.log` (timings and errors). A Gemini key for voice is encrypted for your Windows account.
Nothing is captured without your action or a mode you turned on.

## How it is built

```text
src/Link.Desktop/   the app: Models, Services (requests, voice, agents, harness), Windows (Win32), the windows
src/Link.Guard/     the guard: Claude Code's PreToolUse and Stop hook
companion/          the script that drives ChatGPT in Link's own page
docs/harness/       the agents' rules, roles and tasks, copied beside Link.exe
tests/              logic checks without a test framework; companion checks on mock pages
bench/              Link Bench
```

`IChatIntegration` keeps ChatGPT behind one interface ([companion/README.md](companion/README.md));
`AgentCoordinator` runs a task over `ClaudeCodeProvider` or `CodexProvider`.

## Limits

- **ChatGPT.** A very long chat answers slowly in Link and in ChatGPT alike: start a new one (**New chat**, with a
  summary if needed). Its page takes 650–750 MB right after an answer and about 450 MB at rest.
- **The ChatGPT page can change.** Link logs what it saw when it cannot read an answer.
- **Voice.** Models download on first use (Whisper Base 148 MB; turbo 574 MB with a Vulkan GPU). No echo
  cancellation: interrupt an answer with push-to-talk, not with your voice.
- **Agents.** Claude Code and Codex work in your folder; chat is ChatGPT only. Edits to one folder go one run at a
  time (no worktrees).
- **Screen.** Exclusive fullscreen and protected video capture black; Link does not attach such a frame.

> **Disclaimer.** An unofficial project, not affiliated with OpenAI or Anthropic. Link drives the ChatGPT web page in
> its own window on your behalf: this may conflict with OpenAI's terms and may break when ChatGPT changes. Agents work
> in your folder with the access you give them and spend your subscription. Use at your own risk, without warranty.

License: [MIT](LICENSE).
