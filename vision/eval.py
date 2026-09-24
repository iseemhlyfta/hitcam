"""Measures a trained model on the test split (images it has never seen) and draws what it found.

    python eval.py mycups-nano                    # runs/mycups-nano/checkpoint_best_total.pth on its dataset's test/
    python eval.py mycups-nano --split valid --threshold 0.4 --save 20

Prints mAP@50, mAP@50:95 (COCO metrics over all confidence levels) and, at the chosen confidence threshold, per-class
precision (how many found boxes are right) and recall (how many real objects were found), with IoU >= 0.5.
Writes runs/<name>/eval/metrics.json and images with ground truth (green) and predictions (coloured, with scores).
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import math
from pathlib import Path

from PIL import Image

from common import Detection, draw, from_supervision, match, quiet
from trained import dataset_classes, load_run

GROUND_TRUTH = (40, 200, 40)


def coco_map(annotations: dict, predictions: list[dict]) -> tuple[float, float, dict[int, float]]:
    """COCO mAP@[.5:.95], mAP@.5 and per-category AP@.5 via pycocotools."""
    from pycocotools.coco import COCO
    from pycocotools.cocoeval import COCOeval

    if not predictions:
        return 0.0, 0.0, {}
    with contextlib.redirect_stdout(io.StringIO()):
        truth = COCO()
        truth.dataset = json.loads(json.dumps(annotations))
        for i, a in enumerate(truth.dataset["annotations"], start=1):
            a["id"] = i  # pycocotools silently counts a ground truth with id 0 as never matched
            a.setdefault("iscrowd", 0)
            a.setdefault("area", a["bbox"][2] * a["bbox"][3])
        truth.createIndex()
        found = truth.loadRes(predictions)
        evaluator = COCOeval(truth, found, "bbox")
        evaluator.evaluate()
        evaluator.accumulate()
        evaluator.summarize()
    # precision: [iou thresholds, recall points, categories, area ranges, max dets]; iou index 0 = 0.50
    precision = evaluator.eval["precision"]
    per_class = {}
    for k, category_id in enumerate(evaluator.params.catIds):
        values = precision[0, :, k, 0, -1]
        values = values[values > -1]
        per_class[category_id] = float(values.mean()) if values.size else float("nan")
    return float(evaluator.stats[0]), float(evaluator.stats[1]), per_class


def none_if_nan(value: float) -> float | None:
    return None if math.isnan(value) else value


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("run", help="run name (folder vision/runs/<run>) or a path to a .pth checkpoint")
    parser.add_argument("--dataset", help="dataset name, if not the one the run was trained on")
    parser.add_argument("--split", default="test", choices=["test", "valid", "train"])
    parser.add_argument("--threshold", type=float, default=0.5, help="confidence for precision/recall (default 0.5)")
    parser.add_argument("--save", type=int, default=12, help="how many annotated images to save (default 12)")
    args = parser.parse_args()
    quiet()

    run = load_run(args.run, args.dataset)
    if run.dataset_dir is None or not (run.dataset_dir / args.split).is_dir():
        raise SystemExit(f"no {args.split} split for this run; pass --dataset <name>")
    split_dir = run.dataset_dir / args.split
    annotations = json.loads((split_dir / "_annotations.coco.json").read_text(encoding="utf-8"))
    classes = dataset_classes(run.dataset_dir)          # label index -> (category id, name)
    names = {str(i): name for i, (_, name) in enumerate(classes)}
    to_label = {category_id: i for i, (category_id, _) in enumerate(classes)}
    print(f"{run.checkpoint} on {split_dir} ({len(annotations['images'])} images), classes {list(names.values())}")

    model = run.load_model()
    out_dir = run.dir / "eval"
    out_dir.mkdir(parents=True, exist_ok=True)
    for old in out_dir.glob("*.jpg"):
        old.unlink()

    boxes_by_image: dict[int, list] = {}
    for a in annotations["annotations"]:
        x, y, w, h = a["bbox"]
        boxes_by_image.setdefault(a["image_id"], []).append(
            Detection(to_label[a["category_id"]], 1.0, (x, y, x + w, y + h)))

    predictions = []
    stats = {i: {"tp": 0, "fp": 0, "fn": 0} for i in range(len(classes))}
    for n, info in enumerate(annotations["images"]):
        image = Image.open(split_dir / info["file_name"]).convert("RGB")
        found = [d for d in from_supervision(model.predict(image, threshold=0.01)) if d.class_id < len(classes)]
        for d in found:
            x1, y1, x2, y2 = d.box
            predictions.append({"image_id": info["id"], "category_id": classes[d.class_id][0],
                                "bbox": [x1, y1, x2 - x1, y2 - y1], "score": d.score})
        confident = [d for d in found if d.score >= args.threshold]
        truth = boxes_by_image.get(info["id"], [])
        pairs, false_positives, missed = match(confident, truth, 0.5)
        for i, j, _ in pairs:
            stats[truth[j].class_id]["tp"] += 1
        for i in false_positives:
            stats[confident[i].class_id]["fp"] += 1
        for j in missed:
            stats[truth[j].class_id]["fn"] += 1
        if n < args.save:
            picture = draw(image, truth, names, color=GROUND_TRUTH, width=2, label_below=True)
            picture = draw(picture, confident, names, width=3)
            picture.save(out_dir / f"{Path(info['file_name']).stem}.jpg", quality=90)

    map_50_95, map_50, ap_per_category = coco_map(annotations, predictions)
    report = {"checkpoint": str(run.checkpoint), "split": args.split, "images": len(annotations["images"]),
              "mAP50": map_50, "mAP50_95": map_50_95, "threshold": args.threshold, "classes": {}}
    print(f"\nmAP@50 = {map_50:.3f}   mAP@50:95 = {map_50_95:.3f}\n")
    print(f"{'class':20} {'AP50':>6} {'precision':>10} {'recall':>7} {'TP':>5} {'FP':>5} {'FN':>5}")
    for i, (category_id, name) in enumerate(classes):
        s = stats[i]
        precision = s["tp"] / (s["tp"] + s["fp"]) if s["tp"] + s["fp"] else float("nan")
        recall = s["tp"] / (s["tp"] + s["fn"]) if s["tp"] + s["fn"] else float("nan")
        ap = ap_per_category.get(category_id, float("nan"))
        print(f"{name:20} {ap:6.3f} {precision:10.3f} {recall:7.3f} {s['tp']:5} {s['fp']:5} {s['fn']:5}")
        report["classes"][name] = {"AP50": none_if_nan(ap), "precision": none_if_nan(precision),
                                   "recall": none_if_nan(recall), **s}
    (out_dir / "metrics.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\nimages (green = ground truth) and metrics.json -> {out_dir}")


if __name__ == "__main__":
    main()
