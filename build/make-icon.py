"""生成译印 INKSEAL 的应用图标（Assets/app.ico）。

图样直接来自设计稿 inkseal-ui.html 的 .inkwell / .seal-mark：
  64 格坐标系里一枚朱砂圆角印面（rect 4,4,56,56 rx14），
  里面刻掉两支相对的箭头与中心方菱形（负形），刻掉的部分是纸色。
所以这份图标和界面里那颗品牌记号是同一个几何，不是另画一张。

每个尺寸都独立超采样渲染再缩，避免「一次 256→16 缩放」把笔画糊掉。
用法：python build/make-icon.py
"""
import os
import struct
import io
from PIL import Image, ImageDraw

SS = 8                      # 超采样倍数
VERMILION = (192, 57, 43)   # --vermilion #C0392B
PAPER = (244, 243, 241)     # --paper     #F4F3F1
SIZES = [16, 20, 24, 28, 32, 40, 48, 56, 64, 128, 256]

BOX = 64.0
TILE = (4.0, 4.0, 60.0, 60.0)
TILE_RADIUS = 14.0
ARROW_LEFT = [(28, 18), (28, 25.5), (20, 32), (28, 38.5), (28, 46), (13.5, 32)]
ARROW_RIGHT = [(36, 18), (36, 25.5), (44, 32), (36, 38.5), (36, 46), (50.5, 32)]
DIAMOND = [(32, 27.4), (36.6, 32), (32, 36.6), (27.4, 32)]


def render(size):
    """渲染单个尺寸：先在 size*SS 的画布上画 64 格坐标，再 LANCZOS 缩回。"""
    canvas = size * SS
    scale = canvas / BOX
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    draw.rounded_rectangle(
        [TILE[0] * scale, TILE[1] * scale, TILE[2] * scale, TILE[3] * scale],
        radius=TILE_RADIUS * scale,
        fill=VERMILION,
    )
    for shape in (ARROW_LEFT, ARROW_RIGHT, DIAMOND):
        draw.polygon([(x * scale, y * scale) for x, y in shape], fill=PAPER)

    return image.resize((size, size), Image.LANCZOS)


def frame_bytes(image):
    """单帧编码：≤128 用经典 BMP（BITMAPINFOHEADER + XOR 位图 + AND 掩码），
    只有 256 用 PNG。理由：256 的 BMP 要 270KB，而 WPF 的 ICO 解码器对
    「全部尺寸都是 PNG 帧」的图标历来不稳，小尺寸保留经典格式最保险。"""
    if image.width > 128:
        buffer = io.BytesIO()
        image.save(buffer, format="PNG", optimize=True)
        return buffer.getvalue()

    width, height = image.size
    pixels = image.load()
    header = struct.pack("<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, 0, 0, 0, 0, 0)

    xor = bytearray()
    for y in range(height - 1, -1, -1):          # XOR 位图自下而上
        for x in range(width):
            r, g, b, a = pixels[x, y]
            xor += bytes((b, g, r, a))           # 非预乘 BGRA

    row = ((width + 31) // 32) * 4               # AND 掩码每行按 4 字节对齐
    mask = bytearray()
    for y in range(height - 1, -1, -1):
        line = bytearray(row)
        for x in range(width):
            if pixels[x, y][3] == 0:             # 1 = 透明
                line[x // 8] |= 0x80 >> (x % 8)
        mask += line

    return header + bytes(xor) + bytes(mask)


def write_ico(path, images):
    """手写 ICO 容器：PIL 的 ICO 保存会把所有尺寸从同一张图缩出来，
    这里每个尺寸都是独立超采样渲染的，所以自己拼容器。"""
    entries, blobs, offset = [], [], 6 + 16 * len(images)
    for image in images:
        blob = frame_bytes(image)
        side = image.width
        entries.append(struct.pack(
            "<BBBBHHII",
            side if side < 256 else 0,
            side if side < 256 else 0,
            0, 0, 1, 32, len(blob), offset))
        blobs.append(blob)
        offset += len(blob)

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as handle:
        handle.write(struct.pack("<HHH", 0, 1, len(images)))
        for entry in entries:
            handle.write(entry)
        for blob in blobs:
            handle.write(blob)


def main():
    target = os.path.join(os.path.dirname(__file__), "..", "src", "TranslationApp.App", "Assets", "app.ico")
    target = os.path.abspath(target)
    write_ico(target, [render(size) for size in SIZES])
    print(f"wrote {target} ({os.path.getsize(target)} bytes, {len(SIZES)} sizes)")


if __name__ == "__main__":
    main()
