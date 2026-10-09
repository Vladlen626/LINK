# A generator to copy, not to use: a plank crate. Copy it into the project's tools/blender/generators/ and make it
# the project's own there; the shapes, colours and numbers of a game come from its docs/art/STYLE.md.
# python blender_mcp.py py gen.py example_crate.py test
import bmesh, bpy, math
from mathutils import Matrix, Vector

PARAMS = {
    "width": (0.8, 0.4, 1.6, "m", "outer width and depth"),
    "height": (0.6, 0.3, 1.2, "m", "outer height"),
    "planks": (4, 2, 7, "count", "boards on each side, one above another"),
    "frame": (0.06, 0.03, 0.12, "m", "width of the corner and rim boards"),
    "skew": (0.02, 0.0, 0.06, "m", "how far a board end strays from true: hand-made, not CAD"),
}
BUDGET = (150, 1500)
PRESETS = {"small": {"width": 0.5, "height": 0.4, "planks": 3}, "tall": {"height": 1.0, "planks": 6}}


def material(name, rgb):
    """A colour material by name: palette.py maps the name to the project's palette."""
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.diffuse_color = (*rgb, 1)
    bsdf = m.node_tree.nodes.get("Principled BSDF") if m.node_tree else None
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (*rgb, 1)
    return m


def board(bm, size, matrix, slot):
    """A box: the block every part of the crate is made of."""
    made = bmesh.ops.create_cube(bm, size=1.0, matrix=matrix @ Matrix.Diagonal((*size, 1)))
    for face in {f for v in made["verts"] for f in v.link_faces}:
        face.material_index = slot


def build(p, rng):
    w, h, f = p["width"], p["height"], p["frame"]
    gap = 0.012
    n = min(p["planks"], max(1, int((h - 2 * f - gap) / (0.04 + gap))))  # as many as fit, each 4 cm at least
    plank = (h - 2 * f - gap * (n + 1)) / n
    bm = bmesh.new()
    # The dark box inside shows through the gaps: a shadow line drawn by geometry, with no texture.
    board(bm, (w - 2 * f, w - 2 * f, h - 0.02), Matrix.Translation((0, 0, h / 2)), 1)
    for side in range(4):
        turn = Matrix.Rotation(side * math.pi / 2, 4, "Z")
        for i in range(n):
            z = f + gap + plank / 2 + i * (plank + gap)
            tilt = rng.uniform(-1, 1) * p["skew"] / w  # radians: the board's end moves by about skew
            out = rng.uniform(0, p["skew"] / 3)
            # The boards sit back from the frame: its edge throws a line of shadow over them.
            place = turn @ Matrix.Translation((0, -w / 2 + 0.3 * f + f / 4 - out, z)) @ Matrix.Rotation(tilt, 4, "Y")
            board(bm, (w - 2 * f, f / 2, plank), place, 0)
        # Corner posts and the rim boards along the bottom and the top of this side.
        board(bm, (f, f, h), turn @ Matrix.Translation((-w / 2 + f / 2, -w / 2 + f / 2, h / 2)), 2)
        for z in (f / 2, h - f / 2):
            board(bm, (w - 2 * f, f * 0.8, f), turn @ Matrix.Translation((0, -w / 2 + f * 0.4, z)), 2)
    mesh = bpy.data.meshes.new("Crate")
    bm.to_mesh(mesh)
    bm.free()
    for m in (material("wood", (0.55, 0.38, 0.22)), material("wood_dark", (0.18, 0.12, 0.08)),
              material("wood_edge", (0.40, 0.26, 0.15))):
        mesh.materials.append(m)
    bpy.context.collection.objects.link(bpy.data.objects.new("Crate", mesh))


def check(objects, p):
    """The crate stands on the ground at its origin and keeps its size within the skew."""
    corners = [o.matrix_world @ Vector(c) for o in objects for c in o.bound_box]
    low = min(c.z for c in corners)
    wide = max(c.x for c in corners) - min(c.x for c in corners)
    found = [] if abs(low) < 0.002 else [f"the bottom is at z={low:.3f}, not on the ground"]
    if abs(wide - p["width"]) > p["skew"] + 0.01:
        found.append(f"{wide:.3f} m wide for width {p['width']}")
    return found
