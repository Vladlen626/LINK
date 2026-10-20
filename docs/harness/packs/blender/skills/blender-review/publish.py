# Puts the asset into its review folder for the review window: check.py's numbers (report.json), the meshes as
# model.glb (modifiers applied, object names kept), the previous model kept as model.prev.glb for before/after, and
# state.json with the revision the window reloads on.
# blender -b <file.blend> --python publish.py -- <review dir> [objects] [check options] [--ref]
# python blender_mcp.py py publish.py <review dir> [objects] [check options] [--ref]   (the open Blender, blender-live)
#   review dir: the asset's folder for review (skill blender-review says where)
#   objects, check options: as check.py takes them ("*" and "size=1.8,4.5,?;tris=30000")
#   --ref: the reference ghost (ref.glb) instead, no check and no new revision: the objects, or a .glb file in their
#          place (Tripo and the like), scaled whole to the sizes given ("size=?,4.5,?") and stood on the ground at the
#          origin, its front kept where the file has it (-Y in Blender); the import is taken out of the scene again
# Prints check.py's lines and the revision. The selection and active object come back as they were.
import bpy, contextlib, io, json, os, runpy, sys, time
from mathutils import Matrix, Vector

ARGS = globals().get("ARGS") or (sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
as_ref = "--ref" in ARGS
ARGS = [a for a in ARGS if a != "--ref"]
if not ARGS:
    raise SystemExit("publish.py <review dir> [objects] [check options] [--ref]")
folder = os.path.abspath(ARGS[0])
names = ARGS[1] if len(ARGS) > 1 and ARGS[1] else "*"
checks = ARGS[2] if len(ARGS) > 2 else ""
here = os.path.dirname(os.path.abspath(globals().get("__file__") or sys.argv[sys.argv.index("--python") + 1]))
os.makedirs(folder, exist_ok=True)


def path(name):
    return os.path.join(folder, name)


@contextlib.contextmanager
def quiet():
    """The glTF add-on prints a line per object: kept from the agent's context unless it fails."""
    log = io.StringIO()
    try:
        with contextlib.redirect_stdout(log), contextlib.redirect_stderr(log):
            yield
    except Exception:
        print(log.getvalue()[-3000:])
        raise


def export(objects, name, materials="EXPORT"):
    """Writes the objects as <name>.new.glb: the caller puts it in place once it is whole."""
    view_layer = bpy.context.view_layer
    view_layer.update()  # objects removed by a script stay in the layer as None until it syncs
    selected = [o for o in view_layer.objects if o.select_get()]
    active = view_layer.objects.active
    try:
        for o in selected:
            o.select_set(False)
        for o in objects:
            o.select_set(True)
        view_layer.objects.active = objects[0] if objects else None
        with quiet():
            # Y up as glTF wants: the window turns points back into Blender's axes (x, -z, y) for the notes.
            bpy.ops.export_scene.gltf(filepath=path(name + ".new.glb"), export_format="GLB", use_selection=True,
                                      export_apply=True, export_yup=True, export_animations=False,
                                      export_materials=materials)
    finally:
        for o in objects:
            o.select_set(False)
        for o in selected:
            o.select_set(True)
        view_layer.objects.active = active


def imported(file, sizes):
    """A generated model (about a unit big, centred) as a ghost at the asset's size: its objects, to remove after."""
    before = set(bpy.data.objects)
    with quiet():
        bpy.ops.import_scene.gltf(filepath=file)
    new = [o for o in bpy.data.objects if o not in before]
    meshes = [o for o in new if o.type == "MESH"]
    bpy.context.view_layer.update()
    pts = [o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
    lo = Vector([min(p[a] for p in pts) for a in range(3)])
    hi = Vector([max(p[a] for p in pts) for a in range(3)])
    want = [None if c.strip() in ("", "?") else float(c) for c in sizes.split(",")] if sizes else []
    ratios = [want[a] / (hi[a] - lo[a]) for a in range(min(3, len(want))) if want[a] and hi[a] > lo[a]]
    s = sum(ratios) / len(ratios) if ratios else 1.0
    move = Matrix.Translation((-(lo.x + hi.x) / 2 * s, -(lo.y + hi.y) / 2 * s, -lo.z * s)) @ Matrix.Scale(s, 4)
    for o in new:
        if o.parent is None:
            o.matrix_world = move @ o.matrix_world
    print("reference %s scaled x%.3f to %.3f x %.3f x %.3f m" % (os.path.basename(file), s, *((hi - lo) * s)))
    return new, meshes


scene = bpy.context.scene
if as_ref and names.lower().endswith((".glb", ".gltf")):
    options = dict(p.strip().split("=", 1) for p in checks.split(";") if "=" in p)
    temporary, objects = imported(os.path.abspath(names), options.get("size", ""))
else:
    temporary = []
    objects = [o for o in scene.objects if o.type == "MESH" and o.visible_get()] if names == "*" else \
        [bpy.data.objects[n] for n in names.split(",")]

if as_ref:
    try:
        # The window paints it as a plain ghost: no materials, so a generated model's textures stay out of the file.
        export(objects, "ref", "NONE")
    finally:
        for o in temporary:
            data = o.data
            bpy.data.objects.remove(o)
            if isinstance(data, bpy.types.Mesh) and data.users == 0:
                bpy.data.meshes.remove(data)
    os.replace(path("ref.new.glb"), path("ref.glb"))
    print("reference written to %s" % path("ref.glb"))
    result = {"ref": path("ref.glb")}
else:
    state_file = path("state.json")
    state = json.load(open(state_file, encoding="utf-8")) if os.path.exists(state_file) else {"rev": 0}
    checked = runpy.run_path(os.path.join(here, "check.py"), init_globals={
        "ARGS": [names, ";".join(p for p in (checks, "out=" + path("report.new.json")) if p)]})
    export(objects, "model")
    # Only a finished export replaces the model: a failed one leaves the window on the last good revision.
    for name in ("model.glb", "report.json"):
        if os.path.exists(path(name)):
            os.replace(path(name), path(name.replace(".", ".prev.", 1)))
        os.replace(path(name.replace(".", ".new.", 1)), path(name))
    state.update({"rev": state.get("rev", 0) + 1, "time": time.strftime("%Y-%m-%dT%H:%M:%S"),
                  "objects": [o.name for o in objects], "blend": bpy.data.filepath,
                  "issues": checked["result"]["issues"], "tris": checked["result"]["tris"]})
    with open(state_file + ".tmp", "w", encoding="utf-8") as f:
        json.dump(state, f, ensure_ascii=False, indent=1)
    os.replace(state_file + ".tmp", state_file)
    print("published rev %d to %s" % (state["rev"], folder))
    result = {"rev": state["rev"], "issues": state["issues"], "tris": state["tris"]}
