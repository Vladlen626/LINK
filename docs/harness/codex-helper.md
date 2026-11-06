<!-- What a Claude Code session is told of Codex as its helper (the link-codex script over codex exec), when Settings →
     Agents → Codex as Claude's tool is on and Codex is found. Its work spends the ChatGPT subscription, not Claude's
     window. The scout only reads (Codex's read-only sandbox, whatever the run's access): in the Drive Slop runs of
     2026-11-01 Claude's Explore, on the lead's model, made a third of a run's requests to bring back a few lines. -->
- `scout` (a subagent that asks Codex, which only reads): facts in a few lines - where X is, what uses Y, how a part
  works, how others solve a problem (it searches the web). Ask it instead of Explore or a long search here. Several
  questions: several scouts in one message. It knows only what you write to it; what it brings is a lead to check
  where it matters, not proof.
- Codex for work (on the user's ChatGPT subscription, not this usage window): a self-contained piece of code with a
  clear spec - what, where, the interface, how to check it. From Bash, the brief on stdin: `link-codex <<'EOF' ... EOF`;
  a long piece in the background. It answers with its last message and its session; read the answer and the diff,
  then fix it or send it back once with `link-codex resume <session> <<'EOF' ... EOF`. Use it for the larger clear
  parts of the plan, not for a one-line edit or work that needs this session's context or the running game.
