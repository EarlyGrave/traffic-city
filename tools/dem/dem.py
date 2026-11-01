#!/usr/bin/env python3
"""tools/dem: плитки terrarium z13 (SRTM, общественное достояние) -> окно -> .r16 + JSON-фрагмент + сводка.

Только стандартная библиотека Python. Сырые плитки кладутся в --cache (вне репозитория).
Пример:
  python -I tools/dem/dem.py --lat 56.4936 --lon 39.6828 --size-m 6080 --cell-m 32 \
      --cache <scratch>/tiles --out-r16 scenarios/terrain/yuryev-polsky-32m.r16 --out-json <scratch>/frag.json \
      --river river.json --fit-radius 400
Север = -Z, восток = +X. Начало окна (origin_m) = (-size/2, -size/2) относительно якоря.
"""
import argparse, json, math, os, struct, urllib.request, zlib

Z = 13
URL = "https://elevation-tiles-prod.s3.amazonaws.com/terrarium/{z}/{x}/{y}.png"


def lonlat_to_px(lat, lon, z=Z):
    n = 256 * (1 << z)
    x = (lon + 180.0) / 360.0 * n
    s = math.sin(math.radians(lat))
    y = (0.5 - math.log((1 + s) / (1 - s)) / (4 * math.pi)) * n
    return x, y


def decode_png(data):
    """Минимальный разбор PNG 8 бит RGB/RGBA без interlace."""
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    pos, idat, w, bpp = 8, b"", 0, 3
    while pos < len(data):
        ln, = struct.unpack(">I", data[pos:pos + 4])
        typ = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + ln]
        pos += 12 + ln
        if typ == b"IHDR":
            w, h, depth, ctype, _, _, inter = struct.unpack(">IIBBBBB", body)
            assert depth == 8 and ctype in (2, 6) and inter == 0
            bpp = 3 if ctype == 2 else 4
        elif typ == b"IDAT":
            idat += body
    raw = zlib.decompress(idat)
    stride = w * bpp
    rows, prev = [], bytearray(stride)
    p = 0
    for _ in range(h):
        f = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + ((a + b) >> 1)) & 255
            elif f == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        rows.append(line)
        prev = line
    return w, h, bpp, rows


def load_tile(tx, ty, cache):
    path = os.path.join(cache, f"{Z}_{tx}_{ty}.png")
    if not os.path.exists(path) or os.path.getsize(path) == 0:
        os.makedirs(cache, exist_ok=True)
        for attempt in range(5):
            try:
                with urllib.request.urlopen(URL.format(z=Z, x=tx, y=ty), timeout=60) as r:
                    body = r.read()
                break
            except OSError:
                if attempt == 4:
                    raise
        with open(path + ".part", "wb") as f:
            f.write(body)
        os.replace(path + ".part", path)
    w, h, bpp, rows = decode_png(open(path, "rb").read())
    return [[(r[x * bpp] * 256 + r[x * bpp + 1] + r[x * bpp + 2] / 256.0 - 32768.0) for x in range(w)] for r in rows]


class Mosaic:
    def __init__(self, x0, y0, nx, ny, cache):
        self.x0, self.y0, self.nx, self.ny = x0, y0, nx, ny
        self.w, self.h = nx * 256, ny * 256
        self.g = [[0.0] * self.w for _ in range(self.h)]
        for j in range(ny):
            for i in range(nx):
                t = load_tile(x0 + i, y0 + j, cache)
                for r in range(256):
                    self.g[j * 256 + r][i * 256:(i + 1) * 256] = t[r]

    def smooth3(self):
        g, w, h = self.g, self.w, self.h
        o = [row[:] for row in g]
        for y in range(1, h - 1):
            for x in range(1, w - 1):
                o[y][x] = sum(g[y + dy][x + dx] for dy in (-1, 0, 1) for dx in (-1, 0, 1)) / 9.0
        self.g = o

    def sample(self, px, py):
        """Билинейно по глобальным пиксельным координатам."""
        fx, fy = px - self.x0 * 256 - 0.5, py - self.y0 * 256 - 0.5
        ix, iy = int(math.floor(fx)), int(math.floor(fy))
        tx, ty = fx - ix, fy - iy
        ix = max(0, min(self.w - 2, ix))
        iy = max(0, min(self.h - 2, iy))
        g = self.g
        return (g[iy][ix] * (1 - tx) * (1 - ty) + g[iy][ix + 1] * tx * (1 - ty)
                + g[iy + 1][ix] * (1 - tx) * ty + g[iy + 1][ix + 1] * tx * ty)


def build_grid(m, lat, lon, size_m, cell_m, dx=0.0, dz=0.0):
    """Окно size_m с центром, смещённым на (dx на восток, dz на юг) м от (lat, lon). Возвращает n, h[z][x]."""
    mpp = 156543.03392 * math.cos(math.radians(lat)) / (1 << Z)  # м на пиксель
    cx, cy = lonlat_to_px(lat, lon)
    cx += dx / mpp
    cy += dz / mpp
    n = int(round(size_m / cell_m)) + 1
    half = size_m / 2.0
    grid = []
    for iz in range(n):
        grid.append([m.sample(cx + (-half + ix * cell_m) / mpp, cy + (-half + iz * cell_m) / mpp) for ix in range(n)])
    return n, grid


def sample_grid(grid, n, cell_m, half, x, z):
    fx, fz = (x + half) / cell_m, (z + half) / cell_m
    ix = max(0, min(n - 2, int(math.floor(fx))))
    iz = max(0, min(n - 2, int(math.floor(fz))))
    tx, tz = fx - ix, fz - iz
    return (grid[iz][ix] * (1 - tx) * (1 - tz) + grid[iz][ix + 1] * tx * (1 - tz)
            + grid[iz + 1][ix] * (1 - tx) * tz + grid[iz + 1][ix + 1] * tx * tz)


def polyline_samples(pts, step=20.0):
    out = []
    for (x0, z0), (x1, z1) in zip(pts, pts[1:]):
        L = math.hypot(x1 - x0, z1 - z0)
        k = max(1, int(L / step))
        for i in range(k):
            out.append((x0 + (x1 - x0) * i / k, z0 + (z1 - z0) * i / k))
    out.append(tuple(pts[-1]))
    return out


def fit_anchor(m, lat, lon, size_m, cell_m, river, radius, shift_step=50.0):
    """Сдвиг якоря (в пределах radius, шаг shift_step), при котором средняя высота под рекой минимальна."""
    best = None
    s = polyline_samples(river)
    k = int(radius // shift_step)
    for a in range(-k, k + 1):
        for b in range(-k, k + 1):
            dx, dz = a * shift_step, b * shift_step
            if math.hypot(dx, dz) > radius:
                continue
            n, g = build_grid(m, lat, lon, size_m, cell_m, dx, dz)
            tot = sum(sample_grid(g, n, cell_m, size_m / 2.0, x, z) for x, z in s) / len(s)
            if best is None or tot < best[0]:
                best = (tot, dx, dz)
    return best


def summary(grid, n, cell_m):
    flat = sorted(v for r in grid for v in r)
    sl = []
    for z in range(n - 1):
        for x in range(n - 1):
            gx = (grid[z][x + 1] - grid[z][x]) / cell_m
            gz = (grid[z + 1][x] - grid[z][x]) / cell_m
            sl.append(100 * math.hypot(gx, gz))
    sl.sort()

    def pct(a, p):
        return a[min(len(a) - 1, int(p * len(a)))]
    return {"min_m": round(flat[0], 2), "max_m": round(flat[-1], 2), "median_m": round(pct(flat, .5), 2),
            "slope_p50_pct": round(pct(sl, .5), 2), "slope_p90_pct": round(pct(sl, .9), 2), "slope_p99_pct": round(pct(sl, .99), 2),
            "share_gt8": round(sum(1 for v in sl if v > 8) / len(sl) * 100, 2),
            "share_gt12": round(sum(1 for v in sl if v > 12) / len(sl) * 100, 2)}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lat", type=float, required=True)
    ap.add_argument("--lon", type=float, required=True)
    ap.add_argument("--size-m", type=float, default=6080)
    ap.add_argument("--cell-m", type=float, default=32)
    ap.add_argument("--cache", required=True, help="папка для скачанных плиток (вне репозитория)")
    ap.add_argument("--out-r16")
    ap.add_argument("--out-json")
    ap.add_argument("--river", help="JSON-файл с массивом точек реки [[x,z],...] относительно якоря")
    ap.add_argument("--fit-radius", type=float, default=0.0, help="подбор якоря по реке в пределах R м (0 = без подбора)")
    ap.add_argument("--shift-east", type=float, default=0.0, help="явный сдвиг якоря на восток, м (без подбора; повтор привязки другого окна)")
    ap.add_argument("--shift-south", type=float, default=0.0, help="явный сдвиг якоря на юг, м")
    a = ap.parse_args()

    mpp = 156543.03392 * math.cos(math.radians(a.lat)) / (1 << Z)
    cx, cy = lonlat_to_px(a.lat, a.lon)
    margin = (a.size_m / 2 + a.fit_radius + math.hypot(a.shift_east, a.shift_south)) / mpp + 4
    tx0, tx1 = int((cx - margin) // 256), int((cx + margin) // 256)
    ty0, ty1 = int((cy - margin) // 256), int((cy + margin) // 256)
    m = Mosaic(tx0, ty0, tx1 - tx0 + 1, ty1 - ty0 + 1, a.cache)
    m.smooth3()
    dx, dz = a.shift_east, a.shift_south
    fit = None
    if a.river and a.fit_radius > 0:
        fit = fit_anchor(m, a.lat, a.lon, a.size_m, a.cell_m, json.load(open(a.river)), a.fit_radius)
        dx, dz = fit[1], fit[2]
    n, g = build_grid(m, a.lat, a.lon, a.size_m, a.cell_m, dx, dz)
    base = math.floor(min(v for r in g for v in r))
    data = b"".join(struct.pack("<H", max(0, min(65535, int(round((v - base) / 0.01))))) for r in g for v in r)
    if a.out_r16:
        os.makedirs(os.path.dirname(os.path.abspath(a.out_r16)), exist_ok=True)
        open(a.out_r16, "wb").write(data)
    half = a.size_m / 2
    res = {"fragment": {"heightmap": {"size": [n, n], "cell_m": a.cell_m, "origin_m": [-half, -half],
                                      "base_m": float(base), "unit_m": 0.01}},
           "summary": summary(g, n, a.cell_m),
           "anchor_shift_m": {"east": dx, "south": dz}, "window_centre_lat_lon": [a.lat, a.lon],
           "river_mean_height_m": round(fit[0], 2) if fit else None, "pixel_m": round(mpp, 3)}
    if a.out_json:
        json.dump(res, open(a.out_json, "w"), indent=2)
    print(json.dumps(res, indent=2))


if __name__ == "__main__":
    main()
