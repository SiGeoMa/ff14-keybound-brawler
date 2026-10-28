"""生成 KeyboundBrawler 插件图标（128x128 PNG）。

主题：金碟「魔光键影」——深色圆角底 + 金色键帽 + 光晕 + K 字。
"""

from __future__ import annotations

import math
import os

from PIL import Image, ImageDraw, ImageFilter, ImageFont

SIZE = 128
OUT = os.path.join(os.path.dirname(__file__), "..", "icon.png")
OUT_PLUGIN = os.path.join(os.path.dirname(__file__), "..", "dalamud-plugin", "KeyboundBrawler", "icon.png")


def rounded_rect_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return mask


def vertical_gradient(size: int, top: tuple[int, int, int], bottom: tuple[int, int, int]) -> Image.Image:
    grad = Image.new("RGBA", (size, size))
    pixels = grad.load()
    for y in range(size):
        t = y / max(1, size - 1)
        r = int(top[0] + (bottom[0] - top[0]) * t)
        g = int(top[1] + (bottom[1] - top[1]) * t)
        b = int(top[2] + (bottom[2] - top[2]) * t)
        for x in range(size):
            pixels[x, y] = (r, g, b, 255)
    return grad


def main() -> None:
    size = SIZE
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    # 背景：深紫黑圆角
    bg = vertical_gradient(size, (28, 24, 52), (12, 10, 24))
    mask = rounded_rect_mask(size, 26)
    img.paste(bg, (0, 0), mask)

    # 金色光晕（中心偏下）
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    for radius in range(66, 6, -2):
        alpha = int(70 * (1 - radius / 66) ** 2)
        gd.ellipse(
            (size / 2 - radius, size / 2 + 10 - radius, size / 2 + radius, size / 2 + 10 + radius),
            fill=(255, 200, 70, alpha),
        )
    glow = glow.filter(ImageFilter.GaussianBlur(6))
    img = Image.alpha_composite(img, glow)

    # 键帽：金色圆角矩形（带 3D 顶面）
    key_w, key_h = 72, 64
    kx = (size - key_w) / 2
    ky = (size - key_h) / 2 - 6

    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    sd.rounded_rectangle((kx + 4, ky + 10, kx + key_w + 4, ky + key_h + 10), radius=14, fill=(0, 0, 0, 140))
    shadow = shadow.filter(ImageFilter.GaussianBlur(5))
    img = Image.alpha_composite(img, shadow)

    # 键帽侧面
    draw = ImageDraw.Draw(img)
    draw.rounded_rectangle((kx, ky + 6, kx + key_w, ky + key_h + 6), radius=14, fill=(150, 105, 20, 255))
    # 键帽顶面渐变
    top = vertical_gradient(key_w, (255, 214, 96), (232, 168, 40)).resize((key_w, key_h))
    top_mask = Image.new("L", (key_w, key_h), 0)
    ImageDraw.Draw(top_mask).rounded_rectangle((0, 0, key_w - 1, key_h - 1), radius=13, fill=255)
    img.paste(top, (int(kx), int(ky)), top_mask)

    # 顶面高光
    hl = Image.new("RGBA", (key_w, key_h), (0, 0, 0, 0))
    ImageDraw.Draw(hl).rounded_rectangle((6, 4, key_w - 6, key_h // 2), radius=10, fill=(255, 255, 255, 40))
    img.paste(hl, (int(kx), int(ky)), hl)

    # K 字母
    draw = ImageDraw.Draw(img)
    font = None
    for candidate in (
        r"C:\Windows\Fonts\segoeuib.ttf",
        r"C:\Windows\Fonts\arialbd.ttf",
        r"C:\Windows\Fonts\msyhbd.ttc",
    ):
        if os.path.exists(candidate):
            try:
                font = ImageFont.truetype(candidate, 44)
                break
            except Exception:
                continue

    if font is None:
        font = ImageFont.load_default()

    text = "K"
    bbox = draw.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    tx = kx + (key_w - tw) / 2 - bbox[0]
    ty = ky + (key_h - th) / 2 - bbox[1] - 2
    draw.text((tx + 1, ty + 2), text, font=font, fill=(120, 80, 10, 150))
    draw.text((tx, ty), text, font=font, fill=(40, 24, 8, 255))

    # 底部点缀：三颗小光点
    for i, alpha in enumerate((220, 160, 100)):
        x = size / 2 - 18 + i * 18
        draw.ellipse((x - 3, size - 26, x + 3, size - 20), fill=(255, 214, 96, alpha))

    # 外描边
    outline = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(outline).rounded_rectangle((1, 1, size - 2, size - 2), radius=25, outline=(255, 200, 90, 60), width=2)
    img = Image.alpha_composite(img, outline)

    img.save(OUT)
    img.save(OUT_PLUGIN)
    print(f"saved {OUT} and {OUT_PLUGIN}")


if __name__ == "__main__":
    main()
