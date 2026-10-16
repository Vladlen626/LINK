# LINK Remote, No-stop and Usage — work plan

The user's request (2026-10-15): control Link from the phone while at work (write tasks, press Allow, answer the
choices), or from a browser on another computer; a **No-stop** checkbox that lets a task pick its own recommended
option and allow itself everything without stopping; integrate [ccusage](https://ccusage.com) where it improves the
flow. Works R.1–R.5 below. Status 2026-10-15: all five built in one session, checked by the tests and a headless page
check (skill `remote-page`); live runs, the phone over Tailscale and push wait for the user's test.

## Short

- **No-stop is Link-level.** Link's runs never wait mid-run: every stop is *between* runs (a `link-ask`, a decision
  card, a denial, the plan, a limit). No-stop answers those stops itself, inside a budget, and writes each answer
  into the timeline.
- **Remote is a web app Link serves, not an APK.** A PWA installed from Chrome is a home-screen app on Android and
  works in any browser on another computer. A native APK adds a toolchain, signing and updates for the same screens.
- **Reach from work: Tailscale, not a cloud relay.** Link listens on `127.0.0.1` only; `tailscale serve` gives the
  phone a private `https://<pc>.<tailnet>.ts.net` address. No port forwarding, no admin rights, no URL ACLs, no
  server of ours on the internet; HTTPS also lets the PWA install and push.
- **ccusage is the usage source for everything Link did not run itself**: all local Claude Code and Codex logs (the
  terminal sessions too), $ for Codex, days/weeks/months and the 5-hour block with burn rate and projection.

## 1. Decisions on open details

- No-stop is per task (a checkbox on the task card, also switchable on a live task and from the phone); a setting
  holds its default (off) and its budget.
- Remote is off by default; Settings → Remote turns it on, shows the address, the pairing code and the one
  Tailscale command.
- No new .NET dependency: `TcpListener` (HTTP, a long poll), `System.Text.Json`, `ECDiffieHellman`/`HKDF`/`AesGcm` for Web
  Push. ccusage runs as an external tool through `npx` (Node is already there for Claude Code), its version pinned.
- Rejected: Claude Code's own Remote Control (needs an interactive session, Link runs `-p`; one session per
  instance); a native Android app; a cloud relay; opening a LAN port (needs `netsh urlacl` and a firewall rule and
  stays reachable to everyone on the office Wi-Fi); re-implementing model prices (ccusage keeps the LiteLLM table).

## 2. How it works

**No-stop answers** (only when the task's box is on):
- `link-ask` → the first option (the one the agent recommends); multi-select → the first. A 10 s countdown on the
  card ("No-stop picks *X* in 10 s · Cancel") so the user at the PC can still step in.
- Plan first → "Run the plan". Team `AwaitOk` → OK. A denial → "Allow and retry" for this task only (never "Always
  allow"). A usage limit → continue after reset. `link-next` on an unfinished task → continue with its words and
  its effort.
- Permissions: Claude `--permission-mode bypassPermissions` with a hard deny list still passed as
  `--disallowedTools` (deny rules win over bypass); Codex `approval_policy=never`, sandbox stays `workspace-write`.
  The deny list is a setting, by default the irreversible outward actions: `git push`, `p4 submit`, `gh pr merge`,
  deleting outside the workspace.
- Stops anyway, as NeedsYou with the reason: 20 choices in a row (Settings' `NoStopTurns`), the same choice twice in a
  row, three runs without a changed file, Failed, or the user spoke. A usage limit is no stop: the task goes on a
  minute after the reset. No $ budget and no block projection: ccusage cannot know a subscription's limit, the CLIs'
  own limit reports can.

**Remote** (`Services/RemoteServer.cs`, `MainWindow.Remote.cs`):
- A `TcpListener` with plain HTTP/1.1 (`HttpListener` would refuse the Host name a tunnel forwards) on `http://127.0.0.1:<port>/`; every API request needs the device token (Authorization header). Pairing: the
  first visit asks for a 6-digit code shown in Link; the device gets a long random token, stored hashed with its
  name in settings; Settings lists devices and revokes them. Wrong codes are throttled.
- API, JSON: `GET /api/tasks` (project, goal, status, what it waits for), `GET /api/tasks/{id}` (the last turns'
  words, question, decision, limit, changed files, usage), `POST /api/tasks` (project + words + No-stop/Plan first),
  `POST /api/tasks/{id}/reply|decide|stop|nostop`, `GET /api/events?since=` (a long poll: a task changed),
  `GET /api/usage`, `GET|POST /api/push`.
- A facade on `MainWindow` (works stay private) marshals every call to the dispatcher and reuses the same paths the
  panel uses: `ContinueAgent`, `DecisionTaken`, `StopRequested`, the run request. Every remote action is written to
  link.log and the timeline as "from <device>".
- Out of scope remotely: screen capture, microphone, the ChatGPT chat, settings, file contents beyond the changed
  file list. Context sent to the AI stays an explicit local action.

**Phone and browser** (`remote/` beside `companion/`, built in as resources): one page, no framework — task list
with a "needs you" badge, a task view (turns, the question as buttons, the decision card, Stop, a reply box,
No-stop switch), New task, Usage. `manifest.json` + a service worker make it installable.

**Notifications:** Web Push with VAPID (keys made by Link once), sent when a task becomes NeedsYou or Done. Push
goes through the browser's own push service, so the PC needs the internet, nothing else.

**ccusage** (`Services/UsageReport.cs`): runs
`npx -y ccusage@20.0.34 daily --json --offline --since <month or week start> --sections daily,weekly,monthly,session
--by-agent` and `blocks --active --json --offline`, about 2.5 s each, side by side, when the Studio opens, after a
run (at most every 5 minutes) and on a click. One run covers every Claude account: their `projects` folders are the
user's own. Uses:
- The spent line under the usage bars (and on the phone): today and the month; the week, each agent and Claude's
  5-hour block (cost, rate, projection) on hover.
- A task's real cost by session id, Codex included (today Link shows "≈ $ at API prices" for Claude only).
- `agent-token-review` reads `ccusage session --json` before parsing logs by hand.

## 3. Works

### R.1 No-stop
`AgentTaskRecord.NoStop`, `AgentSettings.NoStopTurns`/`NoStopBudgetUsd`/`NoStopDeny` (+ `Normalize`, `Clone`);
the box after `PlanFirstBox` and on the live task; the auto-answers above in `MainWindow.Agent.cs` at the places
that today set NeedsYou or call `OfferChoice`; the provider flags; tests for picking, the stop rules and the flags.
Done when: a task with No-stop runs plan → work → its own `link-ask` → done without a click, and stops on budget.

### R.2 Remote server
Server, pairing, devices in settings, the API and the long poll over the facade, Settings → Remote; tests on the API with a
fake facade. Done when: `curl` with a token lists tasks, starts one, answers its question.

### R.3 Phone and browser app
The PWA over R.2's API. Done when: from the phone over Tailscale the user creates a task, answers a choice, allows
a denial, stops a run.

### R.4 Push
VAPID keys, subscription endpoint, encrypted payloads (RFC 8291), sent on NeedsYou/Done.

### R.5 ccusage
`UsageReport`, the spent line, per-task cost, the skill
update. Done when: the view matches `npx ccusage daily` in a terminal.

### R.6 Telegram bot (added 2026-10-15)
The phone runs Hiddify, and Android keeps one VPN at a time, so Tailscale on the phone means switching. `TelegramBot`
(in `RemoteControl`) long-polls the Bot API over `IRemoteHost`: one chat paired by a code, alerts with buttons
(options, decision, Stop, No-stop), words to the task, `/tasks /new /plan /auto /spent`. The token is kept with DPAPI.
Rejected for now: a relay on the user's VPS (more to run; Telegram already is one).

## 4. The user's part
- Install Tailscale on the PC and the phone, the same account; run once the command Link shows
  (`tailscale serve --bg <port>`); open the address on the phone, enter the code, "Add to Home screen".
- Risks: No-stop with bypass can do anything the deny list does not name — use it on projects under version
  control; a stolen phone is revoked in Settings; ccusage's first run downloads the package (pinned version).
