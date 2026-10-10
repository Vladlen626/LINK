# Frames from shots.py glued into one sheet with captions.
# uv run --no-project --with pillow sheet.py <out.png> <cols> <in1.png> <in2.png> ...
# Each cell is the size of the first frame; a frame of another size is fitted into it, a small one (a thumbnail)
# enlarged by whole pixels so that its pixels stay as the player would see them. A PNG is marked as a sheet: Link then
# sends the critic the sheet as an image and its frames by path, not the same frames twice.
import os
import sys
from PIL import Image, ImageDraw, PngImagePlugin

out, cols, ins = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
tiles = [Image.open(p).convert("RGB") for p in ins]
tw, th = tiles[0].size
rows = (len(tiles) + cols - 1) // cols
g = Image.new("RGB", (tw * cols, th * rows), "white")
dr = ImageDraw.Draw(g)
for i, (p, t) in enumerate(zip(ins, tiles)):
    x, y = (i % cols) * tw, (i // cols) * th
    if t.size != (tw, th):
        scale = min(tw / t.width, th / t.height)
        small = scale >= 2
        if small:
            scale = int(scale)
        t = t.resize((max(1, int(t.width * scale)), max(1, int(t.height * scale))), Image.NEAREST if small else Image.LANCZOS)
        dr.rectangle((x, y, x + tw - 1, y + th - 1), fill=(128, 128, 128))
    g.paste(t, (x + (tw - t.width) // 2, y + (th - t.height) // 2))
    dr.rectangle((x, y, x + tw - 1, y + th - 1), outline=(90, 90, 90))
    dr.text((x + 6, y + 4), os.path.splitext(os.path.basename(p))[0], fill=(255, 255, 0))
mark = PngImagePlugin.PngInfo()
mark.add_text("Link", "sheet")
g.save(out, pnginfo=mark)
print(out, g.size)
