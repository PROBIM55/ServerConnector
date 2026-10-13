"""Render the approved monochrome Platform mark for Windows app/installer icons."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
source = root / "design" / "brand" / "platform-field-refined-v3.png"
assets = root / "Connector.Desktop" / "Assets"

with Image.open(source) as original:
    alpha = original.convert("RGBA").getchannel("A")
    mark = Image.new("RGBA", original.size, (255, 255, 255, 0))
    mark.putalpha(alpha)
    # The same monochrome rendering used by the approved kit's CSS.
    mark.save(assets / "platform_installer_logo.png")
    icon = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    ImageDraw.Draw(icon).rounded_rectangle((0, 0, 255, 255), radius=32, fill="#12171d")
    glyph = mark.resize((216, 216), Image.Resampling.LANCZOS)
    icon.alpha_composite(glyph, (20, 20))
    icon.save(assets / "platform_connector.ico", format="ICO",
              sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"Rendered approved Platform mark: {original.size}; alpha={alpha.getextrema()}")
