#!/usr/bin/env python3
"""Draws HexiumDist/icon.png (256x256) with no dependencies beyond the standard library.

The emblem is the mod in one picture: a gilt DASHED ring - a territory border, drawn the way the
game draws a ward's - around a banner planted on a pole. Supersampled 4x for clean edges, and the
PNG is written by hand (zlib + CRC), the same dependency-free approach as the workspace's
offline texture harness. Deterministic: the same script always writes the same bytes.
"""

import math
import pathlib
import struct
import zlib

SIZE = 256
SS = 4                      # supersampling factor per axis
N = SIZE * SS

BG_INNER = (34, 30, 24)
BG_OUTER = (12, 10, 8)
GOLD = (214, 170, 72)
GOLD_BRIGHT = (255, 232, 170)
GOLD_DEEP = (120, 84, 28)
CLOTH = (150, 38, 32)
CLOTH_DARK = (96, 22, 20)


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def in_ring(x, y, cx, cy, r0, r1, dashes, duty):
    d = math.hypot(x - cx, y - cy)
    if d < r0 or d > r1:
        return False
    ang = (math.atan2(y - cy, x - cx) / (2 * math.pi)) % 1.0
    return (ang * dashes) % 1.0 < duty


def in_poly(x, y, pts):
    inside = False
    j = len(pts) - 1
    for i in range(len(pts)):
        xi, yi = pts[i]
        xj, yj = pts[j]
        if (yi > y) != (yj > y) and x < (xj - xi) * (y - yi) / (yj - yi + 1e-12) + xi:
            inside = not inside
        j = i
    return inside


def shade(x, y):
    """Colour of one supersample, in 0..N space."""
    u, v = x / N, y / N
    cx, cy = 0.5, 0.52

    # background: warm vignette
    d = math.hypot(u - 0.5, v - 0.5) / 0.7071
    col = lerp(BG_INNER, BG_OUTER, min(1.0, d * 1.15))

    # faint filled territory
    if math.hypot(u - cx, v - cy) < 0.40:
        col = lerp(col, GOLD, 0.07)

    # the dashed border ring, with a darker under-stroke for depth
    if in_ring(u, v, cx, cy + 0.006, 0.395, 0.445, 28, 0.62):
        col = lerp(col, GOLD_DEEP, 0.9)
    if in_ring(u, v, cx, cy, 0.395, 0.44, 28, 0.62):
        t = (v - (cy - 0.44)) / 0.88                 # light from the top
        col = lerp(GOLD_BRIGHT, GOLD, min(1.0, max(0.0, t * 1.4)))

    # the pole
    pole_x0, pole_x1 = 0.395, 0.425
    if pole_x0 <= u <= pole_x1 and 0.24 <= v <= 0.80:
        t = (u - pole_x0) / (pole_x1 - pole_x0)
        col = lerp(GOLD_BRIGHT, GOLD_DEEP, t)
    # finial
    if math.hypot(u - 0.41, v - 0.225) < 0.028:
        col = lerp(GOLD_BRIGHT, GOLD, math.hypot(u - 0.40, v - 0.215) / 0.03)
    # foot
    if 0.34 <= u <= 0.48 and 0.79 <= v <= 0.815:
        col = GOLD_DEEP

    # the banner: a swallow-tailed pennant flying off the pole, gently waved
    wave = 0.018 * math.sin((u - 0.425) * 14.0)
    top, bottom = 0.27 + wave, 0.56 + wave
    tip_x = 0.72
    notch_x = 0.64
    mid = (top + bottom) / 2
    pts = [(0.425, top), (tip_x, top + 0.02), (notch_x, mid), (tip_x, bottom - 0.02), (0.425, bottom)]
    if in_poly(u, v, pts):
        fold = 0.5 + 0.5 * math.sin((u - 0.425) * 14.0 + 1.2)
        col = lerp(CLOTH, CLOTH_DARK, fold * 0.8)
        # a gold rune on the cloth: an Algiz-like mark (protection)
        rx, ry = 0.54, mid
        stem = abs(u - rx) < 0.012 and ry - 0.09 <= v <= ry + 0.09
        # arms rise from the stem: left arm has u falling as v falls, right arm u rising as v falls
        arm_l = abs((u - rx) - (v - (ry - 0.02)) * 0.9) < 0.012 and rx - 0.07 <= u <= rx and v <= ry - 0.02
        arm_r = abs((u - rx) + (v - (ry - 0.02)) * 0.9) < 0.012 and rx <= u <= rx + 0.07 and v <= ry - 0.02
        if stem or arm_l or arm_r:
            col = lerp(GOLD_BRIGHT, GOLD, 0.35)
        # hem
        if abs(v - top) < 0.012 or abs(v - bottom) < 0.012:
            col = lerp(col, GOLD, 0.8)
    return col


def render():
    rows = []
    for py in range(SIZE):
        row = bytearray([0])                          # PNG filter type 0 per scanline
        for px in range(SIZE):
            r = g = b = 0
            for sy in range(SS):
                for sx in range(SS):
                    c = shade(px * SS + sx + 0.5, py * SS + sy + 0.5)
                    r += c[0]
                    g += c[1]
                    b += c[2]
            n = SS * SS
            row += bytes((r // n, g // n, b // n, 255))
        rows.append(bytes(row))
    return b"".join(rows)


def png(raw):
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)   # 8-bit RGBA
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def main():
    out = pathlib.Path(__file__).resolve().parent.parent / "HexiumDist" / "icon.png"
    out.write_bytes(png(render()))
    print(f"wrote {out} ({out.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
