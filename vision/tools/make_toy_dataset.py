"""Synthetic "toy" dataset for checking the whole pipeline without a camera and without labelling by hand.

Draws 3 kinds of simple objects (red ball, blue box, yellow star) plus distractors on random cluttered backgrounds and
writes exactly what you would get after collect.py + Label Studio:

    vision/data/<name>/images/*.jpg          frames
    vision/data/<name>/export/result.json    COCO annotations in Label Studio's export style (category ids from 0)

Then:  python split.py <name>   ->  train/ valid/ test/  ->  train.py / eval.py / export.py

    python tools/make_toy_dataset.py                 # 100 images into data/toy
    python tools/make_toy_dataset.py --count 300 --name toy300
"""

from __future__ import annotations

import argparse
import json
import math
import random
import shutil
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from common import DATA  # noqa: E402

CLASSES = ["red_ball", "blue_box", "yellow_star"]
WIDTH, HEIGHT = 640, 480


def jitter(rgb, amount=30, rng=random):
    return tuple(max(0, min(255, c + rng.randint(-amount, amount))) for c in rgb)


def background(rng: random.Random) -> Image.Image:
    """Gradient + random clutter + noise, so the model has to learn the objects, not the scene."""
    top, bottom = jitter((rng.randint(60, 200),) * 3, 60, rng), jitter((rng.randint(60, 200),) * 3, 60, rng)
    image = Image.new("RGB", (WIDTH, HEIGHT))
    canvas = ImageDraw.Draw(image)
    for y in range(HEIGHT):
        t = y / (HEIGHT - 1)
        canvas.line([(0, y), (WIDTH, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(top, bottom)))
    for _ in range(rng.randint(5, 25)):  # clutter: muted rectangles, lines, ellipses
        color = jitter((rng.randint(40, 220),) * 3, 40, rng)
        x, y = rng.randint(-50, WIDTH), rng.randint(-50, HEIGHT)
        w, h = rng.randint(10, 200), rng.randint(10, 200)
        kind = rng.random()
        if kind < 0.4:
            canvas.rectangle([x, y, x + w, y + h], fill=color)
        elif kind < 0.7:
            canvas.line([x, y, x + w, y + h], fill=color, width=rng.randint(1, 8))
        else:
            canvas.ellipse([x, y, x + w, y + h], outline=color, width=rng.randint(1, 5))
    return image


def star_points(cx, cy, r, angle, n=5):
    points = []
    for i in range(n * 2):
        radius = r if i % 2 == 0 else r * 0.45
        a = angle + math.pi * i / n
        points.append((cx + radius * math.sin(a), cy - radius * math.cos(a)))
    return points


def draw_object(canvas: ImageDraw.ImageDraw, kind: str, cx: float, cy: float, size: float, rng: random.Random):
    """Draws one object and returns its tight bounding box (x1, y1, x2, y2)."""
    if kind == "red_ball":
        r = size / 2
        base = jitter((210, 30, 30), 30, rng)
        canvas.ellipse([cx - r, cy - r, cx + r, cy + r], fill=base)
        hr = r * 0.35  # highlight so it looks like a ball, not a flat disc
        canvas.ellipse([cx - r * 0.5 - hr / 2, cy - r * 0.5 - hr / 2, cx - r * 0.5 + hr / 2, cy - r * 0.5 + hr / 2],
                       fill=jitter((255, 150, 150), 20, rng))
        return cx - r, cy - r, cx + r, cy + r
    if kind == "blue_box":
        angle = rng.uniform(0, math.pi / 2)
        w, h = size * rng.uniform(0.7, 1.0), size * rng.uniform(0.7, 1.0)
        corners = []
        for dx, dy in ((-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2)):
            corners.append((cx + dx * math.cos(angle) - dy * math.sin(angle),
                            cy + dx * math.sin(angle) + dy * math.cos(angle)))
        canvas.polygon(corners, fill=jitter((30, 70, 200), 30, rng), outline=jitter((10, 20, 90), 10, rng), width=3)
        xs, ys = [p[0] for p in corners], [p[1] for p in corners]
        return min(xs), min(ys), max(xs), max(ys)
    points = star_points(cx, cy, size / 2, rng.uniform(0, 2 * math.pi))
    canvas.polygon(points, fill=jitter((240, 210, 30), 20, rng), outline=jitter((150, 110, 0), 10, rng), width=2)
    xs, ys = [p[0] for p in points], [p[1] for p in points]
    return min(xs), min(ys), max(xs), max(ys)


def draw_distractor(canvas, rng):
    """Objects that must NOT be detected: green triangles and grey/purple discs."""
    cx, cy, s = rng.uniform(0, WIDTH), rng.uniform(0, HEIGHT), rng.uniform(30, 120)
    if rng.random() < 0.5:
        canvas.polygon([(cx, cy - s / 2), (cx + s / 2, cy + s / 2), (cx - s / 2, cy + s / 2)],
                       fill=jitter((40, 160, 60), 30, rng))
    else:
        canvas.ellipse([cx - s / 2, cy - s / 2, cx + s / 2, cy + s / 2], fill=jitter((120, 90, 140), 30, rng))


def overlap(a, b):
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    smaller = min((a[2] - a[0]) * (a[3] - a[1]), (b[2] - b[0]) * (b[3] - b[1]))
    return ix * iy / smaller if smaller > 0 else 0.0


def make_image(rng: random.Random):
    image = background(rng)
    canvas = ImageDraw.Draw(image)
    for _ in range(rng.randint(0, 3)):
        draw_distractor(canvas, rng)
    boxes = []
    for _ in range(rng.randint(1, 4)):
        kind = rng.choice(CLASSES)
        for _attempt in range(20):
            size = rng.uniform(40, 170)
            cx, cy = rng.uniform(size / 2, WIDTH - size / 2), rng.uniform(size / 2, HEIGHT - size / 2)
            # predict the box roughly to avoid heavy overlaps (objects hiding each other are a separate lesson)
            guess = (cx - size * 0.6, cy - size * 0.6, cx + size * 0.6, cy + size * 0.6)
            if all(overlap(guess, b) < 0.2 for _, b in boxes):
                break
        else:
            continue
        box = draw_object(canvas, kind, cx, cy, size, rng)
        box = (max(0.0, box[0]), max(0.0, box[1]), min(WIDTH, box[2]), min(HEIGHT, box[3]))
        boxes.append((kind, box))
    if rng.random() < 0.5:
        image = image.filter(ImageFilter.GaussianBlur(rng.uniform(0.3, 1.5)))  # slightly out of focus
    return image, boxes


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--name", default="toy", help="dataset name, folder vision/data/<name> (default: toy)")
    parser.add_argument("--count", type=int, default=100, help="number of images (default: 100)")
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--ids", default="0,1,2",
                        help="COCO category ids for red_ball,blue_box,yellow_star (default 0,1,2 like Label Studio); "
                             "e.g. 7,3,5 checks that the id -> model output mapping survives unsorted ids")
    args = parser.parse_args()
    ids = [int(v) for v in args.ids.split(",")]
    if len(ids) != len(CLASSES) or len(set(ids)) != len(ids):
        parser.error(f"--ids needs {len(CLASSES)} different numbers")

    root = DATA / args.name
    if root.exists():
        shutil.rmtree(root)
    (root / "images").mkdir(parents=True)
    (root / "export").mkdir()
    rng = random.Random(args.seed)
    images, annotations = [], []
    for i in range(args.count):
        image, boxes = make_image(rng)
        name = f"toy_{i:04d}.jpg"
        image.save(root / "images" / name, quality=rng.randint(70, 95))
        images.append({"id": i, "file_name": f"images/{name}", "width": WIDTH, "height": HEIGHT})
        for kind, (x1, y1, x2, y2) in boxes:
            annotations.append({"id": len(annotations), "image_id": i, "category_id": ids[CLASSES.index(kind)],
                                "bbox": [round(x1, 2), round(y1, 2), round(x2 - x1, 2), round(y2 - y1, 2)],
                                "area": round((x2 - x1) * (y2 - y1), 2), "iscrowd": 0, "segmentation": [],
                                "ignore": 0})
    coco = {"images": images, "annotations": annotations,
            "categories": [{"id": i, "name": name} for i, name in zip(ids, CLASSES)],
            "info": {"description": "HitCam Vision toy dataset", "contributor": "make_toy_dataset.py"}}
    (root / "export" / "result.json").write_text(json.dumps(coco, indent=1), encoding="utf-8")
    counts = {name: sum(a["category_id"] == i for a in annotations) for i, name in zip(ids, CLASSES)}
    print(f"{args.count} images, {len(annotations)} boxes {counts} -> {root}")
    print(f"next: python split.py {args.name}")


if __name__ == "__main__":
    main()
