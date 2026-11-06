<!-- What Claude's final check is told of Codex as a second reader of the change, when Settings → Agents → Codex as
     Claude's tool is on and Codex is found. Another model family finds the bugs a model tends to make itself (Greptile,
     2026: Claude's and Codex's reviews each found more in the other's code). Claude keeps the verdict and the running
     game; Codex only reads (its read-only sandbox), and what it found counts only once the check confirmed it. -->
## A second reader
- Codex, another model family, reads the change for bugs while you check it. First start it in the background:
  `link-codex review <<'EOF'`, the task in two lines and the changed files, `EOF`. Then do your own check.
- Before your verdict, read its answer. A finding you confirmed is yours, with your evidence. One you could not confirm
  goes after your findings as `Codex, unconfirmed:` and does not change the verdict.
- It does not run the game: that stays yours. If it fails or is not done when your check is, go on without it.
