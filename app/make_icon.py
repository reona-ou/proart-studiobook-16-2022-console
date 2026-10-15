import os
from PIL import Image, ImageDraw

W = H = 32
im = Image.new('RGBA', (W, H), (0, 0, 0, 0))
d = ImageDraw.Draw(im)

d.ellipse([1, 1, 30, 30], fill=(30, 90, 160, 255), outline=(120, 180, 230, 255), width=1)
d.ellipse([4, 4, 27, 27], fill=(22, 62, 112, 255))

box = [6, 6, 25, 25]
for k in range(3):
    a0 = k * 120 + 6
    a1 = a0 + 96
    d.pieslice(box, a0, a1, fill=(232, 240, 250, 235))

d.ellipse([13, 13, 18, 18], fill=(250, 252, 255, 255))

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'Assets')
os.makedirs(out, exist_ok=True)
p = os.path.join(out, 'tray.png')
im.save(p)
im.resize((64, 64), Image.LANCZOS).save(os.path.join(out, 'tray64.png'))
im.save(os.path.join(out, 'tray.ico'), format='ICO', sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64)])
print('wrote tray.ico')
print('wrote', p)
