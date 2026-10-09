# Palette atlas: the project's colours in one small texture and one material, each face taking one colour, as
# palette-coloured games paint their assets: a family of assets shares the atlas, there is no texture to paint.
# python blender_mcp.py py palette.py <objects|*> <palette> <atlas.png> [mode] [material]
#   palette:  a STYLE.md whose front matter has "palette: name #RRGGBB, name #RRGGBB, ...", or that list itself
#   atlas:    the PNG to write, where the engine project takes it from; written over
#   mode:     flat (default) - each face takes its colour as it is; height - a face higher in its object takes a
#             lighter shade of its colour and a lower one a darker (a gradient over the object, as if lit from above)
#   material: the material the faces get (default M_Palette)
# A face's colour is named by its material: a material "rust" or "Rust.001" takes the palette's "rust". Faces of a
# material the palette lacks keep it and are listed. The faces' UVs go to the UV map "Palette", made the first map
# (the engine's UV0) and the active one; other maps follow it as they were, and their meshes are listed. Run it as the
# build script's last step, after the faces got their colour materials: a face it coloured has no colour name any more.
# The atlas: a column of 8 px per colour; its upper half the colour itself, its lower half a gradient from dark through
# the colour to light. Import it in the engine without filtering (Point) and without mipmaps, or colours bleed.
import bpy, bmesh, os, re, sys

ARGS = globals().get("ARGS") or (sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
names, source, atlas = ARGS[0], ARGS[1], os.path.abspath(ARGS[2])
mode = ARGS[3] if len(ARGS) > 3 and ARGS[3] else "flat"
material_name = ARGS[4] if len(ARGS) > 4 and ARGS[4] else "M_Palette"
if mode not in ("flat", "height"):
    raise ValueError(f"mode is flat or height, not {mode}")

text = source
if os.path.isfile(source):
    lines = open(source, encoding="utf-8").read().replace("\r\n", "\n").split("\n")
    head = lines[1:lines.index("---", 1)] if lines and lines[0].strip() == "---" and "---" in lines[1:] else []
    text = next((line.split(":", 1)[1] for line in head if line.startswith("palette:")), "")
palette = {}
for name, code in re.findall(r"([A-Za-z][\w-]*)\s+#([0-9A-Fa-f]{6})", text):
    palette.setdefault(name.lower(), tuple(int(code[i:i + 2], 16) / 255 for i in (0, 2, 4)))
if not palette:
    raise ValueError(f"no colours in {source}: write them as 'palette: name #RRGGBB, ...'")
order = list(palette)

CELL, HALF = 8, 16
width, height = CELL * len(order), HALF * 2
pixels = [0.0] * (width * height * 4)
for i, name in enumerate(order):
    for row in range(height):
        # Image rows count from the bottom: the gradient below, dark (0.6 of the colour) to light (1.4), the colour at its middle.
        factor = 1.0 if row >= HALF else 0.6 + 0.8 * row / (HALF - 1)
        rgb = [min(1.0, c * factor) for c in palette[name]]
        for x in range(i * CELL, (i + 1) * CELL):
            pixels[(row * width + x) * 4:(row * width + x) * 4 + 4] = (*rgb, 1.0)
image = bpy.data.images.get(os.path.basename(atlas))
if image and tuple(image.size) != (width, height):
    bpy.data.images.remove(image)
    image = None
image = image or bpy.data.images.new(os.path.basename(atlas), width, height)
image.pixels = pixels
os.makedirs(os.path.dirname(atlas), exist_ok=True)
image.filepath_raw, image.file_format = atlas, "PNG"
image.save()

material = bpy.data.materials.get(material_name) or bpy.data.materials.new(material_name)
nodes, links = material.node_tree.nodes, material.node_tree.links
bsdf = next((n for n in nodes if n.type == "BSDF_PRINCIPLED"), None) or nodes.new("ShaderNodeBsdfPrincipled")
texture = nodes.get("Palette") or nodes.new("ShaderNodeTexImage")
texture.name, texture.image, texture.interpolation = "Palette", image, "Closest"
uv_node = nodes.get("Palette UV") or nodes.new("ShaderNodeUVMap")
uv_node.name, uv_node.uv_map = "Palette UV", "Palette"
links.new(uv_node.outputs["UV"], texture.inputs["Vector"])
links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
bsdf.inputs["Roughness"].default_value = 1.0  # matte: the colour zones, not highlights, carry the form
nodes.active = texture  # Workbench frames show the active image

scene = bpy.context.scene
objects = [o for o in scene.objects if o.type == "MESH" and o.visible_get()] if names == "*" else [bpy.data.objects[n] for n in names.split(",")]
faces, unmatched, other_maps = {}, {}, []
for mesh in {o.data for o in objects}:
    slots = list(mesh.materials)
    colour_of = [re.sub(r"\.\d{3}$", "", m.name).lower() if m else "" for m in slots]
    keep = [m for m, colour in zip(slots, colour_of) if colour not in palette and m and m != material]
    new_slots = [material] + keep
    if any(uv.name != "Palette" for uv in mesh.uv_layers):
        other_maps.append(mesh.name)
    bm = bmesh.new()
    bm.from_mesh(mesh)
    uv = bm.loops.layers.uv.get("Palette") or bm.loops.layers.uv.new("Palette")
    zs = [v.co.z for v in bm.verts] or [0.0]
    low, span = min(zs), max(max(zs) - min(zs), 1e-6)
    indices = []
    for face in bm.faces:
        m = slots[face.material_index] if face.material_index < len(slots) else None
        colour = colour_of[face.material_index] if m else ""
        if m == material:
            indices.append(0)  # coloured by an earlier run: its UVs stay
        elif colour in palette:
            row = HALF + HALF // 2 if mode == "flat" else round((face.calc_center_median().z - low) / span * (HALF - 1))
            coords = ((order.index(colour) + 0.5) * CELL / width, (row + 0.5) / height)
            for loop in face.loops:
                loop[uv].uv = coords
            faces[colour] = faces.get(colour, 0) + 1
            indices.append(0)
        else:
            name = m.name if m else "(none)"
            unmatched[name] = unmatched.get(name, 0) + 1
            indices.append(new_slots.index(m) if m in new_slots else 0)
    bm.to_mesh(mesh)
    bm.free()
    # The engine and Workbench read the first and the active map: Palette goes first, the others after it as they were.
    others = [(layer.name, [c for loop in layer.data for c in loop.uv]) for layer in mesh.uv_layers if layer.name != "Palette"]
    for name, _ in others:
        mesh.uv_layers.remove(mesh.uv_layers[name])
    for name, data in others:
        mesh.uv_layers.new(name=name).data.foreach_set("uv", data)
    mesh.uv_layers["Palette"].active = mesh.uv_layers["Palette"].active_render = True
    # Clearing the slots resets every face's index: they are set again after.
    mesh.materials.clear()
    for m in new_slots:
        mesh.materials.append(m)
    mesh.polygons.foreach_set("material_index", indices)
    mesh.update()

result = {"atlas": atlas, "size": [width, height], "colours": order, "faces": faces, "material": material.name}
if unmatched:
    result["unmatched"] = unmatched
if other_maps:
    result["other_uv_maps"] = {"meshes": other_maps, "note": "kept after Palette; a texture of another material that read the first map now reads Palette"}
