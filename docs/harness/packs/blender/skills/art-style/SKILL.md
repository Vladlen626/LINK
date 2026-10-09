---
name: art-style
description: The game's own art style - the project's art profile in docs/art (STYLE.md with palette, gameplay camera, budgets and finish; REFERENCES.md with what each reference is for; approved assets), how a 3D model, texture or level follows it, the frames that judge style (gameplay camera, thumbnail, lineup with approved assets), and drafting a profile for a new game. Use for any art of a game.
---

# The game's art style

The skills are craft for every game; how this game looks lives in the project, in `docs/art/`, and nowhere else. A
project without `docs/art/STYLE.md` has no profile: follow the references and two or three existing assets of the
kind, and say in the answer that a profile is missing.

## The art profile
- `docs/art/STYLE.md` - a front matter with the numbers the tools read, then the style in words:
  ```
  ---
  palette: hull #C9B79C, rust #8B3A2B, sea #2F5D62, lamp #F2C14E
  camera: 35,40,18,45
  background: #1E2A30
  thumbnail: 64
  finish: faceted
  budgets: prop 150-800, building 800-3000, hero 3000-8000
  ---
  ```
  `camera` is the gameplay camera as `shots.py` takes it: degrees down, vertical field of view, metres away, degrees
  round from the front. `thumbnail`: the pixels of the object when it is small on screen. `finish`: `smooth` (bevels,
  smooth shading), `faceted` (hard edges, flat faces) or the project's own word. `budgets`: triangles per class.
  Sections: shape language (forms, proportions, exaggeration, asymmetry), colour and material (zones, what each
  palette colour is for, what draws light and shadow), detail by importance (hero, interactive, background), what
  never, and `## Rubric` - the style's checks, each one a frame can answer.
- `docs/art/REFERENCES.md` - each reference with its role: STYLE (the look: palette, edges, density), SHAPE
  (silhouette, proportions), MATERIAL (surfaces, colour zones), FUNCTION (construction, real sizes, how it works); what
  to take from it and what not to trust. A style reference never sets the construction; a drawn or AI image sets
  neither sizes nor the sides it does not show.
- `Art/Approved/<asset>/` - each asset the user approved: `gamecam.png` (its frame from the profile's camera), its
  record `.gen.json` if a generator made it, the rating. Approved assets of a class are the lineup the next is judged
  against; when the camera changes, their frames are rendered again.
- Generators in `tools/blender/generators/` (skill `blender-procedural`); the palette atlas where the engine takes it
  (Unity: under `Assets/`); this game's art know-how in `.agents/skills/<pack>-project/` (`blender-project`).

## Working to it
- Read STYLE.md and REFERENCES.md before the first stage. The profile's finish, palette, budgets and rubric win over
  any skill's defaults: a skill's bevels and smoothing do not go on a faceted game.
- Judge the style where the player sees it: `shots.py` with `style=docs/art/STYLE.md` and the views `game` (the
  gameplay camera) and `thumb` (does the silhouette read when small, is the detail not mush); front and side views
  judge the construction, not the style. Then the lineup: the `game` frame beside the `gamecam.png` of two approved
  assets of the class, in one sheet (`sheet.py`).
- Colour by zones on faces from the palette (skill `blender-model`, Palette), not by noise; the look `flat` shows
  whether the zones alone carry the form.
- Name the full paths of the frames and the sheet in the answer: a critic judges from them, without your account.
  Name the approved assets you compared with, and where yours differs on purpose.

## A new profile
- Asked to draft one: read the references the user gives, the game's existing assets, and what the developers of a
  reference game wrote about how they made it; write STYLE.md and REFERENCES.md with the numbers the tools need, and
  list what the user must confirm (palette, camera, budgets). Its rules are hypotheses until approved assets and exams
  prove them.
- What is learned about how this game looks goes into STYLE.md; how to build anything, into the pack's skills.
