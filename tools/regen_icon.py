"""重新生成 Fangcun 应用图标 (Assets\\fangcun.ico)。

设计语言与托盘图标保持一致: 灰圆角方 + 白色"寸"字。
原图标只有 16x16 一帧, Windows 任务栏/Alt-Tab/资源管理器缩放后糊。
这里输出多尺寸 ico (16/20/24/32/40/48/64/128/256), 小尺寸单独 hand-tune 字
号与内边距以保证锐利; 32 以上直接 LANCZOS 缩小, 大尺寸可清晰显示"寸"。

用法:
    python tools/regen_icon.py            # 写回 Assets\\fangcun.ico
    python tools/regen_icon.py --preview  # 仅写各尺寸预览 PNG 到 tools/_icon_preview/
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


# 设计参数
BG_COLOR = (0x66, 0x66, 0x66, 255)   # 灰圆角主体
FG_COLOR = (255, 255, 255, 255)      # 白色"寸"
GLYPH = "寸"

# 每尺寸的 hand-tune: (内边距比例, 圆角比例, 字号比例)
# 内边距/圆角 = 像素比例, 字号 = 渲染 256 时的相对倍率
# 越小的尺寸字号略小、圆角略大(避免大圆角吃光空间), 边距相对略小(让字更易读)
PER_SIZE_TUNE = {
    16:  dict(pad=0.04, radius=0.28, font=0.50),
    20:  dict(pad=0.06, radius=0.26, font=0.52),
    24:  dict(pad=0.08, radius=0.24, font=0.54),
    32:  dict(pad=0.10, radius=0.22, font=0.56),
    40:  dict(pad=0.10, radius=0.22, font=0.58),
    48:  dict(pad=0.10, radius=0.22, font=0.60),
    64:  dict(pad=0.10, radius=0.22, font=0.60),
    128: dict(pad=0.10, radius=0.22, font=0.60),
    256: dict(pad=0.10, radius=0.22, font=0.60),
}


def find_cn_font() -> str:
    """找一个有"寸"字形的 Windows 字体; 优先粗黑体。"""
    candidates = [
        r"C:\Windows\Fonts\msyhbd.ttc",     # 微软雅黑 Bold
        r"C:\Windows\Fonts\simhei.ttf",     # 黑体
        r"C:\Windows\Fonts\msyh.ttc",       # 微软雅黑
        r"C:\Windows\Fonts\simsun.ttc",     # 宋体
        "/System/Library/Fonts/PingFang.ttc",  # mac
        "/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc",  # linux
    ]
    for c in candidates:
        if os.path.isfile(c):
            return c
    raise FileNotFoundError("找不到可用中文字体")


def render(size: int, font_path: str) -> Image.Image:
    """渲染单帧 RGBA 图标。"""
    t = PER_SIZE_TUNE.get(size, PER_SIZE_TUNE[256])
    im = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)

    pad = max(1, int(round(size * t["pad"])))
    radius = max(2, int(round(size * t["radius"])))
    rect = (pad, pad, size - 1 - pad, size - 1 - pad)
    d.rounded_rectangle(rect, radius=radius, fill=BG_COLOR)

    # 字号 (PIL font size 大致等于 字符 em)
    font_size = max(6, int(round(size * t["font"])))
    font = ImageFont.truetype(font_path, font_size)

    # 居中绘制"寸"
    # PIL 的 textbbox 给出真实像素 bbox, 通过 offset 调整使字水平/垂直居中
    l, t_top, r, b = d.textbbox((0, 0), GLYPH, font=font)
    tw, th = r - l, b - t_top
    x = (size - tw) / 2 - l
    y = (size - th) / 2 - t_top
    d.text((x, y), GLYPH, fill=FG_COLOR, font=font)
    return im


def build_frames(sizes, font_path):
    frames = []
    for s in sizes:
        frames.append(render(s, font_path))
    return frames


def save_ico(frames, sizes, out_path: Path):
    """手动打包多尺寸 ico, 每帧用 PNG 编码(Vista+ 支持)。

    PIL 的 ICO writer 用 sizes 列表时只会保存第一张并自动 resize,
    无法表达"每帧单独 hand-tune"的需求; 因此直接写目录+PNG 负载。
    """
    import io
    out_path.parent.mkdir(parents=True, exist_ok=True)

    assert len(frames) == len(sizes)
    # 每帧先编成 PNG 字节
    png_blobs = []
    for f, s in zip(frames, sizes):
        assert f.size == (s, s)
        buf = io.BytesIO()
        f.save(buf, format="PNG")
        png_blobs.append((s, buf.getvalue()))

    # ICO 头: ICONDIR(6) + ICONDIRENTRY*count(16 each)
    header_size = 6 + 16 * len(png_blobs)
    # 各 entry 偏移
    offsets = []
    cur = header_size
    for _, blob in png_blobs:
        offsets.append(cur)
        cur += len(blob)

    import struct
    out = bytearray()
    # ICONDIR
    out += struct.pack("<HHH", 0, 1, len(png_blobs))
    # ICONDIRENTRY
    for (s, blob), off in zip(png_blobs, offsets):
        # width/height: 0 表示 256
        w = 0 if s >= 256 else s
        h = 0 if s >= 256 else s
        out += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(blob), off)
    # PNG payloads
    for _, blob in png_blobs:
        out += blob

    with open(out_path, "wb") as fp:
        fp.write(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", action="store_true", help="仅导出各尺寸预览 PNG")
    ap.add_argument("--sizes", default="16,20,24,32,40,48,64,128,256",
                    help="要打包的尺寸, 逗号分隔")
    ap.add_argument("--out", default=None, help="输出 ico 路径, 默认 Assets\\fangcun.ico")
    args = ap.parse_args()

    repo = Path(__file__).resolve().parent.parent
    default_out = repo / "Fangcun" / "Assets" / "fangcun.ico"
    out_path = Path(args.out) if args.out else default_out

    sizes = [int(x) for x in args.sizes.split(",")]
    sizes = sorted(set(s for s in sizes if s > 0))
    if not sizes:
        sys.exit("无效 sizes")

    font_path = find_cn_font()
    frames = build_frames(sizes, font_path)

    if args.preview:
        preview_dir = repo / "tools" / "_icon_preview"
        preview_dir.mkdir(parents=True, exist_ok=True)
        for s, f in zip(sizes, frames):
            f.save(preview_dir / f"fangcun_{s}.png")
        # 一张总览: 每尺寸按 4x 放大拼成一行
        scale = 4
        thumbs = [f.resize((s * scale, s * scale), Image.NEAREST) for s, f in zip(sizes, frames)]
        total_w = sum(t.width for t in thumbs) + (len(thumbs) - 1) * 6
        max_h = max(t.height for t in thumbs)
        sheet = Image.new("RGBA", (total_w, max_h), (255, 255, 255, 255))
        x = 0
        for t in thumbs:
            sheet.paste(t, (x, 0), t)
            x += t.width + 6
        sheet.save(preview_dir / "_sheet.png")
        print(f"预览已写入 {preview_dir}")
        return

    save_ico(frames, sizes, out_path)
    print(f"已写入 {out_path} ({out_path.stat().st_size} bytes), sizes={sizes}")


if __name__ == "__main__":
    main()
