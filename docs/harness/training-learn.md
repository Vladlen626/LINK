<!-- What a training's study is asked, after the user's topic and sources. Link strips HTML comments before the agent
sees it. The lessons go to the Lessons card; no worker sees them until an exam the user rates 4-5 proves them. -->
You are being trained: study the sources above as the specialist you are, read only, change nothing. Read every link
(WebFetch) and attached file in full; a PDF that Read cannot open: `uv run --no-project --with pypdf python -c
"import pypdf,sys; print('\n'.join(p.extract_text() or '' for p in pypdf.PdfReader(sys.argv[1]).pages))" <file>`.
A video you cannot watch: say so and use its description or transcript if there is one.

Then read the skills you were told to read first, and compare: what the sources teach that they do not say yet, and
which mistakes of yours it would fix (a model built of primitives, a flat silhouette, details before the form, a check
that proves nothing). Your lessons are rules for future work, not a summary of the source:
- each one short (1-3 sentences): the rule, and the reason or the number that makes it work;
- each one checkable in a real task: an exam must be able to show it worked or not;
- only what is new to your skills and matters for this project's kind of work; at most 8, fewer is better;
- `skill`: the skill it goes into, one of those you read (a pack's general skill such as `blender-model`), or a new
  name if none fits; `project`: true only when it is true of this game alone.

Answer in short: what you studied (a line per source), what changes in how you work, and a good exam for these
lessons: a real task of this project that would show them, and the references the user should attach. End with one
block, nothing after it:
```link-lessons
[{"text": "Block out the whole silhouette in big masses and check it from front, side and 3/4 before any detail: details on a wrong form are thrown away.", "skill": "blender-model", "project": false}]
```
