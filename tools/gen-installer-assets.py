# 生成安装器品牌图（welcome / header / splash），输出到 tools/installer-assets/
# 用法：python tools/gen-installer-assets.py
#
# NSIS 的 MUI 位图要求 24bpp BMP；Pillow 默认保存即 24 位 RGB。

from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
ICON = ROOT / "docs" / "assets" / "brand" / "starcloud-icon-dark-1024.png"
OUT = ROOT / "tools" / "installer-assets"
VERSION = "V0.3.4"

BG_TOP = (22, 22, 22)
BG_BOTTOM = (12, 12, 12)
ACCENT = (255, 211, 34)      # 品牌黄
TEXT = (245, 245, 245)
DIM = (160, 160, 160)
FAINT = (110, 110, 110)

FONT_BOLD = "C:/Windows/Fonts/msyhbd.ttc"
FONT_REG = "C:/Windows/Fonts/msyh.ttc"


def gradient(size, top=BG_TOP, bottom=BG_BOTTOM):
    w, h = size
    img = Image.new("RGB", size, top)
    d = ImageDraw.Draw(img)
    for y in range(h):
        t = y / max(1, h - 1)
        color = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        d.line([(0, y), (w, y)], fill=color)
    return img


def font(size, bold=False):
    try:
        return ImageFont.truetype(FONT_BOLD if bold else FONT_REG, size)
    except OSError:
        return ImageFont.load_default()


def paste_icon(img, box_size, center):
    icon = Image.open(ICON).convert("RGBA")
    icon.thumbnail((box_size, box_size), Image.LANCZOS)
    x = center[0] - icon.width // 2
    y = center[1] - icon.height // 2
    img.paste(icon, (x, y), icon)


def centered(draw, xy, text, fnt, fill):
    box = draw.textbbox((0, 0), text, font=fnt)
    draw.text((xy[0] - (box[2] - box[0]) / 2, xy[1]), text, font=fnt, fill=fill)


def welcome():
    w, h = 164, 314
    img = gradient((w, h))
    d = ImageDraw.Draw(img)
    # 顶部一条品牌黄细带
    d.rectangle([0, 0, w, 3], fill=ACCENT)
    paste_icon(img, 104, (w // 2, 78))
    centered(d, (w // 2, 148), "Starcloud", font(21, True), TEXT)
    centered(d, (w // 2, 178), "Minecraft Launcher", font(10), DIM)
    d.rectangle([62, 205, 102, 207], fill=ACCENT)
    centered(d, (w // 2, 220), "开发版 " + VERSION, font(9), FAINT)
    centered(d, (w // 2, 270), "星云创新", font(9), FAINT)
    img.save(OUT / "welcome.bmp")


def header():
    w, h = 150, 57
    img = Image.new("RGB", (w, h), BG_TOP)
    d = ImageDraw.Draw(img)
    paste_icon(img, 34, (26, h // 2))
    d.text((50, 10), "Starcloud", font=font(13, True), fill=TEXT)
    d.text((50, 31), "Minecraft Launcher", font=font(8), fill=DIM)
    img.save(OUT / "header.bmp")


def splash():
    w, h = 480, 300
    img = gradient((w, h))
    d = ImageDraw.Draw(img)
    # 圆角描边
    d.rounded_rectangle([0, 0, w - 1, h - 1], radius=18, outline=(48, 48, 48), width=1)
    d.rectangle([0, 0, w, 4], fill=ACCENT)
    paste_icon(img, 88, (w // 2, 86))
    centered(d, (w // 2, 150), "Starcloud Minecraft Launcher", font(18, True), TEXT)
    centered(d, (w // 2, 182), "正在安装，请稍候…", font(11), (200, 200, 200))
    # 进度条底 + 品牌黄
    d.rounded_rectangle([140, 222, 340, 228], radius=3, fill=(40, 40, 40))
    d.rounded_rectangle([140, 222, 300, 228], radius=3, fill=ACCENT)
    centered(d, (w // 2, 248), "开发版 " + VERSION, font(9), FAINT)
    img.save(OUT / "splash.bmp")


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    welcome()
    header()
    splash()
    print("installer assets ->", OUT)
