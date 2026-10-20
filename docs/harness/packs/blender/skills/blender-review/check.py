# Numbers before frames: what an asset's meshes are, as exported (modifiers applied, world space, metres).
# blender -b <file.blend> --python check.py -- [objects] [options]
# python blender_mcp.py py check.py [objects] [options]          (the open Blender, skill blender-live)
#   objects: names joined by commas, or "*" (default) for every visible mesh
#   options: "name=value;..."
#     size=X,Y,Z  the target size of the whole set in metres, Blender's axes (X across, Y front to back, Z up);
#                 "?" for an axis without a target: size=1.8,4.5,?
#     tris=N      the triangle budget of the whole set
#     gap=0.001   metres: an island farther than this from every other island floats
#     out=path    also write the full report as JSON (publish.py writes report.json for the review window)
# Prints a few lines: the set against its targets, one line per object, then the issues worst first, each with a point
# to find it by. What it finds: islands floating apart from the rest (the commonest defect of a scripted model),
# non-manifold and open edges, inverted closed islands, degenerate faces, loose vertices and edges, n-gons, unapplied
# or negative scale. The scene is left as it was.
import bmesh, bpy, json, sys
from mathutils import Vector
from mathutils.bvhtree import BVHTree

ARGS = globals().get("ARGS") or (sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
names = ARGS[0] if ARGS and ARGS[0] else "*"
options = dict(part.strip().split("=", 1) for part in (ARGS[1] if len(ARGS) > 1 else "").split(";") if "=" in part)
GAP = float(options.get("gap", "0.001"))
SAMPLE = 400  # vertices per island tried against the others: enough to find a contact, cheap on a dense mesh


def r(v, n=3):
    return [round(c, n) for c in v]


def islands_of(bm):
    """Connected parts of a mesh: lists of vertex indices, the largest first."""
    seen, parts = set(), []
    for start in bm.verts:
        if start.index in seen:
            continue
        stack, part = [start], []
        seen.add(start.index)
        while stack:
            v = stack.pop()
            part.append(v.index)
            for e in v.link_edges:
                o = e.other_vert(v)
                if o.index not in seen:
                    seen.add(o.index)
                    stack.append(o)
        parts.append(part)
    return sorted(parts, key=len, reverse=True)


def measure(obj, depsgraph):
    """One object as it exports: its counts, issues and islands (each with a BVH tree for the contact test)."""
    ev = obj.evaluated_get(depsgraph)
    me = ev.to_mesh()
    bm = bmesh.new()
    bm.from_mesh(me)
    ev.to_mesh_clear()
    bm.transform(obj.matrix_world)
    bm.verts.ensure_lookup_table()
    bm.faces.ensure_lookup_table()
    bm.verts.index_update()
    tris = bm.calc_loop_triangles()
    issues = []

    def where(elems):
        pts = [e.co if isinstance(e, bmesh.types.BMVert) else
               (e.verts[0].co + e.verts[1].co) / 2 if isinstance(e, bmesh.types.BMEdge) else
               e.calc_center_median() for e in elems[:200]]
        return r(sum(pts, Vector()) / len(pts)) if pts else None

    open_edges = [e for e in bm.edges if len(e.link_faces) == 1]
    bad_edges = [e for e in bm.edges if len(e.link_faces) > 2]
    loose_edges = [e for e in bm.edges if not e.link_faces]
    loose_verts = [v for v in bm.verts if not v.link_edges]
    degenerate = [f for f in bm.faces if f.calc_area() < 1e-10]
    ngons = sum(1 for f in bm.faces if len(f.verts) > 4)
    if bad_edges:
        issues.append({"kind": "non-manifold", "object": obj.name, "count": len(bad_edges), "at": where(bad_edges),
                       "text": "%d edges shared by 3+ faces" % len(bad_edges)})
    if degenerate:
        issues.append({"kind": "degenerate", "object": obj.name, "count": len(degenerate), "at": where(degenerate),
                       "text": "%d faces of zero area" % len(degenerate)})
    if loose_edges or loose_verts:
        n = len(loose_edges) + len(loose_verts)
        issues.append({"kind": "loose", "object": obj.name, "count": n, "at": where(loose_verts or loose_edges),
                       "text": "%d vertices or edges without faces" % n})
    s = obj.matrix_world.to_scale()
    if min(s) < 0:
        issues.append({"kind": "negative-scale", "object": obj.name, "at": r(obj.matrix_world.translation),
                       "text": "negative scale %s: mirror the mesh data, not the scale" % r(s)})
    elif max(abs(c - 1) for c in s) > 1e-4:
        issues.append({"kind": "unapplied-scale", "object": obj.name, "at": r(obj.matrix_world.translation),
                       "text": "scale %s not applied" % r(s)})

    face_island = {}
    islands = []
    for i, part in enumerate(islands_of(bm)):
        vset = set(part)
        faces = [f for f in bm.faces if f.verts[0].index in vset]
        for f in faces:
            face_island[f.index] = i
        pts = [bm.verts[k].co for k in part]
        lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
        hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
        closed = all(len(e.link_faces) == 2 for f in faces for e in f.edges)
        tree = BVHTree.FromPolygons([bm.verts[k].co.copy() for k in range(len(bm.verts))],
                                    [[v.index for v in f.verts] for f in faces]) if faces else None
        islands.append({"object": obj.name, "index": i, "verts": part, "faces": len(faces), "lo": lo, "hi": hi,
                        "closed": closed, "tree": tree, "volume": 0.0})
    for lt in tris:
        i = face_island.get(lt[0].face.index)
        if i is not None:
            a, b, c = (l.vert.co for l in lt)
            islands[i]["volume"] += a.dot(b.cross(c)) / 6
    for isl in islands:
        if isl["closed"] and isl["faces"] and isl["volume"] < 0:
            issues.append({"kind": "inverted", "object": obj.name, "at": r((isl["lo"] + isl["hi"]) / 2),
                           "text": "island %d is closed and its normals point inward" % isl["index"]})
    if open_edges and len(open_edges) * 20 > len(bm.edges):
        issues.append({"kind": "open", "object": obj.name, "count": len(open_edges), "at": where(open_edges),
                       "text": "%d open boundary edges: holes, or a surface meant to be one-sided" % len(open_edges)})
    info = {"object": obj.name, "tris": len(tris), "verts": len(bm.verts), "faces": len(bm.faces), "ngons": ngons,
            "islands": len(islands), "open_edges": len(open_edges), "materials": [m.name for m in obj.data.materials if m],
            "origin": r(obj.matrix_world.translation)}
    coords = [v.co.copy() for v in bm.verts]
    bm.free()
    return info, issues, islands, coords


def floating(islands, coords):
    """Islands touching no other island within GAP, with how far the nearest one is. Each object's largest island is
    its own part (a wheel in its arch, a door in its gap), so only the others are judged: a handle hovering off its
    door, a panel left beside the body."""
    found = []
    if len(islands) < 2:
        return found
    main = {}
    for isl in islands:
        if isl["object"] not in main or isl["faces"] > main[isl["object"]]["faces"]:
            main[isl["object"]] = isl
    for isl in islands:
        if isl is main[isl["object"]] or not isl["faces"]:
            continue
        pts = [coords[isl["object"]][k] for k in isl["verts"]]
        step = max(1, len(pts) // SAMPLE)
        pts = pts[::step]
        best = None
        for other in islands:
            if other is isl or other["tree"] is None:
                continue
            if any(isl["lo"][a] - other["hi"][a] > (best if best is not None else 1e9) or
                   other["lo"][a] - isl["hi"][a] > (best if best is not None else 1e9) for a in range(3)):
                continue
            if isl["tree"] and isl["tree"].overlap(other["tree"]):  # sunk into the other part: attached
                best = 0.0
                break
            for p in pts:
                hit = other["tree"].find_nearest(p)
                if hit[0] is not None and (best is None or hit[3] < best):
                    best = hit[3]
                    if best <= GAP:
                        break
            if best is not None and best <= GAP:
                break
        if best is None or best > GAP:
            found.append({"kind": "floating", "object": isl["object"], "at": r((isl["lo"] + isl["hi"]) / 2),
                          "gap": round(best, 4) if best is not None else None,
                          "text": "island %d (%d faces) touches nothing; nearest part %s" % (
                              isl["index"], isl["faces"], "%.0f mm away" % (best * 1000) if best is not None else "none")})
    return found


def target(text):
    return [None if c.strip() in ("", "?") else float(c) for c in text.split(",")]


def main():
    scene = bpy.context.scene
    objects = [o for o in scene.objects if o.type == "MESH" and o.visible_get()] if names == "*" else \
        [bpy.data.objects[n] for n in names.split(",")]
    depsgraph = bpy.context.evaluated_depsgraph_get()
    rows, issues, islands, coords = [], [], [], {}
    for obj in objects:
        info, found, isl, co = measure(obj, depsgraph)
        rows.append(info)
        issues += found
        islands += isl
        coords[obj.name] = co
    issues = floating(islands, coords) + issues
    lo = Vector([min(i["lo"][a] for i in islands) for a in range(3)]) if islands else Vector()
    hi = Vector([max(i["hi"][a] for i in islands) for a in range(3)]) if islands else Vector()
    size = hi - lo
    tris = sum(row["tris"] for row in rows)
    report = {"objects": rows, "issues": issues, "size": r(size), "min": r(lo), "max": r(hi), "tris": tris}

    head = "check: %d objects, %d tris" % (len(rows), tris)
    if "tris" in options:
        budget = int(options["tris"])
        report["tris_budget"] = budget
        head += " (budget %d: %s)" % (budget, "ok" if tris <= budget else "OVER by %d" % (tris - budget))
    head += ", size %.3f x %.3f x %.3f m" % tuple(size)
    if "size" in options:
        want = target(options["size"])
        report["size_target"] = want
        diffs = ["%s %+.0f%%" % ("XYZ"[a], (size[a] / want[a] - 1) * 100) for a in range(3) if want[a]]
        head += " (target %s: %s)" % (options["size"], ", ".join(diffs))
    lines = [head]
    for row in rows:
        lines.append("  %s: %d tris, %d islands%s%s" % (
            row["object"], row["tris"], row["islands"], ", %d n-gons" % row["ngons"] if row["ngons"] else "",
            ", %d open edges" % row["open_edges"] if row["open_edges"] else ""))
    lines.append("issues: none" if not issues else "issues (%d):" % len(issues))
    for it in issues[:20]:
        lines.append("  %s %s at %s: %s" % (it["kind"], it["object"], it.get("at"), it["text"]))
    if len(issues) > 20:
        lines.append("  ... %d more in the JSON report" % (len(issues) - 20))
    if "out" in options:
        with open(options["out"], "w", encoding="utf-8") as f:
            json.dump(report, f, ensure_ascii=False, indent=1)
    return report, lines


report, lines = main()
print("\n".join(lines))
result = {"issues": len(report["issues"]), "tris": report["tris"], "size": report["size"]}
