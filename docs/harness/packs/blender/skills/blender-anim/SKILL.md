---
name: blender-anim
description: Character animation in Blender by script - the user's actions and the agent's, poses and clips built by rerunnable scripts, clips that match gameplay (no root motion, contacts on frames), checks by numbers then frames, wrist and IK pitfalls, deformation checks, FBX export of clips. Use for any animation or rig work in Blender.
---

# Animation in Blender

Work through the bridge of `blender-live`. Which actions a character has and whose they are is written in the
project's `docs/map.md`; the project's own builders and checks are named in its skills.

## The user's actions
- An action the user made is never changed or overwritten under its name: yours goes beside it under a new name, and
  the map says whose is which. Keep the originals the agents started from in a folder of their own.
- Your actions live only in the open Blender until the .blend is saved: save after each built group.

## Clips by script
- Build poses and clips in rerunnable scripts: a pose is all channels of the rig, a clip is written whole (every
  channel keyed, Euler without flips, events as pose markers, windows as `<Name>_Begin` / `_End` markers). Gameplay
  numbers (speeds, heights, offsets) are constants at the top of the script: when they change in the engine, change
  them there and rebuild.
- A manifest of clips (action → engine name, loop, root motion, montage) is read by export and import alike.
- A new group of clips: first one action through the whole path into the engine and play, then the rest at once.

## A clip that fits the game without edits
- Without root motion a clip follows the capsule the game moves: the planted feet and hands slide back in the clip at
  the capsule's speed, so they stand still in the world. A capsule moved by a curve (a climb) means the clip is built
  in the world of the obstacle and shifted by the same curve each frame; the engine plays it by gameplay time.
- Contact and lift-off fall exactly on frames of the clip; a swinging foot stays within ~0.965 of the leg's length
  from the hip.
- Loops: the last frame does not repeat the first; compare the seam by the bones' matrices, not Euler values (two Euler
  branches can be one pose).

## Check by numbers, then by frames
- Numbers first, in one run over any number of actions: reach of the limbs as a share of their length (a rig's IK
  limit is about 1.005 for arms and 1.02 for legs; over 1.03 the rig stretches a bone), the loop seam, the slip of a
  planted foot against the ground at the capsule's speed, a foot that comes in too fast before contact.
- Then frames of the key moments (`shots.py`, glued with `sheet.py`), from the views the game's camera has.

## Hands, wrists and IK
- A hand holding something: check the axis of the object and the direction of the fingers separately. A raised hand's
  Y should continue the forearm; Y pointing down turned the wrist ~180° already in Blender.
- Y along the forearm does not rule out a 180° twist around it: measure swing and twist against the rest matrices
  and keep the wrist anatomical. Interpolate fast changes in the forearm's space (quaternion slerp); IK controls in
  Euler need solving at quarter frames, or whole frames are right and a flip shows between them.
- Measure the IK target's real error: a reach check alone does not show a hand that falls short of its hold.

## Deformation
- Check on a copy of the .blend: a fixed set of FK probes for both sides (fingers too) before and after a change to
  topology or weights. For the game mesh check linear skinning and the mesh before the Armature modifier: subdivision
  after skinning and Preserve Volume hide defects. Look for inner caps, triple edges, weights of cuffs on the hand,
  UVs and normalised weights.
- After a full run, repeat only the failures.

## Export
- FBX of the active action only, baked animation on, NLA strips and All Actions off, Add Leaf Bones off, Apply Scalings
  `FBX Units Scale`, the project's frame rate. Events go beside the FBX (a JSON of the markers) when the engine side
  needs notifies. Unreal: the Unreal pack and the project's skills say how clips are imported.
