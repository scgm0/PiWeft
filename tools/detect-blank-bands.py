#!/usr/bin/env python3
"""逐帧检测会话正文里的成段空白带。

用法: detect-blank-bands.py <frames-dir>

对每张截图：取会话正文列（窗口中部横条），按行计算像素标准差；
连续 ≥260 行（约 260 物理像素）的「近乎纯背景」行带即为可疑空白。
两屏对照的真实截图里，空白带横跨约 1/3 视口且持续存在，正常内容
（文字/表格/代码）不会出现这种带；消息间距 <40px，也不会误报。
"""
import sys
from pathlib import Path

try:
    from PIL import Image
except ImportError:
    print("PIL unavailable; pip install pillow", file=sys.stderr)
    sys.exit(2)

MIN_BAND = 260
ROW_STD_THRESHOLD = 3.0


def analyze(path: Path) -> list[tuple[int, int]]:
    image = Image.open(path).convert("L")
    width, height = image.size
    # 会话正文列：窗口宽度的 27%–92%（侧栏/滚动条/右缘留白之外）。
    left = int(width * 0.27)
    right = int(width * 0.92)
    crop = image.crop((left, 0, right, height))
    pixels = crop.load()
    w, h = crop.size
    # 行标准差（隔行采样加速）。
    stds = []
    for y in range(h):
        total = 0
        total_sq = 0
        count = 0
        for x in range(0, w, 6):
            value = pixels[x, y]
            total += value
            total_sq += value * value
            count += 1
        mean = total / max(1, count)
        variance = total_sq / max(1, count) - mean * mean
        stds.append(variance ** 0.5)

    bands = []
    start = None
    for y, std in enumerate(stds + [255.0]):  # sentinel closes a trailing band
        if std < ROW_STD_THRESHOLD:
            if start is None:
                start = y
        else:
            if start is not None and y - start >= MIN_BAND:
                bands.append((start, y))
            start = None
    return bands


def main() -> None:
    frames = Path(sys.argv[1])
    any_band = False
    for png in sorted(frames.glob("*.png")):
        bands = analyze(png)
        if bands:
            any_band = True
            spans = ", ".join(f"{a}-{b} ({b - a}px)" for a, b in bands)
            print(f"BLANK {png.name}: {spans}")
        else:
            print(f"clean {png.name}")
    if not any_band:
        print("no blank bands detected in any frame")
    sys.exit(1 if any_band else 0)


if __name__ == "__main__":
    main()
