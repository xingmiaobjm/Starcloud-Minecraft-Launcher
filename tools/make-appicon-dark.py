# 从 Assets/AppIcon.ico 派生深色任务栏版 Assets/AppIcon-Dark.ico。
#
# 口径与品牌 SVG 一致（starcloud-mark-dark.svg）："只把线换成浅色，其余保持原样"。
# 深色墨线 (24,19,16) → 浅色 (230,234,242)，青色面、白色填充与透明底全部不动，
# 与墨线的抗锯齿过渡按「墨线 × 底色」线性混合反解后重合成，边缘不会出现脏色。
#
# 用法：python tools/make-appicon-dark.py
# 依赖：仅 Python 标准库。ico 里的图片全部是 BMP 格式（含 256px），PNG 分支仅作兜底。

import struct
import sys
import zlib
from pathlib import Path

SRC = Path(__file__).resolve().parent.parent / "dotnet/src/Starcloud.App/Assets/AppIcon.ico"
DST = Path(__file__).resolve().parent.parent / "dotnet/src/Starcloud.App/Assets/AppIcon-Dark.ico"

INK = (16, 19, 24)        # 深色墨线（#101318）
INK_DARK_BG = (230, 234, 242)  # 深底版浅色墨线（#E6EAF2）
BASES = [                 # 墨线可能压在哪些底色上（抗锯齿混合的另一半）
    (255, 211, 34),       # 黄色面（#FFD322）
    (230, 234, 242),      # 白色填充（#E6EAF2）
    (255, 255, 255),      # 边缘高光
]


def parse_ico(data):
    count = struct.unpack("<H", data[4:6])[0]
    entries = []
    for i in range(count):
        e = data[6 + i * 16:22 + i * 16]
        w, h, _, _, _, bpp, size, off = struct.unpack("<BBBBHHII", e)
        entries.append({"w": w or 256, "h": h or 256, "bpp": bpp, "data": data[off:off + size]})
    return entries


def decode_png(raw):
    """兜底：ico 里 256px 图常被编码成 PNG（RGBA）。"""
    assert raw[:8] == b"\x89PNG\r\n\x1a\n"
    pos, idat = 8, b""
    w = h = ctype = None
    while pos < len(raw):
        ln, typ = struct.unpack(">I4s", raw[pos:pos + 8])
        chunk = raw[pos + 8:pos + 8 + ln]
        pos += 12 + ln
        if typ == b"IHDR":
            w, h, _, ctype = struct.unpack(">IIBB", chunk[:10])
        elif typ == b"IDAT":
            idat += chunk
        elif typ == b"IEND":
            break
    packed = zlib.decompress(idat)
    channels = 4 if ctype == 6 else 3
    stride = w * channels
    out = bytearray(w * h * 4)
    prev = bytearray(stride)
    pos = 0
    for y in range(h):
        filt = packed[pos]
        pos += 1
        line = bytearray(packed[pos:pos + stride])
        pos += stride
        if filt == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 255
        elif filt == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif filt == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 255
        elif filt == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = prev[i]
                c = prev[i - channels] if i >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 255
        prev = line
        for x in range(w):
            o = (y * w + x) * 4
            if channels == 4:
                b, g, r, a = line[x * 4:x * 4 + 4]
            else:
                b, g, r = line[x * 4:x * 4 + 3]
                a = 255
            out[o:o + 4] = bytes((r, g, b, a))
    return bytes(out)  # RGBA，自顶向下


def decode_bmp(raw, w, h):
    """ico 的 BMP 帧：40 字节头 + 自底向上 BGRA + AND 掩码。返回 RGBA 自顶向下。"""
    stride = w * 4
    pixels = raw[40:40 + stride * h]
    out = bytearray(w * h * 4)
    for y in range(h):
        src = (h - 1 - y) * stride
        for x in range(w):
            b, g, r, a = pixels[src + x * 4:src + x * 4 + 4]
            o = (y * w + x) * 4
            out[o:o + 4] = bytes((r, g, b, a))
    return bytes(out)


def recolor(px, w, h):
    """把墨线换成浅色；黄面附近（星与黄面描边）保持深色——与品牌 SVG 深底版口径一致。"""
    out = bytearray(px)
    # 预扫：黄色面膨胀两像素，圈出「星 + 黄面描边」保护区
    yellow = (255, 211, 34)
    protect = bytearray(w * h)
    for i in range(0, len(out), 4):
        r, g, b, a = out[i:i + 4]
        if a > 200 and abs(r - yellow[0]) < 28 and abs(g - yellow[1]) < 28 and abs(b - yellow[2]) < 28:
            x, y = (i // 4) % w, (i // 4) // w
            for dy in range(-2, 3):
                for dx in range(-2, 3):
                    nx, ny = x + dx, y + dy
                    if 0 <= nx < w and 0 <= ny < h:
                        protect[ny * w + nx] = 1
    for i in range(0, len(out), 4):
        r, g, b, a = out[i:i + 4]
        if a == 0:
            continue
        if protect[i // 4]:
            continue
        pixel = (r, g, b)
        # 与墨线一致（含透明底上的墨线：RGB 就是墨线色，靠 alpha 表现边缘）
        best = None
        for base in BASES:
            lo, hi = INK, base
            span = [(hi[c] - lo[c]) for c in range(3)]
            denom = sum(v * v for v in span)
            t = 0.0
            if denom:
                t = sum((pixel[c] - lo[c]) * span[c] for c in range(3)) / denom
            t = max(0.0, min(1.0, t))
            err = sum((pixel[c] - (lo[c] + (hi[c] - lo[c]) * t)) ** 2 for c in range(3))
            if best is None or err < best[0]:
                best = (err, t, base)
        err, t, base = best
        # t 是底色权重：pixel = t·墨线 + (1-t)·底色。纯底色（dist≈0）不动，
        # 其余（含纯墨线 t=0、墨线与底色的过渡）都按同比例把墨线换成浅色。
        dist = sum((pixel[c] - base[c]) ** 2 for c in range(3))
        if dist < 64:
            continue
        mixed = tuple(round(INK_DARK_BG[c] * (1 - t) + base[c] * t) for c in range(3))
        out[i:i + 3] = bytes(mixed)
    return bytes(out)


def encode_bmp(rgba, w, h):
    """RGBA（自顶向下）→ ico 的 BMP 帧（BGRA 自底向上 + 全零 AND 掩码）。"""
    stride = w * 4
    body = bytearray(40)
    struct.pack_into("<IiiHHIIiiII", body, 0,
                     40, w, h * 2, 1, 32, 0, w * h * 4, 0, 0, 0, 0)
    rows = bytearray(stride * h)
    for y in range(h):
        src = y * stride
        dst = (h - 1 - y) * stride
        for x in range(w):
            r, g, b, a = rgba[src + x * 4:src + x * 4 + 4]
            rows[dst + x * 4:dst + x * 4 + 4] = bytes((b, g, r, a))
    mask_stride = ((w + 31) // 32) * 4
    return bytes(body) + bytes(rows) + bytes(mask_stride * h)


def main():
    data = SRC.read_bytes()
    entries = parse_ico(data)
    frames = []
    for e in entries:
        w, h, raw = e["w"], e["h"], e["data"]
        is_png = raw[:8] == b"\x89PNG\r\n\x1a\n"
        rgba = decode_png(raw) if is_png else decode_bmp(raw, w, h)
        frames.append((w, h, encode_bmp(recolor(rgba, w, h), w, h)))

    out = bytearray(struct.pack("<HHH", 0, 1, len(frames)))
    offset = 6 + 16 * len(frames)
    blobs = []
    for w, h, blob in frames:
        out += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
        blobs.append(blob)
        offset += len(blob)
    for blob in blobs:
        out += blob
    DST.write_bytes(bytes(out))
    print(f"已生成 {DST}（{DST.stat().st_size} 字节，{len(frames)} 个尺寸）")


if __name__ == "__main__":
    sys.exit(main())
