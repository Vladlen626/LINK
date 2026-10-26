---
skills: blender-live, blender-model, blender-review, art-style
---
- Drive Blender headless: `blender -b <file>.blend --python <script>`.
- Model in metres at unit scale 1; apply transforms (Ctrl+A). Blender bakes OpenGL normals.
- The user reviews a model by marks in the review window (skill `blender-review`), also where the project's own skill
  has a frame review: publish after every build step and end each answer that shows a revision with its `link-page`
  block, which Link makes the button.
- FBX export: selected objects only, modifiers applied, Smoothing = Face, Add Leaf Bones off; Apply Scalings = FBX All for
  Unity (skill `blender-model`), FBX Units Scale for Unreal.
