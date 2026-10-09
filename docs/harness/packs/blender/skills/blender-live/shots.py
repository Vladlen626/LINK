# Frames of objects from the open Blender: an OpenGL render through a temporary camera, without overlays.
# python blender_mcp.py py shots.py <objects> [views] [frames] [prefix] [looks]
#   objects: names joined by commas, or "*" for every visible mesh
#   views:   front,back,left,right,top,34,34r (default "front,left,34"); Blender's -Y is the front, 34 is the front
#            3/4 and 34r the rear 3/4 from the other side
#   frames:  "1,5" or "1-12" or "1-12:2" (default: the current frame)
#   looks:   lit,matcap,wire,uv (default: the scene's shading as it is); a review sheet is
#            "front,left,back,34,34r,top 1 sheet lit,matcap,wire,uv"
#     lit    - solid shading with the material colours
#     matcap - one grey matcap: the form alone, no colour to hide it
#     wire   - the wireframe (a temporary Wireframe modifier: a camera render draws no wire overlay)
#     uv     - a UV grid on every object (stretching and texel density)
# PNG -> %TEMP%/blender_shots/<prefix>_[<look>_]<view>_<frame>.png; the scene's camera, frame, render and shading
# settings, modifiers and materials come back.
import bpy, os, tempfile
from mathutils import Vector

names = ARGS[0] if ARGS else "*"
views = (ARGS[1] if len(ARGS) > 1 else "front,left,34").split(",")
spec = ARGS[2] if len(ARGS) > 2 else str(bpy.context.scene.frame_current)
prefix = ARGS[3] if len(ARGS) > 3 else "shot"
looks = [look for look in (ARGS[4] if len(ARGS) > 4 else "").split(",") if look] or [""]

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
        "top": (0, 0, 1), "34": (-0.7, -0.7, 0.45), "34r": (0.7, 0.7, 0.45)}
shading = scene.display.shading


def set_look(look):
    """Sets the shading of a look; returns what puts the objects' modifiers and materials back."""
    undo = []
    shading.type, shading.light, shading.show_shadows, shading.show_cavity = "SOLID", "STUDIO", False, True
    shading.color_type = "MATERIAL"
    if look == "matcap":
        shading.light, shading.show_cavity = "MATCAP", False
        shading.color_type, shading.single_color = "SINGLE", (0.8, 0.8, 0.8)
    elif look == "wire":
        shading.color_type, shading.single_color = "SINGLE", (0.9, 0.9, 0.9)
        for o in objects:
            m = o.modifiers.new("__shots_wire", "WIREFRAME")
            m.thickness, m.use_replace, m.use_even_offset = 0.006, True, True
            undo.append(lambda o=o, m=m: o.modifiers.remove(m))
    elif look == "uv":
        shading.color_type = "TEXTURE"
        grid = bpy.data.images.new("__shots_uv_grid", 1024, 1024)
        grid.generated_type = "COLOR_GRID"
        undo.append(lambda: bpy.data.images.remove(grid))
        for o in objects:
            if not o.data.materials:
                temp = bpy.data.materials.new("__shots_uv")
                o.data.materials.append(temp)
                undo.append(lambda o=o, temp=temp: (o.data.materials.pop(), bpy.data.materials.remove(temp)))
        for m in {m for o in objects for m in o.data.materials if m}:
            if not m.use_nodes:
                m.use_nodes = True
                undo.append(lambda m=m: setattr(m, "use_nodes", False))
            node = m.node_tree.nodes.new("ShaderNodeTexImage")
            node.image, active = grid, m.node_tree.nodes.active
            m.node_tree.nodes.active = node
            undo.append(lambda m=m, node=node, active=active: (m.node_tree.nodes.remove(node), setattr(m.node_tree.nodes, "active", active)))
    return undo


out_dir = os.path.join(tempfile.gettempdir(), "blender_shots")
os.makedirs(out_dir, exist_ok=True)
saved = (scene.camera, scene.frame_current, scene.render.filepath, scene.render.resolution_x, scene.render.resolution_y, scene.render.engine)
saved_shading = (shading.type, shading.light, shading.studio_light, shading.color_type, tuple(shading.single_color),
                 shading.show_cavity, shading.show_shadows)
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
        for look in looks:
            undo = set_look(look) if look else []
            try:
                for view in views:
                    direction = Vector(DIRS[view]).normalized()
                    cam.location = center + direction * size * 4
                    cam.rotation_euler = (-direction).to_track_quat("-Z", "Y").to_euler()
                    path = os.path.join(out_dir, f"{prefix}_{look + '_' if look else ''}{view}_{f}.png")
                    scene.render.filepath = path
                    # A background Blender has no viewport to draw: it renders with Workbench instead.
                    if bpy.app.background:
                        bpy.ops.render.render(write_still=True)
                    else:
                        bpy.ops.render.opengl(write_still=True, view_context=False)  # else the GUI draws the viewport view, not this camera
                    files.append(path)
            finally:
                for step in reversed(undo):
                    step()
finally:
    scene.camera, frame, scene.render.filepath, scene.render.resolution_x, scene.render.resolution_y, scene.render.engine = saved
    scene.frame_set(frame)
    shading.type, shading.light = saved_shading[0], saved_shading[1]
    try:
        shading.studio_light = saved_shading[2]
    except TypeError:
        pass  # a light of another kind than the one that was set
    shading.color_type, shading.single_color, shading.show_cavity, shading.show_shadows = saved_shading[3:]
    bpy.data.objects.remove(cam)
    bpy.data.cameras.remove(cam_data)
result = {"files": files}
