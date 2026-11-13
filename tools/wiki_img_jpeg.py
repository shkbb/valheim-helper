"""Shrinks a wiki pack's pictures: PNG screenshots and renders without transparency become JPEG (quality 85).

python wiki_img_jpeg.py <img folder> [key prefix]
Only pictures whose key starts with the prefix (default "wg~", Weird Gloop) and that are larger than 40 KB are looked at;
index.tsv is updated to the new file names. Pictures with transparency stay PNG with a 256-colour palette. Needs Pillow.
"""
import os
import sys

from PIL import Image

folder = sys.argv[1]
prefix = sys.argv[2] if len(sys.argv) > 2 else 'wg~'
index_path = os.path.join(folder, 'index.tsv')
rows = [line.rstrip('\n').split('\t') for line in open(index_path, encoding='utf-8') if line.strip()]
saved = 0
changed = 0
for row in rows:
    if len(row) < 3 or not row[0].startswith(prefix) or not row[1].lower().endswith('.png'):
        continue
    path = os.path.join(folder, row[1])
    if not os.path.exists(path) or os.path.getsize(path) < 40_000:
        continue
    with Image.open(path) as image:
        image.load()
        if image.mode in ('RGBA', 'LA') or (image.mode == 'P' and 'transparency' in image.info):
            alpha = image.convert('RGBA').getchannel('A')
            if alpha.getextrema()[0] < 250:
                # really transparent: stays PNG, with a 256-colour palette
                small = image.convert('RGBA').quantize(256, method=Image.Quantize.FASTOCTREE)
                tmp = path + '.tmp'
                small.save(tmp, 'PNG', optimize=True)
                before, after = os.path.getsize(path), os.path.getsize(tmp)
                if after < before * 0.7:
                    os.replace(tmp, path)
                    saved += before - after
                    changed += 1
                else:
                    os.remove(tmp)
                continue
        rgb = image.convert('RGB')
    jpg_name = row[1][:-4] + '.jpg'
    jpg_path = os.path.join(folder, jpg_name)
    rgb.save(jpg_path, 'JPEG', quality=85, optimize=True)
    before = os.path.getsize(path)
    after = os.path.getsize(jpg_path)
    if after > before * 0.7:
        os.remove(jpg_path)
        continue
    os.remove(path)
    row[1] = jpg_name
    saved += before - after
    changed += 1
with open(index_path, 'w', encoding='utf-8', newline='\n') as out:
    for row in rows:
        out.write('\t'.join(row) + '\n')
print(f'{changed} pictures made smaller, {saved / 1e6:.1f} MB saved')
