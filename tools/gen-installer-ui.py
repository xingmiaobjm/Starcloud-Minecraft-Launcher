"""生成自定义安装器界面位图（抖音式单窗口，无边框）。

坐标一律按 960x720 的逻辑画布写，按 100% / 150% / 200% 三档 DPI 输出，
NSIS 侧按系统 DPI 选对应文件，所以文字和圆角在任何缩放下都是清晰的。

产物（tools/installer-assets/ui/）：
  bg-install-<pct>.bmp   安装前：标题栏 + 大图 + 路径条 + 一键安装 + 协议行
  bg-progress-<pct>.bmp  安装中：标题 + 进度条轨道
  bg-done-<pct>.bmp      完成：对勾 + 完成按钮
  chk-off/on-<pct>.bmp   自绘复选框（18x18 逻辑）
  layout.nsh             供 installer.nsi 使用的逻辑坐标
"""

from __future__ import annotations

import math
import os
from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "tools", "installer-assets", "ui")
MARK_LIGHT = os.path.join(ROOT, "docs", "assets", "brand", "starcloud-mark-light-512.png")
ICON_DARK = os.path.join(ROOT, "docs", "assets", "brand", "starcloud-icon-dark-256.png")
FONT_BD = r"C:\Windows\Fonts\msyhbd.ttc"
FONT_RG = r"C:\Windows\Fonts\msyh.ttc"
FONT_EN = r"C:\Windows\Fonts\segoeuib.ttf"

INK = (16, 19, 24)
SUB = (107, 114, 128)
MUTED = (156, 163, 175)
ACCENT = (255, 211, 34)
PILL = (245, 246, 248)
BORDER = (230, 232, 236)
WHITE = (255, 255, 255)
OK_GREEN = (34, 197, 94)

SCALES = [(100, 1.0), (150, 1.5), (200, 2.0)]

# ── 逻辑坐标（layout.nsh 从这里生成） ──
WIN_W, WIN_H = 960, 660
TITLE_MIN = (852, 8, 40, 40)
TITLE_CLOSE = (892, 8, 44, 40)
PATH_EDIT = (176, 424, 700, 24)
PATH_BTN = (864, 414, 48, 44)
BTN_INSTALL = (40, 492, 880, 64)
CHK_AGREE = (40, 584, 18, 18)
CHK_CUSTOM = (716, 584, 18, 18)
BTN_DONE = (40, 492, 880, 64)
CHK_RUN = (88, 320, 18, 18)
CHK_UNINSTALL_DATA = (88, 316, 18, 18)
PROGRESS = (88, 316, 784, 12)


def font(path: str, size: float) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(path, max(1, int(round(size))), index=0)


class Canvas:
    def __init__(self, scale: float):
        self.s = scale
        self.img = Image.new("RGB", (self.px(WIN_W), self.px(WIN_H)), WHITE)
        self.d = ImageDraw.Draw(self.img, "RGB")

    def px(self, v: float) -> int:
        return int(round(v * self.s))

    def box(self, xywh):
        x, y, w, h = xywh
        return [self.px(x), self.px(y), self.px(x + w) - 1, self.px(y + h) - 1]

    def radius(self, r: float) -> int:
        return max(1, self.px(r))

    # ── 基础图元 ──
    def text(self, xy, s, size, fill, bold=True, anchor="la"):
        f = font(FONT_BD if bold else FONT_RG, size * self.s)
        self.d.text((self.px(xy[0]), self.px(xy[1])), s, font=f, fill=fill, anchor=anchor)

    def latin(self, xy, s, size, fill, anchor="la"):
        f = font(FONT_EN, size * self.s)
        self.d.text((self.px(xy[0]), self.px(xy[1])), s, font=f, fill=fill, anchor=anchor)

    def measure(self, s, size, bold=False):
        """文字的逻辑宽度（用于给链接文字配点击热区）"""
        f = font(FONT_BD if bold else FONT_RG, size * self.s)
        return f.getlength(s) / self.s

    def line(self, pts, fill, width):
        self.d.line([(self.px(x), self.px(y)) for x, y in pts],
                    fill=fill, width=max(1, self.px(width)), joint="curve")


def hero_gradient(size_px):
    """暖黄 → 米白 → 淡蓝的斜向渐变，再叠两团柔光。"""
    w, h = size_px
    # 小图算渐变再放大：斜向渐变逐像素算太慢，1/8 分辨率放大后肉眼看不出差别
    sw, sh = max(8, w // 8), max(6, h // 8)
    small = Image.new("RGB", (sw, sh), WHITE)
    px = small.load()
    for y in range(sh):
        for x in range(sw):
            t = (x / max(1, sw - 1)) * 0.65 + (y / max(1, sh - 1)) * 0.35
            if t < 0.5:
                k = t / 0.5
                c = tuple(int(a + (b - a) * k) for a, b in zip((255, 236, 176), (255, 250, 235)))
            else:
                k = (t - 0.5) / 0.5
                c = tuple(int(a + (b - a) * k) for a, b in zip((255, 250, 235), (223, 236, 255)))
            px[x, y] = c
    base = small.resize((w, h), Image.BICUBIC).convert("RGBA")

    overlay = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    od = ImageDraw.Draw(overlay)
    od.ellipse([int(w * 0.50), -int(h * 0.40), int(w * 1.10), int(h * 0.70)],
               fill=(255, 214, 70, 90))
    od.ellipse([int(w * 0.62), int(h * 0.40), int(w * 1.20), int(h * 1.30)],
               fill=(140, 190, 255, 80))
    overlay = overlay.filter(ImageFilter.GaussianBlur(int(max(w, h) * 0.10)))
    return Image.alpha_composite(base, overlay).convert("RGB")


def draw_title(c: Canvas):
    # 图标 + 文字标识
    icon = Image.open(ICON_DARK).convert("RGBA")
    n = c.px(32)
    icon = icon.resize((n, n), Image.LANCZOS)
    c.img.paste(icon, (c.px(40), c.px(22)), icon)
    c.latin((84, 21), "Starcloud", 17, INK)
    c.text((84, 42), "星云创新 · Minecraft 启动器", 10.5, SUB, bold=False)

    # 最小化
    x, y, w, h = TITLE_MIN
    cy = y + h / 2
    c.line([(x + 12, cy), (x + 28, cy)], (120, 124, 132), 1.6)

    # 关闭
    x, y, w, h = TITLE_CLOSE
    cx, cy = x + w / 2, y + h / 2
    c.line([(cx - 7, cy - 7), (cx + 7, cy + 7)], (120, 124, 132), 1.6)
    c.line([(cx + 7, cy - 7), (cx - 7, cy + 7)], (120, 124, 132), 1.6)


def draw_hero(c: Canvas):
    x0, y0, x1, y1 = 40, 76, 920, 376
    w, h = c.px(x1 - x0), c.px(y1 - y0)
    hero = hero_gradient((w, h)).convert("RGBA")
    mask = Image.new("L", (w, h), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w - 1, h - 1], radius=c.radius(20), fill=255)
    c.img.paste(hero, (c.px(x0), c.px(y0)), mask)

    # 标志：浅色版（深描边）在浅底上最清楚
    mark = Image.open(MARK_LIGHT).convert("RGBA")
    n = c.px(196)
    mark = mark.resize((n, n), Image.LANCZOS)
    # 柔和的投影
    shadow = Image.new("RGBA", (n + c.px(40), n + c.px(40)), (0, 0, 0, 0))
    sh = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    sh.paste(mark, (0, 0), mark)
    sh = sh.filter(ImageFilter.GaussianBlur(c.px(10)))
    shadow.paste((0, 0, 0, 40), (c.px(20), c.px(24)), sh)
    mx, my = c.px(658), c.px(128)
    c.img.paste(shadow, (mx - c.px(20), my - c.px(24)), shadow)
    c.img.paste(mark, (mx, my), mark)

    c.text((90, 150), "开箱即用", 52, INK)
    c.text((92, 232), "星云创新 · 为 Minecraft 打造的开箱即用启动器", 14.5, (75, 85, 99), bold=False)

    # 版本胶囊
    chip = [92, 272, 92 + 128, 272 + 26]
    c.d.rounded_rectangle([c.px(chip[0]), c.px(chip[1]), c.px(chip[2]), c.px(chip[3])],
                          radius=c.radius(13), fill=(255, 255, 255))
    c.text((104, 277), "V0.3.4  开发版", 10.5, (90, 95, 105), bold=False)


def draw_pill(c: Canvas):
    x, y, w, h = 40, 404, 880, 64
    c.d.rounded_rectangle(c.box((x, y, w, h)), radius=c.radius(12), fill=PILL, outline=BORDER,
                          width=max(1, c.px(1)))
    c.text((68, 428), "安装位置", 13.5, SUB, bold=False)
    # 文件夹图标
    fx, fy = 880, 420
    c.d.rounded_rectangle([c.px(fx + 2), c.px(fy + 6), c.px(fx + 24), c.px(fy + 26)],
                          radius=c.radius(4), outline=(120, 124, 132), width=max(1, c.px(1.4)))
    c.d.rounded_rectangle([c.px(fx + 2), c.px(fy + 2), c.px(fx + 11), c.px(fy + 8)],
                          radius=c.radius(2), fill=WHITE, outline=(120, 124, 132),
                          width=max(1, c.px(1.4)))


def draw_primary(c: Canvas, label: str):
    x, y, w, h = BTN_INSTALL
    c.d.rounded_rectangle(c.box((x, y, w, h)), radius=c.radius(12), fill=ACCENT)
    c.text((x + w / 2, y + h / 2 - 1), label, 18, INK, anchor="mm")


LINK_RECTS = {}


def draw_footer(c: Canvas):
    # 复选框本身由 NSIS 自绘覆盖，这里只写文字
    c.text((66, 581), "同意", 12.5, SUB, bold=False)
    terms = "《Starcloud 用户服务协议》"
    privacy = "《隐私政策》"
    c.text((100, 581), terms, 12.5, (32, 107, 255), bold=False)
    c.text((288, 581), "和", 12.5, SUB, bold=False)
    c.text((304, 581), privacy, 12.5, (32, 107, 255), bold=False)
    # 链接的点击热区：文字矩形外扩 4px，点击后打开官网
    LINK_RECTS["terms"] = (96, 577, c.measure(terms, 12.5) + 8, 20)
    LINK_RECTS["privacy"] = (300, 577, c.measure(privacy, 12.5) + 8, 20)
    c.text((742, 581), "自定义安装", 12.5, SUB, bold=False)
    c.text((920, 620), "V0.3.4 · 开发版", 10.5, MUTED, bold=False, anchor="ra")


def build_install(c: Canvas):
    draw_title(c)
    draw_hero(c)
    draw_pill(c)
    draw_primary(c, "一键安装")
    draw_footer(c)


def build_progress(c: Canvas):
    draw_title(c)
    c.text((88, 196), "正在安装 Starcloud", 30, INK)
    c.text((88, 250), "正在部署运行环境与资源文件，请保持窗口开启。", 13.5, SUB, bold=False)
    x, y, w, h = PROGRESS
    c.d.rounded_rectangle(c.box((x, y, w, h)), radius=c.radius(6), fill=(240, 241, 244))
    c.text((88, 348), "这通常需要十几秒，安装完成后会自动进入下一步。", 11.5, MUTED, bold=False)
    c.text((920, 620), "V0.3.4 · 开发版", 10.5, MUTED, bold=False, anchor="ra")


def build_uninstall(c: Canvas):
    draw_title(c)
    c.text((88, 196), "卸载 Starcloud", 30, INK)
    c.text((88, 250), "程序本体将被删除，你的账号与配置可以选择保留。", 13.5, SUB, bold=False)
    c.text((114, 314), "同时删除配置、账号与崩溃日志", 13.5, INK, bold=False)
    draw_primary(c, "卸载")
    c.text((920, 620), "V0.3.4 · 开发版", 10.5, MUTED, bold=False, anchor="ra")


def build_uninstalling(c: Canvas):
    draw_title(c)
    c.text((88, 196), "正在卸载 Starcloud", 30, INK)
    c.text((88, 250), "正在移除程序文件与快捷方式，请稍候。", 13.5, SUB, bold=False)
    c.text((920, 620), "V0.3.4 · 开发版", 10.5, MUTED, bold=False, anchor="ra")


def build_done(c: Canvas):
    draw_title(c)
    # 对勾圆
    cx, cy, r = 120, 240, 34
    c.d.ellipse([c.px(cx - r), c.px(cy - r), c.px(cx + r), c.px(cy + r)], fill=(232, 251, 239))
    c.d.ellipse([c.px(cx - r + 5), c.px(cy - r + 5), c.px(cx + r - 5), c.px(cy + r - 5)],
                fill=OK_GREEN)
    c.line([(cx - 13, cy + 1), (cx - 4, cy + 10), (cx + 14, cy - 9)], WHITE, 4.2)

    c.text((176, 212), "安装完成", 30, INK)
    c.text((176, 262), "Starcloud 已经就绪，随时可以开始游戏。", 13.5, SUB, bold=False)
    c.text((114, 316), "立即运行 Starcloud", 13.5, INK, bold=False)
    draw_primary(c, "完成")
    c.text((920, 620), "V0.3.4 · 开发版", 10.5, MUTED, bold=False, anchor="ra")


def build_check(c: Canvas, on: bool):
    n = c.px(18)
    img = Image.new("RGB", (n, n), WHITE)
    d = ImageDraw.Draw(img)
    if on:
        d.rounded_rectangle([0, 0, n - 1, n - 1], radius=max(1, c.px(5)), fill=ACCENT)
        d.line([(c.px(4), c.px(9)), (c.px(7.5), c.px(13)), (c.px(14), c.px(5))],
               fill=(16, 19, 24), width=max(2, c.px(2)))
    else:
        d.rounded_rectangle([0, 0, n - 1, n - 1], radius=max(1, c.px(5)), fill=WHITE,
                            outline=(203, 207, 214), width=max(1, c.px(1)))
    return img


def save_hits(c: Canvas, folder: str, boxes):
    """把背景上对应的区域裁出来，供 NSIS 的点击热区当作自己的位图显示：
    这样热区既能接鼠标，又和底图完全一致（STATIC 用背景色涂抹会盖出灰块）。"""
    for suffix, (x, y, w, h) in boxes.items():
        c.img.crop((c.px(x), c.px(y), c.px(x + w), c.px(y + h))).save(
            os.path.join(folder, f"hit-{suffix}.bmp"))


def main():
    os.makedirs(OUT, exist_ok=True)
    for pct, scale in SCALES:
        d = os.path.join(OUT, str(pct))
        os.makedirs(d, exist_ok=True)

        c = Canvas(scale)
        build_install(c)
        c.img.save(os.path.join(d, "bg-install.bmp"))
        save_hits(c, d, {
            "title": (0, 0, 840, 64), "min": TITLE_MIN, "close": TITLE_CLOSE,
            "folder": PATH_BTN, "btn-install": BTN_INSTALL,
        })
        for link, rect in LINK_RECTS.items():
            c.img.crop((c.px(rect[0]), c.px(rect[1]),
                        c.px(rect[0] + rect[2]), c.px(rect[1] + rect[3]))).save(
                os.path.join(d, f"hit-link-{link}.bmp"))

        c = Canvas(scale)
        build_progress(c)
        c.img.save(os.path.join(d, "bg-progress.bmp"))

        c = Canvas(scale)
        build_done(c)
        c.img.save(os.path.join(d, "bg-done.bmp"))
        save_hits(c, d, {"btn-done": BTN_DONE})

        c = Canvas(scale)
        build_uninstall(c)
        c.img.save(os.path.join(d, "bg-uninstall.bmp"))
        save_hits(c, d, {"btn-uninstall": BTN_INSTALL})

        c = Canvas(scale)
        build_uninstalling(c)
        c.img.save(os.path.join(d, "bg-uninstalling.bmp"))

        c = Canvas(scale)
        build_check(c, False).save(os.path.join(d, "chk-off.bmp"))
        build_check(c, True).save(os.path.join(d, "chk-on.bmp"))

        print(f"  {pct}%  {int(round(WIN_W*scale))}x{int(round(WIN_H*scale))}  -> ui/{pct}")

    lines = ["; 由 tools/gen-installer-ui.py 生成，请勿手改",
             "!define UI_W %d" % WIN_W, "!define UI_H %d" % WIN_H]
    for name, (x, y, w, h) in [
        ("MIN", TITLE_MIN), ("CLOSE", TITLE_CLOSE), ("PATH_EDIT", PATH_EDIT),
        ("PATH_BTN", PATH_BTN), ("BTN", BTN_INSTALL), ("CHK_AGREE", CHK_AGREE),
        ("CHK_CUSTOM", CHK_CUSTOM), ("CHK_RUN", CHK_RUN), ("PROGRESS", PROGRESS),
        ("CHK_DATA", CHK_UNINSTALL_DATA), ("DRAG", (0, 0, 840, 64)),
        ("LINK_TERMS", LINK_RECTS["terms"]), ("LINK_PRIVACY", LINK_RECTS["privacy"]),
    ]:
        lines.append("!define UI_%s_X %d" % (name, x))
        lines.append("!define UI_%s_Y %d" % (name, y))
        lines.append("!define UI_%s_W %d" % (name, w))
        lines.append("!define UI_%s_H %d" % (name, h))
    with open(os.path.join(OUT, "layout.nsh"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print("layout.nsh written")


if __name__ == "__main__":
    main()
