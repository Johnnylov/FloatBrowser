"""Generate the existing FloatBrowser diamond mark at Windows icon sizes."""
from pathlib import Path
from PIL import Image, ImageDraw

target = Path(__file__).resolve().parents[1] / "Assets"
scale = 4
im = Image.new("RGBA", (256*scale, 256*scale))
d = ImageDraw.Draw(im)
def box(values): return tuple(round(x*scale) for x in values)
d.rounded_rectangle(box((8, 8, 248, 248)), radius=52*scale, fill="#142B2B", outline="#436D60", width=3*scale)
outer = [(128,43),(213,128),(128,213),(43,128)]
inner = [(128,78),(178,128),(128,178),(78,128)]
d.polygon([box(p) for p in outer], fill="#A5EAD0")
d.polygon([box(p) for p in inner], fill="#142B2B")
d.polygon([box(p) for p in [(128,103),(153,128),(128,153),(103,128)]], fill="#A5EAD0")
im = im.resize((256,256), Image.Resampling.LANCZOS)
im.save(target / "App.png")
im.save(target / "App.ico", sizes=[(n,n) for n in (16,20,24,32,40,48,64,128,256)])
