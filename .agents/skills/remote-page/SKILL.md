---
name: remote-page
description: Look at Link's remote page (remote/, the phone and browser app) after changing it - serve it with a fake API and read what headless Edge draws, without Link or a phone.
---

# Remote page check

`remote/` is built into Link (`Remote.*` resources) and served by `RemoteServer`; its API is `IRemoteHost`
(`Services/RemoteApi.cs`), checked over real HTTP in `tests/Link.Desktop.Tests/RemoteTests.cs`. The page itself is
checked here, against `fake-link.js`, which answers like Link with one task waiting for an answer.

```bash
node .agents/skills/remote-page/fake-link.js remote 8799 &
EDGE="/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
for view in "" "?task" "?new"; do
  timeout 40 "$EDGE" --headless=new --disable-gpu --user-data-dir="$TEMP/link-remote-edge" --virtual-time-budget=3000 \
    --dump-dom "http://127.0.0.1:8799/$view" | sed 's/></>\n</g' | grep -E "<title>|<h1|<button|class=\"(goal|meta|card|error)"
done
```

- `<title>ERROR …` is a script error on the page; agents' words must show as text (`&lt;b&gt;`), never as HTML.
- `--dump-dom` comes only once the network is idle: the fake refuses the change feed (`/api/events`), so the page
  retries in 5 s, past the virtual time. A feed that answers, or never does, keeps Edge waiting until `timeout`.
- Notifications need HTTPS and a real browser: check them on the phone through `tailscale serve`.
- Stop the fake server after: `netstat -ano | grep ":8799 "` gives its PID for `taskkill //F //PID` (not every node.exe).
