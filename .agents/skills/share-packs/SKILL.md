---
name: share-packs
description: Give the tool packs' skills the user's agents learned (%USERPROFILE%\LinkPacks) to the repo, so colleagues get them with git and Link - which files changed, copy them into docs/harness/packs, keep the packs general. Use when the user wants to share skills or before a commit that should carry them.
---

# Share the packs' skills

How skills live: a pack's skill (`docs/harness/packs/<pack>/skills/<name>`) is the tool's know-how for every project.
Link copies the built-in packs into `%USERPROFILE%\LinkPacks` and replaces a file there with a newer Link's version
while it is still as Link put it (`.link-seeded.json`); one the user or a shared lesson changed stays theirs. An agent
keeps a general lesson in the project's copy of a pack skill, the user shares it after the task (Studio: "Share with
all games") into LinkPacks; what holds only for one project lives in that project's add-on `<pack>-project`.

## Steps

1. `& .agents\skills\share-packs\sync-packs.ps1` - the files of LinkPacks that are lessons, `new` or `changed`.
2. Read each diff before it goes to the repo: a pack stays general. No project paths, asset names, sizes or scripts of
   one game (they go to that project's `<pack>-project` add-on); no Russian-only text in a shared skill.
3. `& .agents\skills\share-packs\sync-packs.ps1 -Apply`, then `git diff docs/harness/packs`, fix what is not general
   in the repo's copy and copy the fix back to LinkPacks (same text on both sides: Link then owns it again).
4. Commit with the user's word (CLAUDE.md, Git). Colleagues get it with the next Link build: unchanged files update.

A project's copy of a pack skill that mixes both kinds (an earlier rule allowed it): move the general lessons into the
pack's skill, the rest into the project's add-on, and remove the copy, so the pack applies there again.
