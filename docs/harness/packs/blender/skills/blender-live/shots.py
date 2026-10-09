# Frames of objects from the open Blender: an OpenGL render through a temporary camera, without overlays.
# python blender_mcp.py py shots.py <objects> [views] [frames] [prefix]
#   objects: names joined by commas, or "*" for every visible mesh
#   views:   front,back,left,right,top,34 (default "front,left,34"); Blender's -Y is the front
#   frames:  "1,5" or "1-12" or "1-12:2" (default: the current frame)
# PNG -> %TEMP%/blender_shots/<prefix>_<view>_<frame>.png; the scene's camera, frame and render settings come back.
import bpy, os, tempfile
from mathutils import Vector

names = ARGS[0] if ARGS else "*"
views = (ARGS[1] if len(ARGS) > 1 else "front,left,34").split(",")
spec = ARGS[2] if len(ARGS) > 2 else str(bpy.context.scene.frame_current)
prefix = ARGS[3] if len(ARGS) > 3 else "shot"

frames = []
for part in spec.split(","):
    step = 1
    if ":" in part:
        part, step = part.split(":")[0], int(part.split(":")[1])
    if "-" in part:
        a, b = part.split("-")
        frames += list(range(int(a), int(b) + 1, step))
    else:
        frames.append(int(part))

scene = bpy.context.scene
objects = [o for o in scene.objects if o.type == "MESH" and o.visible_get()] if names == "*" else [bpy.data.objects[n] for n in names.split(",")]
DIRS = {"front": (0, -1, 0.15), "back": (0, 1, 0.15), "left": (-1, 0, 0.15), "right": (1, 0, 0.15),
        "top": (0, 0, 1), "34": (-0.7, -0.7, 0.45)}

out_dir = os.path.join(tempfile.gettempdir(), "blender_shots")
os.makedirs(out_dir, exist_ok=True)
saved = (scene.camera, scene.frame_current, scene.render.filepath, scene.render.resolution_x, scene.render.resolution_y, scene.render.engine)
cam_data = bpy.data.cameras.new("shots_cam")
cam_data.type = "ORTHO"
cam = bpy.data.objects.new("shots_cam", cam_data)
scene.collection.objects.link(cam)
files = []
try:
    scene.camera = cam
    scene.render.resolution_x, scene.render.resolution_y = 900, 900
    if bpy.app.background:
        scene.render.engine = "BLENDER_WORKBENCH"
    for f in frames:
        scene.frame_set(f)
        corners = [o.matrix_world @ Vector(c) for o in objects for c in o.bound_box]
        lo = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
        hi = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
        center, size = (lo + hi) / 2, max((hi - lo).length, 0.01)
        cam_data.ortho_scale = size * 1.1
        cam_data.clip_end = size * 20
        for view in views:
            direction = Vector(DIRS[view]).normalized()
            cam.location = center + direction * size * 4
            cam.rotation_euler = (-direction).to_track_quat("-Z", "Y").to_euler()
            path = os.path.join(out_dir, f"{prefix}_{view}_{f}.png")
            scene.render.filepath = path
            # A background Blender has no viewport to draw: it renders with Workbench instead.
            if bpy.app.background:
                bpy.ops.render.render(write_still=True)
            else:
                bpy.ops.render.opengl(write_still=True, view_context=False)  # else the GUI draws the viewport view, not this camera
            files.append(path)
finally:
    scene.camera, frame, scene.render.filepath, scene.render.resolution_x, scene.render.resolution_y, scene.render.engine = saved
    scene.frame_set(frame)
    bpy.data.objects.remove(cam)
    bpy.data.cameras.remove(cam_data)
result = {"files": files}
