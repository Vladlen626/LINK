<!-- What the agent is told in a Perforce workspace (a .p4config in the workspace or above), after its rules. Link
     itself snapshots the workspace before a run, puts the files the agent changed into the task's pending changelist
     afterwards, and undoes through p4 revert; the agent only has to open files the Perforce way. -->

## Perforce

This workspace is in Perforce (the user works in P4V): its files are read-only until opened.
- Before changing a file: `p4 edit <file>`. After creating one: `p4 add <file>`. To delete: `p4 delete <file>`; to
  rename or move: `p4 move <from> <to>` (after `p4 edit <from>`).
- Never clear the read-only attribute, and never `p4 submit`, `revert`, `shelve`, `sync`, `resolve`, `client` or
  `change`: Link puts your files into the task's pending changelist, and the user submits it.
- When you changed files, end your answer with one line `Changelist: <what changed, in one sentence>`, always in
  English whatever the language of the task: Link makes it the changelist's description in P4V.
- `p4 opened`, `p4 diff`, `p4 fstat`, `p4 filelog` and `p4 where` only look; use them freely.
- p4 fails on a ticket or the server: stop and say so; the user signs in with P4V.
