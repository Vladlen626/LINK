# Frames of objects from the open Blender: an OpenGL render through a temporary camera, without overlays.
# python blender_mcp.py py shots.py <objects> [views] [frames] [prefix] [looks] [options]
#   objects: names joined by commas, or "*" for every visible mesh
#   views:   front,back,left,right,top,34,34r (default "front,left,34"); Blender's -Y is the front, 34 is the front
#            3/4 and 34r the rear 3/4 from the other side; and two as the player sees the object:
#     game   - the gameplay camera (option cam): perspective, at its distance and angle
#     thumb  - the object as small as a thumbnail (option thumb), from the gameplay camera's side: whether the
#              silhouette still reads and the detail is not too dense
#   frames:  "1,5" or "1-12" or "1-12:2" (default: the current frame)
#   looks:   lit,flat,matcap,wire,uv (default: the scene's shading as it is); a review sheet is
#            "front,left,back,34,34r,top 1 sheet lit,matcap,wire,uv"
#     lit    - solid shading with the material colours (a material's Base Color, or its image texture)
#     flat   - the same colours unshaded: the colour zones as an unlit or palette shader shows them
#     matcap - one grey matcap: the form alone, no colour to hide it
#     wire   - the wireframe (a temporary Wireframe modifier: a camera render draws no wire overlay)
#     uv     - a UV grid on every object (stretching and texel density)
#   options: "name=value;..." - cam=PITCH,FOV,DISTANCE[,YAW]: the gameplay camera, degrees down from level, vertical
#            field of view in degrees, metres to the object, degrees round from the front (default 45, front-left);
#            without it 30,50 at three times the object's size. bg=#RRGGBB the background; thumb=64 the thumbnail's
#            pixels. style=docs/art/STYLE.md takes them from the project's art profile (its front matter's camera,
#            background and thumbnail); an option given beside it wins.
# PNG -> %TEMP%/blender_shots/<prefix>_[<look>_]<view>_<frame>.png; the scene's camera, frame, render, colour and
# shading settings, modifiers and materials come back. A background Blender runs it too: --python shots.py -- <args>.
import bpy, math, os, sys, tempfile
from mathutils import Vector

# The bridge sets ARGS; a background Blender passes them after "--".
ARGS = globals().get("ARGS") or (sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
names = ARGS[0] if ARGS else "*"
views = (ARGS[1] if len(ARGS) > 1 and ARGS[1] else "front,left,34").split(",")
spec = ARGS[2] if len(ARGS) > 2 and ARGS[2] else str(bpy.context.scene.frame_current)
prefix = ARGS[3] if len(ARGS) > 3 and ARGS[3] else "shot"
looks = [look for look in (ARGS[4] if len(ARGS) > 4 else "").split(",") if look] or [""]
options = dict(part.strip().split("=", 1) for part in (ARGS[5] if len(ARGS) > 5 else "").split(";") if "=" in part)
if "style" in options:
    lines = open(options["style"], encoding="utf-8").read().replace("\r\n", "\n").split("\n")
    head = lines[1:lines.index("---", 1)] if lines and lines[0].strip() == "---" and "---" in lines[1:] else []
    fields = dict((key.strip(), value.strip()) for key, value in (line.split(":", 1) for line in head if ":" in line))
    for field, option in (("camera", "cam"), ("background", "bg"), ("thumbnail", "thumb")):
        if fields.get(field):
            options.setdefault(option, fields[field])

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
game = dict(zip(("pitch", "fov", "distance", "yaw"), (float(x) for x in options["cam"].split(",")))) if "cam" in options else {}
pitch, yaw = math.radians(game.get("pitch", 30)), math.radians(game.get("yaw", 45))
DIRS["game"] = DIRS["thumb"] = (-math.sin(yaw) * math.cos(pitch), -math.cos(yaw) * math.cos(pitch), math.sin(pitch))
thumb = int(options.get("thumb", 64))
shading = scene.display.shading


def colour(hex_text, prop):
    """#RRGGBB as the property takes it: a linear colour property gets the sRGB value converted."""
    srgb = [int(hex_text.lstrip("#")[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    if shading.bl_rna.properties[prop].subtype == "COLOR_GAMMA":
        return srgb
    return [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in srgb]


def show_base_colours():
    """Workbench draws a material's viewport colour, not its Base Color: for the frames the one takes the other."""
    undo = []
    for m in {m for o in objects for m in o.data.materials if m}:
        bsdf = next((n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None) if m.node_tree else None
        if bsdf and not bsdf.inputs["Base Color"].is_linked:
            old = tuple(m.diffuse_color)
            m.diffuse_color = tuple(bsdf.inputs["Base Color"].default_value)
            undo.append(lambda m=m, old=old: setattr(m, "diffuse_color", old))
    return undo


def set_look(look):
    """Sets the shading of a look; returns what puts the objects' modifiers and materials back."""
    undo = []
    shading.type, shading.light, shading.show_shadows, shading.show_cavity = "SOLID", "STUDIO", False, True
    shading.color_type = "TEXTURE"  # an image texture where a material has one, else its colour
    if look in ("lit", "flat"):
        undo += show_base_colours()
        if look == "flat":
            shading.light, shading.show_cavity = "FLAT", False
    elif look == "matcap":
        shading.light, shading.show_cavity = "MATCAP", False
        shading.color_type, shading.single_color = "SINGLE", (0.8, 0.8, 0.8)
    elif look == "wire":
        shading.color_type, shading.single_color = "SINGLE", (0.9, 0.9, 0.9)
        for o in objects:
            m = o.modifiers.new("__shots_wire", "WIREFRAME")
            m.thickness, m.use_replace, m.use_even_offset = 0.006, True, True
            undo.append(lambda o=o, m=m: o.modifiers.remove(m))
    elif look == "uv":
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
                 shading.show_cavity, shading.show_shadows, shading.background_type, tuple(shading.background_color))
view_settings = scene.view_settings
saved_colour = (view_settings.view_transform, view_settings.look, view_settings.exposure, view_settings.gamma)
cam_data = bpy.data.cameras.new("shots_cam")
cam = bpy.data.objects.new("shots_cam", cam_data)
scene.collection.objects.link(cam)
files = []
try:
    scene.camera = cam
    # The colours as they are: AgX, the default, shifts a palette's colours.
    view_settings.view_transform, view_settings.look, view_settings.exposure, view_settings.gamma = "Standard", "None", 0, 1
    if "bg" in options:
        shading.background_type, shading.background_color = "VIEWPORT", colour(options["bg"], "background_color")
    if bpy.app.background:
        scene.render.engine = "BLENDER_WORKBENCH"
    for f in frames:
        scene.frame_set(f)
        corners = [o.matrix_world @ Vector(c) for o in objects for c in o.bound_box]
        lo = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
        hi = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
        center, size = (lo + hi) / 2, max((hi - lo).length, 0.01)
        distance = game.get("distance", size * 3)
        for look in looks:
            undo = set_look(look) if look else []
            try:
                for view in views:
                    direction = Vector(DIRS[view]).normalized()
                    if view == "game":
                        cam_data.type, cam_data.sensor_fit, cam_data.angle_y = "PERSP", "VERTICAL", math.radians(game.get("fov", 50))
                        cam_data.clip_start, cam_data.clip_end = 0.05, distance + size * 20
                        cam.location = center + direction * distance
                        scene.render.resolution_x, scene.render.resolution_y = 960, 540
                    else:
                        cam_data.type, cam_data.ortho_scale = "ORTHO", size * (1.02 if view == "thumb" else 1.1)
                        cam_data.clip_start, cam_data.clip_end = 0.01, size * 20
                        cam.location = center + direction * size * 4
                        px = thumb if view == "thumb" else 900
                        scene.render.resolution_x, scene.render.resolution_y = px, px
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
    view_settings.view_transform, view_settings.look, view_settings.exposure, view_settings.gamma = saved_colour
    shading.type, shading.light = saved_shading[0], saved_shading[1]
    try:
        shading.studio_light = saved_shading[2]
    except TypeError:
        pass  # a light of another kind than the one that was set
    (shading.color_type, shading.single_color, shading.show_cavity, shading.show_shadows,
     shading.background_type, shading.background_color) = saved_shading[3:]
    bpy.data.objects.remove(cam)
    bpy.data.cameras.remove(cam_data)
result = {"files": files}
if "game" in views or "thumb" in views:
    result["game"] = {"pitch": math.degrees(pitch), "fov": game.get("fov", 50), "yaw": math.degrees(yaw),
                      "distance": round(distance, 2), "from": "cam option" if game else "default: no cam option"}
