"""Splits a labelled COCO dataset (for example a Label Studio "COCO" export) into train / valid / test in the folder
layout RF-DETR reads:

    vision/data/<dataset>/
        train/_annotations.coco.json + the train images
        valid/_annotations.coco.json + the valid images
        test/_annotations.coco.json  + the test images

    python split.py mycups                              # reads data/mycups/export/result.json
    python split.py mycups --coco D:/ls/result.json     # any COCO json
    python split.py mycups --valid 0.2 --test 0.1 --seed 7

Images are looked up next to the json (as file_name says), in <json folder>/images and in data/<dataset>/images
(also without the "1a2b3c4d-" prefix Label Studio adds to uploaded files). Images without boxes are kept as
"background" examples unless --drop-empty is given.
"""

from __future__ import annotations

import argparse
import json
import random
import re
import shutil
from collections import Counter
from pathlib import Path

from common import DATA

SPLITS = ("train", "valid", "test")
UPLOAD_PREFIX = re.compile(r"^[0-9a-f]{8}-")


def find_image(file_name: str, json_dir: Path, dataset_dir: Path) -> Path | None:
    name = Path(file_name.replace("\\", "/")).name
    candidates = [json_dir / file_name, json_dir / "images" / name, dataset_dir / "images" / name]
    stripped = UPLOAD_PREFIX.sub("", name)
    if stripped != name:
        candidates += [json_dir / "images" / stripped, dataset_dir / "images" / stripped]
    return next((c for c in candidates if c.is_file()), None)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dataset", help="dataset name: folder vision/data/<dataset>")
    parser.add_argument("--coco", type=Path, help="COCO json (default: data/<dataset>/export/result.json)")
    parser.add_argument("--valid", type=float, default=0.15, help="share of images for valid (default 0.15)")
    parser.add_argument("--test", type=float, default=0.15, help="share of images for test (default 0.15)")
    parser.add_argument("--seed", type=int, default=0, help="shuffle seed: same seed = same split")
    parser.add_argument("--drop-empty", action="store_true", help="skip images that have no boxes")
    args = parser.parse_args()

    dataset_dir = DATA / args.dataset
    coco_path = args.coco or dataset_dir / "export" / "result.json"
    if not coco_path.exists():
        raise SystemExit(f"no annotations at {coco_path}: export COCO from Label Studio and unzip it there")
    coco = json.loads(coco_path.read_text(encoding="utf-8"))
    categories = sorted(coco["categories"], key=lambda c: int(c["id"]))
    by_image: dict[int, list] = {}
    for a in coco["annotations"]:
        by_image.setdefault(a["image_id"], []).append(a)

    images, missing, empty = [], [], 0
    for image in coco["images"]:
        path = find_image(image["file_name"], coco_path.parent, dataset_dir)
        if path is None:
            missing.append(image["file_name"])
            continue
        if not by_image.get(image["id"]):
            empty += 1
            if args.drop_empty:
                continue
        images.append((image, path))
    if missing:
        print(f"WARNING: {len(missing)} images not found, skipped (first: {missing[0]})")
    if not images:
        raise SystemExit("no images found")

    rng = random.Random(args.seed)
    rng.shuffle(images)
    n = len(images)
    n_valid = max(1, round(n * args.valid))
    n_test = max(1, round(n * args.test)) if args.test > 0 else 0
    parts = {"valid": images[:n_valid], "test": images[n_valid:n_valid + n_test],
             "train": images[n_valid + n_test:]}
    if len(parts["train"]) < 1:
        raise SystemExit("too few images for a split")

    for split in SPLITS:
        out = dataset_dir / split
        if out.exists():
            shutil.rmtree(out)
        out.mkdir(parents=True)
        new_images, new_annotations, used_names, counts = [], [], set(), Counter()
        # ids start at 1: pycocotools treats annotation id 0 as "not matched" and would count it as a miss
        for new_id, (image, path) in enumerate(parts[split], start=1):
            name = path.name
            if name in used_names:  # two different folders with the same file name
                name = f"{new_id}_{name}"
            used_names.add(name)
            shutil.copy2(path, out / name)
            new_images.append({"id": new_id, "file_name": name, "width": image["width"], "height": image["height"]})
            for a in by_image.get(image["id"], []):
                x, y, w, h = (float(v) for v in a["bbox"])
                if w <= 1 or h <= 1:
                    continue
                new_annotations.append({"id": len(new_annotations) + 1, "image_id": new_id,
                                        "category_id": int(a["category_id"]), "bbox": [x, y, w, h],
                                        "area": w * h, "iscrowd": 0})
                counts[int(a["category_id"])] += 1
        # every split keeps the full category list: rfdetr maps category ids to model outputs from train's list
        data = {"images": new_images, "annotations": new_annotations,
                "categories": [{"id": int(c["id"]), "name": c["name"], "supercategory": "none"} for c in categories]}
        (out / "_annotations.coco.json").write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
        per_class = ", ".join(f"{c['name']} {counts[int(c['id'])]}" for c in categories)
        print(f"{split:5}: {len(new_images):4} images, {len(new_annotations):5} boxes ({per_class})")
        absent = [c["name"] for c in categories if counts[int(c["id"])] == 0]
        if absent and new_images:
            print(f"       WARNING: no {', '.join(absent)} in {split}; collect more images or try another --seed")
    print(f"images without boxes: {empty}{' (dropped)' if args.drop_empty else ' (kept as background)'}")
    print(f"dataset ready: {dataset_dir}\nnext: python train.py {args.dataset}")


if __name__ == "__main__":
    main()
