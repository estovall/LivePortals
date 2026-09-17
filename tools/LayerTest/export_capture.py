"""Turn a stored capture's PNGs into raw RGBA (rows bottom-up) next to copies of its grid and meta files, for
`dotnet run -c Release -- capture <outdir> <key> <outdir>`.
usage: python export_capture.py <BepInEx/config/LivePortals/world> <key> <outdir>"""
import glob, os, shutil, sys
from PIL import Image
src, key, out = sys.argv[1:4]
os.makedirs(out, exist_ok=True)
shutil.copy(os.path.join(src, key + ".txt"), out)
for b in glob.glob(os.path.join(src, key + "_p*.bin")):
    shutil.copy(b, out)
for p in glob.glob(os.path.join(src, key + "_p*.png")):
    im = Image.open(p).convert("RGBA").transpose(Image.FLIP_TOP_BOTTOM)
    open(os.path.join(out, os.path.basename(p)[:-4] + ".rgba"), "wb").write(im.tobytes())
