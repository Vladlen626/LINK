# Frames from shots.py glued into one sheet with captions.
# uv run --no-project --with pillow sheet.py <out.png> <cols> <in1.png> <in2.png> ...
import os
import sys
from PIL import Image, ImageDraw

out, cols, ins = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
tiles = [Image.open(p).convert("RGB") for p in ins]
tw, th = tiles[0].size
rows = (len(tiles) + cols - 1) // cols
g = Image.new("RGB", (tw * cols, th * rows), "white")
dr = ImageDraw.Draw(g)
for i, (p, t) in enumerate(zip(ins, tiles)):
    x, y = (i % cols) * tw, (i // cols) * th
    g.paste(t, (x, y))
    dr.rectangle((x, y, x + tw - 1, y + th - 1), outline=(90, 90, 90))
    dr.text((x + 6, y + 4), os.path.splitext(os.path.basename(p))[0], fill=(255, 255, 0))
g.save(out)
print(out, g.size)
