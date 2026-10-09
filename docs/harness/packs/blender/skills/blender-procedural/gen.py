# Assets from a generator: a Python file of the project (tools/blender/generators/<name>.py) that builds one asset
# from parameters and a seed, so that the hundredth rock is a seed and a preset, not a new modelling task.
# python blender_mcp.py py gen.py <generator.py> <seeds> [params] [options]
#   seeds:   7      - builds seed 7 into the collection <generator>_7 and keeps it (a rerun builds it anew);
#            1-12 or 3,7,19 - a sweep: each seed built, measured and shot alone, then taken away;
#            test   - the contract test: the default, every parameter at its minimum and at its maximum (seed 1),
#                     seeds 2-4, and the default built again to prove the seed alone decides the result
#   params:  "name=value,..." over the defaults; name=min or name=max puts one at an end of its range; a word alone is
#            a preset of PRESETS, or min / max for every parameter
#   options: "name=value;..." - views (default game), looks (default lit), style, cam, bg, thumb as shots.py takes them;
#            palette=<STYLE.md or list>;atlas=<png>;shade=flat|height - palette.py colours the built faces;
#            name=<collection> for a build; out=<file.gen.json> writes its record; keep=1 leaves a sweep's or a
#            test's builds in the scene, in a row along X past the scene's own objects
# The generator file:
#   PARAMS = {"name": (default, min, max, "unit", "what it changes"), ...}   an int default makes an int parameter
#   BUDGET = (min, max)          the triangles of one asset
#   PRESETS = {"name": {"param": value, ...}}   optional: settings that proved good
#   def build(p, rng):           makes the asset's objects in the current collection from p (the values by name);
#                                every random choice comes from rng (random.Random(seed))
#   def check(objects, p):       optional: the asset's invariants - the list of what is wrong, empty when they hold
# Helper modules beside the generator are imported anew on each run and count in its version.
# The answer: a build's record (generator, version, seed, params, triangles, size, problems) - also kept on its
# collection as the property "gen"; a sweep's or a test's rows and frames, and the sheet.py command that glues the
# frames into one lineup.
import bpy, hashlib, json, math, os, random, re, sys, tempfile
import numpy as np
from mathutils import Vector

BRIDGE = "ARGS" in globals()
ARGS = globals().get("ARGS") or (sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
path = os.path.abspath(ARGS[0])
seeds = ARGS[1] if len(ARGS) > 1 and ARGS[1] else "test"
spec = ARGS[2] if len(ARGS) > 2 else ""
options = dict(part.strip().split("=", 1) for part in (ARGS[3] if len(ARGS) > 3 else "").split(";") if "=" in part)
here = os.path.dirname(os.path.abspath(__file__))
SHOTS, SHEET = (os.path.normpath(os.path.join(here, "..", "blender-live", n)) for n in ("shots.py", "sheet.py"))
PALETTE = os.path.normpath(os.path.join(here, "..", "blender-model", "palette.py"))
folder, stem = os.path.normcase(os.path.dirname(path)), os.path.splitext(os.path.basename(path))[0]
FAR = Vector((0, 0, -10000))  # a sweep shoots each build down here, out of sight of the scene's own objects
scene, layer = bpy.context.scene, bpy.context.view_layer


def helpers():
    """The generator's own modules: those imported from its folder."""
    return sorted((name, m.__file__) for name, m in list(sys.modules.items())
                  if os.path.normcase(os.path.dirname(getattr(m, "__file__", None) or "")) == folder)


# An open Blender keeps imported modules: the generator's helpers are read anew, as the generator is.
for name, _ in helpers():
    del sys.modules[name]
if folder not in map(os.path.normcase, sys.path):
    sys.path.insert(0, os.path.dirname(path))
source = open(path, encoding="utf-8").read()
gen = {"__file__": path, "__name__": stem}
exec(compile(source, path, "exec"), gen)
PARAMS, BUDGET, PRESETS = gen.get("PARAMS"), gen.get("BUDGET"), gen.get("PRESETS") or {}

errors = []
if not isinstance(PARAMS, dict) or not PARAMS:
    errors.append('PARAMS = {"name": (default, min, max, "unit", "what it changes")} is missing')
else:
    for name, entry in PARAMS.items():
        if not (isinstance(entry, tuple) and len(entry) == 5 and all(isinstance(x, (int, float)) for x in entry[:3])
                and entry[1] <= entry[0] <= entry[2] and all(isinstance(x, str) and x for x in entry[3:])):
            errors.append(f"PARAMS[{name!r}] is not (default, min, max, unit, what it changes) with min <= default <= max")
if not (isinstance(BUDGET, tuple) and len(BUDGET) == 2 and 0 <= BUDGET[0] <= BUDGET[1]):
    errors.append("BUDGET = (min, max) triangles is missing")
if not callable(gen.get("build")):
    errors.append("def build(p, rng) is missing")
if errors:
    raise ValueError(f"{path}: " + "; ".join(errors))


def put(p, name, text):
    if name not in PARAMS:
        raise ValueError(f"no parameter {name}; there are {', '.join(PARAMS)}")
    default, low, high, unit = PARAMS[name][:4]
    v = low if text == "min" else high if text == "max" else float(text)
    if isinstance(default, int):
        if v != int(v):
            raise ValueError(f"{name} is a whole number, not {text}")
        v = int(v)
    if not low <= v <= high:
        raise ValueError(f"{name}={text} is outside {low}..{high} {unit}")
    p[name] = v


def resolve(text):
    p = {name: entry[0] for name, entry in PARAMS.items()}
    for token in filter(None, (t.strip() for t in text.split(","))):
        if "=" in token:
            put(p, *(s.strip() for s in token.split("=", 1)))
        elif token in ("min", "max"):
            for name in PARAMS:
                put(p, name, token)
        elif token in PRESETS:
            for name, v in PRESETS[token].items():
                put(p, name, str(v))
        else:
            raise ValueError(f"{token} is no preset; there are {', '.join(PRESETS) or 'none'}, and min and max")
    return p


def version():
    """The generator's and its helpers' source: a changed line is another version. Line ends do not count."""
    digest = hashlib.sha1(source.replace("\r\n", "\n").encode("utf-8"))
    for _, f in helpers():
        digest.update(open(f, encoding="utf-8").read().replace("\r\n", "\n").encode("utf-8"))
    return digest.hexdigest()[:12]


def remove(name):
    coll = bpy.data.collections.get(name)
    if not coll:
        return
    for o in list(coll.all_objects):
        data = o.data
        bpy.data.objects.remove(o)
        if isinstance(data, bpy.types.Mesh) and data.users == 0:
            bpy.data.meshes.remove(data)
    for child in list(coll.children_recursive):
        bpy.data.collections.remove(child)
    bpy.data.collections.remove(coll)


def build(name, seed, p):
    """Builds into a new collection of that name; the objects take its name, the only one the name itself."""
    remove(name)
    coll = bpy.data.collections.new(name)
    scene.collection.children.link(coll)
    active, before = layer.active_layer_collection, set(bpy.data.objects.keys())
    layer.active_layer_collection = layer.layer_collection.children[coll.name]
    try:
        gen["build"](dict(p), random.Random(seed))
    finally:
        layer.active_layer_collection = active
    made = [o for o in bpy.data.objects if o.name not in before and o.users_collection]
    for o in made:
        if coll not in o.users_collection:
            coll.objects.link(o)
        for other in o.users_collection:
            if other != coll:
                other.objects.unlink(o)
    for o in made:
        o.name = name if len(made) == 1 else f"{name}_{re.sub(r'[.]\d{3}$', '', o.name)}"
        if o.type == "MESH":
            o.data.name = o.name  # the engine names the mesh after it
    return made


def measure(objects):
    """Triangles, bounds and faults of the evaluated meshes (modifiers applied), and a hash of their geometry."""
    depsgraph = bpy.context.evaluated_depsgraph_get()
    tris, open_edges, flat, points, digest = 0, 0, 0, [], hashlib.sha1()
    for o in sorted((o for o in objects if o.type == "MESH"), key=lambda o: o.name):
        e = o.evaluated_get(depsgraph)
        m = e.to_mesh()
        m.calc_loop_triangles()
        tris += len(m.loop_triangles)
        co = np.empty(len(m.vertices) * 3)
        m.vertices.foreach_get("co", co)
        w = np.array(e.matrix_world)
        co = co.reshape(-1, 3) @ w[:3, :3].T + w[:3, 3]
        points.append(co)
        loops = {key: np.empty(len(m.loops), dtype=np.int32) for key in ("vertex_index", "edge_index")}
        for key, a in loops.items():
            m.loops.foreach_get(key, a)
        faces = {key: np.empty(len(m.polygons), dtype=np.int32) for key in ("loop_total", "material_index")}
        for key, a in faces.items():
            m.polygons.foreach_get(key, a)
        area = np.empty(len(m.polygons))
        m.polygons.foreach_get("area", area)
        # An edge of a closed surface has two faces: one is a hole's border, more a fin or a face inside.
        open_edges += int((np.bincount(loops["edge_index"], minlength=len(m.edges)) != 2).sum())
        flat += int((area < 1e-8).sum())
        for a in (np.round(co, 5) + 0.0, loops["vertex_index"], faces["loop_total"], faces["material_index"]):
            digest.update(a.tobytes())
        e.to_mesh_clear()
    if not points:
        return {"triangles": 0, "size_m": [0, 0, 0], "base_z": 0, "open_edges": 0, "zero_area_faces": 0, "hash": ""}
    points = np.concatenate(points)
    lo, hi = points.min(0), points.max(0)
    return {"triangles": tris, "size_m": [round(float(x), 3) for x in hi - lo], "base_z": round(float(lo[2]), 3),
            "open_edges": open_edges, "zero_area_faces": flat, "hash": digest.hexdigest()[:16],
            "nan": bool(np.isnan(points).any())}


def run(script, args):
    scope = {"ARGS": args, "__file__": script, "__name__": "__main__"}
    exec(compile(open(script, encoding="utf-8").read(), script, "exec"), scope)
    return scope.get("result") or {}


def problems(objects, p, stats):
    found = [] if any(o.type == "MESH" for o in objects) else ["no mesh was built"]
    if not BUDGET[0] <= stats["triangles"] <= BUDGET[1]:
        found.append(f"{stats['triangles']} triangles, outside the budget {BUDGET[0]}-{BUDGET[1]}")
    if stats["zero_area_faces"]:
        found.append(f"{stats['zero_area_faces']} faces of zero area")
    if stats.pop("nan", False):
        found.append("coordinates that are not numbers (NaN)")
    if callable(gen.get("check")):
        found += list(gen["check"](objects, dict(p)) or [])
    return found


def colour(objects):
    """palette.py on the build: the faces' colour materials become the atlas, as the game will show them."""
    if "palette" not in options:
        return []
    atlas = options.get("atlas") or os.path.join(tempfile.gettempdir(), "blender_gen", stem + "_palette.png")
    names = ",".join(o.name for o in objects if o.type == "MESH")
    unmatched = run(PALETTE, [names, options["palette"], atlas, options.get("shade", "flat")]).get("unmatched")
    return [f"materials outside the palette: {', '.join(unmatched)}"] if unmatched else []


def move(objects, delta):
    for o in objects:
        if o.parent not in objects:
            o.location += delta


def shoot(objects, label):
    names = ",".join(o.name for o in objects if o.type == "MESH")
    shot_options = ";".join(f"{k}={options[k]}" for k in ("style", "cam", "bg", "thumb") if k in options)
    return run(SHOTS, [names, options.get("views", "game"), "", f"{stem}_{label}", options.get("looks", "lit"),
                       shot_options])["files"]


def seed_list(text):
    found = []
    for part in text.split(","):
        part, step = (part.split(":") + ["1"])[:2]
        a, b = (part.split("-") + [part])[:2]
        found += list(range(int(a), int(b) + 1, int(step)))
    return found


if seeds.isdigit():
    seed, p = int(seeds), resolve(spec)
    name = options.get("name") or f"{stem}_{seed}"
    objects = build(name, seed, p)
    stats = measure(objects)
    found = problems(objects, p, stats) + colour(objects)
    result = {"generator": path, "version": version(), "seed": seed, "params": p, "triangles": stats["triangles"],
              "size_m": stats["size_m"], "hash": stats["hash"], "problems": found}
    bpy.data.collections[name]["gen"] = json.dumps(result)
    if "out" in options:
        with open(options["out"], "w", encoding="utf-8") as f:
            json.dump(result, f, ensure_ascii=False, indent=1)
    result = {"collection": name, "objects": [o.name for o in objects], **result}
else:
    test, p = seeds == "test", resolve(spec)
    variants = ([("default", 1, p), ("min", 1, resolve("min")), ("max", 1, resolve("max"))]
                + [(f"s{s}", s, p) for s in (2, 3, 4)]) if test else [(f"s{s}", s, p) for s in seed_list(seeds)]
    rows, files, hashes = [], [], {}
    # Kept builds stand in a row past the scene's own objects, not among them.
    x = max((c.x for o in scene.objects if o.type == "MESH" for c in (o.matrix_world @ Vector(b) for b in o.bound_box)),
            default=-1.0) + 1.0
    for label, seed, values in variants:
        name = f"{stem}_{label}"
        row = {"name": label, "seed": seed} | ({"params": values} if values != p else {})
        rows.append(row)
        try:
            objects = build(name, seed, values)
        except Exception as error:  # one failing corner of the range is a finding, not the end of the test
            remove(name)
            row["problems"] = [f"build failed: {type(error).__name__}: {error}"]
            continue
        stats = measure(objects)
        row["problems"] = problems(objects, values, stats)
        hashes[label] = stats.pop("hash")
        row |= stats
        move(objects, FAR)
        row["problems"] += colour(objects)
        files += shoot(objects, label)
        if "keep" in options:
            move(objects, Vector((x + stats["size_m"][0] / 2, 0, 0)) - FAR)
            x += stats["size_m"][0] * 1.5 + 0.5
        else:
            remove(name)
    result = {"generator": path, "version": version(), "params": p, "rows": rows}
    if test and "default" in hashes:
        again = build(f"{stem}_again", 1, p)
        result["deterministic"] = measure(again)["hash"] == hashes["default"]
        remove(f"{stem}_again")
        if not result["deterministic"]:
            result["problem"] = "seed 1 built twice gave two meshes: something random is outside rng"
        elif len({hashes.get(k) for k in ("default", "s2", "s3", "s4")}) == 1:
            result["problem"] = "seeds 1-4 gave one mesh: the seed changes nothing"
    if files:
        built = sum(1 for row in rows if "triangles" in row)
        per = len(files) // max(1, built)  # one build's frames stay side by side
        cols = per * max(1, round(math.sqrt(built / per)))
        sheet = os.path.join(os.path.dirname(files[0]), f"{stem}_{'test' if test else 'sweep'}.png")
        result["files"] = files
        result["sheet"] = f'uv run --no-project --with pillow "{SHEET}" "{sheet}" {cols} ' + " ".join(f'"{f}"' for f in files)
if not BRIDGE:
    print(json.dumps(result, ensure_ascii=False, indent=1))
