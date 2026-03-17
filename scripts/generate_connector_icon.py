from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(r"E:\00_Cursor\18_Server")
ASSETS = ROOT / "connector-desktop" / "Connector.Desktop" / "Assets"
PNG_PATH = ASSETS / "structura_logo.png"
ICO_PATH = ASSETS / "structura_connector.ico"


def rounded_rect(draw: ImageDraw.ImageDraw, box: tuple[int, int, int, int], radius: int, fill, outline=None, width: int = 1):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def draw_node_glow(base: Image.Image, box: tuple[int, int, int, int], radius: int, glow_color: tuple[int, int, int, int], blur: int):
    layer = Image.new("RGBA", base.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    draw.rounded_rectangle(box, radius=radius, fill=glow_color)
    layer = layer.filter(ImageFilter.GaussianBlur(blur))
    base.alpha_composite(layer)


def draw_connector_symbol(size: int = 1024) -> Image.Image:
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    glow = Image.new("RGBA", image.size, (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)

    center = size // 2
    top_y = 170
    bottom_y = 550
    node_size = 180
    half = node_size // 2
    left_x = center - 210
    right_x = center + 210
    top_x = center
    line_w = 26

    def node_box(cx: int, cy: int) -> tuple[int, int, int, int]:
        return (cx - half, cy - half, cx + half, cy + half)

    top_box = node_box(top_x, top_y + half)
    left_box = node_box(left_x, bottom_y + half)
    right_box = node_box(right_x, bottom_y + half)

    center_line_y = 465
    trunk_top = top_box[3] - 8
    trunk_bottom = center_line_y
    left_inner_x = left_box[0] + half
    right_inner_x = right_box[0] + half

    for width, alpha in ((78, 65), (48, 100)):
        glow_draw.line((top_x, trunk_top, top_x, trunk_bottom), fill=(59, 170, 255, alpha), width=width)
        glow_draw.line((left_inner_x, center_line_y, right_inner_x, center_line_y), fill=(59, 170, 255, alpha), width=width)

    image.alpha_composite(glow.filter(ImageFilter.GaussianBlur(38)))

    line_layer = Image.new("RGBA", image.size, (0, 0, 0, 0))
    line_draw = ImageDraw.Draw(line_layer)
    line_draw.line((top_x, trunk_top, top_x, trunk_bottom), fill=(68, 206, 255, 255), width=line_w)
    line_draw.line((left_inner_x, center_line_y, right_inner_x, center_line_y), fill=(68, 206, 255, 255), width=line_w)
    image.alpha_composite(line_layer)

    draw = ImageDraw.Draw(image)
    outer_fill = (46, 145, 255, 215)
    outer_outline = (105, 226, 255, 245)
    inner_fill = (24, 100, 220, 230)
    inner_outline = (137, 237, 255, 220)
    rim_outline = (5, 60, 205, 255)

    for box in (top_box, left_box, right_box):
        draw_node_glow(image, box, 28, (54, 172, 255, 180), 24)
        rounded_rect(draw, box, 28, outer_fill, outline=rim_outline, width=6)
        rounded_rect(draw, (box[0] + 10, box[1] + 10, box[2] - 10, box[3] - 10), 24, None, outline=outer_outline, width=5)
        rounded_rect(draw, (box[0] + 46, box[1] + 46, box[2] - 46, box[3] - 46), 14, inner_fill, outline=rim_outline, width=4)
        rounded_rect(draw, (box[0] + 52, box[1] + 52, box[2] - 52, box[3] - 52), 12, None, outline=inner_outline, width=3)

    highlight = Image.new("RGBA", image.size, (0, 0, 0, 0))
    highlight_draw = ImageDraw.Draw(highlight)
    for box in (top_box, left_box, right_box):
        highlight_draw.polygon(
            [
                (box[0] + 12, box[1] + 90),
                (box[0] + 70, box[1] + 20),
                (box[2] - 12, box[1] + 20),
                (box[2] - 12, box[1] + 130),
            ],
            fill=(255, 255, 255, 38),
        )
    image.alpha_composite(highlight)

    return image


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    image = draw_connector_symbol()
    image.save(PNG_PATH)
    image.save(ICO_PATH, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])
    print(f"Wrote {PNG_PATH}")
    print(f"Wrote {ICO_PATH}")


if __name__ == "__main__":
    main()
