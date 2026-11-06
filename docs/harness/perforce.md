<!-- What the agent is told in a Perforce workspace (a .p4config in the workspace or above), after its rules: open
     files the Perforce way. Link lists what changed by the files' sizes and times; the user submits in P4V. -->

## Perforce

This workspace is in Perforce (the user works in P4V): its files are read-only until opened.
- Before changing a file: `p4 edit <file>`. After creating one: `p4 add <file>`. To delete: `p4 delete <file>`; to
  rename or move: `p4 move <from> <to>` (after `p4 edit <from>`).
- Never clear the read-only attribute, and never `p4 submit`, `revert`, `shelve`, `sync`, `resolve`, `client` or
  `change`: the user reviews your opened files and submits them in P4V.
- `p4 opened`, `p4 diff`, `p4 fstat`, `p4 filelog` and `p4 where` only look; use them freely.
- p4 fails on a ticket or the server: stop and say so; the user signs in with P4V.
