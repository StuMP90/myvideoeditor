"""Generates the app icon and MSIX logo assets from code (no binary design source to maintain).

Usage: python3 tools/make_icons.py   (requires Pillow)
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
BG = (24, 28, 38, 255)
ACCENT = (255, 92, 72, 255)
TRACK_A = (74, 144, 226, 255)
TRACK_B = (64, 196, 140, 255)
FG = (230, 234, 242, 255)


def glyph(size: int, pad_ratio: float = 0.0, transparent: bool = False) -> Image.Image:
    """A play button over two timeline tracks."""
    scale = 4  # draw large and downsample, for smooth edges at small sizes
    big = size * scale
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = int(big * pad_ratio)
    inner = big - 2 * pad
    if not transparent:
        d.rounded_rectangle((pad, pad, big - pad - 1, big - pad - 1), radius=max(2, inner // 6), fill=BG)

    def box(x0, y0, x1, y1):
        return (pad + inner * x0, pad + inner * y0, pad + inner * x1, pad + inner * y1)

    r = inner // 28
    # Two timeline tracks with clips.
    d.rounded_rectangle(box(0.14, 0.64, 0.50, 0.73), radius=r, fill=TRACK_A)
    d.rounded_rectangle(box(0.54, 0.64, 0.86, 0.73), radius=r, fill=TRACK_A)
    d.rounded_rectangle(box(0.14, 0.78, 0.38, 0.87), radius=r, fill=TRACK_B)
    d.rounded_rectangle(box(0.42, 0.78, 0.86, 0.87), radius=r, fill=TRACK_B)
    # Playhead.
    d.rectangle(box(0.615, 0.58, 0.645, 0.92), fill=FG)
    # Play triangle.
    d.polygon([
        (pad + inner * 0.38, pad + inner * 0.13),
        (pad + inner * 0.38, pad + inner * 0.53),
        (pad + inner * 0.70, pad + inner * 0.33),
    ], fill=ACCENT)
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    assets = ROOT / "src" / "MyVideoEditor.App" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    glyph(256).save(assets / "myvideoeditor.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    glyph(256).save(assets / "myvideoeditor.png")

    msix = ROOT / "packaging" / "msix" / "Images"
    msix.mkdir(parents=True, exist_ok=True)
    glyph(50).save(msix / "StoreLogo.png")
    glyph(44).save(msix / "Square44x44Logo.png")
    glyph(44, transparent=True).save(msix / "Square44x44Logo.targetsize-44_altform-unplated.png")
    glyph(150, pad_ratio=0.18).save(msix / "Square150x150Logo.png")
    wide = Image.new("RGBA", (310, 150), BG)
    wide.alpha_composite(glyph(150, pad_ratio=0.18), (80, 0))
    wide.save(msix / "Wide310x150Logo.png")

    # Microsoft Store listing logos (Partner Center: 300x300 required, 150x150 and 71x71).
    store = ROOT / "packaging" / "store"
    store.mkdir(parents=True, exist_ok=True)
    for size in (300, 150, 71):
        glyph(size).save(store / f"StoreLogo-{size}x{size}.png")


if __name__ == "__main__":
    main()
