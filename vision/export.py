"""Exports a trained model to ONNX (FP32) + labels.json, checks it on DirectML against PyTorch and, with --install,
puts it where the HitCam PC app finds it (%LOCALAPPDATA%\\HitCam\\models).

    python export.py mycups-nano                      # -> output/models/mycups-nano.onnx + .labels.json
    python export.py mycups-nano --install            # ... and copy into HitCam
    python export.py mycups-nano --stem cups --title "Мои кружки" --install

The check runs both models on up to --check images of the test split (valid if there is no test) and requires the same
detections: same classes, box IoU >= 0.95. Detections within 0.05 of the threshold are ignored there because a
tiny numeric difference can move them to the other side of it.
"""

from __future__ import annotations

import argparse
import re
import shutil
import time
from pathlib import Path

from PIL import Image

from common import (IMAGE_EXTENSIONS, OUTPUT, OnnxDetector, from_supervision, hitcam_models_dir, iou,
                    labels_metadata, quiet, write_labels)
from trained import dataset_classes, load_run

VARIANTS = {"RFDETRNano": "RF-DETR Nano", "RFDETRSmall": "RF-DETR Small"}


def verify(model, onnx_path: Path, images: list[Path], threshold: float = 0.5, margin: float = 0.05) -> bool:
    detector = OnnxDetector(onnx_path, "DmlExecutionProvider")
    print(f"\ncheck: PyTorch vs ONNX on {detector.provider}, {len(images)} images, threshold {threshold}")
    ok = True
    worst_iou, total = 1.0, 0
    for path in images:
        image = Image.open(path).convert("RGB")
        reference = from_supervision(model.predict(image, threshold=threshold - margin))
        exported = detector.detect(image, threshold - margin)
        sure_ref = [d for d in reference if d.score >= threshold + margin]
        sure_onnx = [d for d in exported if d.score >= threshold + margin]
        problems = []
        for d in sure_ref:  # every confident PyTorch box exists in ONNX ...
            best = max((iou(d.box, e.box) for e in exported if e.class_id == d.class_id), default=0.0)
            worst_iou = min(worst_iou, best)
            if best < 0.95:
                problems.append(f"only in PyTorch: {d} (best IoU {best:.3f})")
        for d in sure_onnx:  # ... and the other way round
            best = max((iou(d.box, e.box) for e in reference if e.class_id == d.class_id), default=0.0)
            if best < 0.95:
                problems.append(f"only in ONNX: {d} (best IoU {best:.3f})")
        total += len(sure_ref)
        status = "ok" if not problems else "MISMATCH"
        print(f"  {path.name}: PyTorch {len(sure_ref)}, ONNX {len(sure_onnx)} confident detections - {status}")
        for p in problems:
            print("     ", p)
        ok &= not problems
    print(f"  {total} confident detections compared, worst box IoU {worst_iou:.4f}")
    ms = detector.benchmark(Image.open(images[0]).convert("RGB"))
    print(f"  ONNX speed on {detector.provider}: {ms:.1f} ms per frame")
    return ok


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("run", help="run name (folder vision/runs/<run>) or a path to a .pth checkpoint")
    parser.add_argument("--stem", help="file name without extension (default: the run name)")
    parser.add_argument("--title", help="name shown in HitCam (default: '<stem> (RF-DETR Nano)')")
    parser.add_argument("--out", type=Path, default=OUTPUT / "models", help="output folder (default output/models)")
    parser.add_argument("--install", action="store_true", help="copy into %%LOCALAPPDATA%%\\HitCam\\models")
    parser.add_argument("--check", type=int, default=8, help="images for the PyTorch/ONNX check; 0 = skip")
    parser.add_argument("--dataset", help="dataset for the check, if not the one the run was trained on")
    args = parser.parse_args()
    quiet()

    run = load_run(args.run, args.dataset)
    stem = args.stem or run.name
    if not re.fullmatch(r"[\w.\-]+", stem):
        raise SystemExit(f"--stem {stem!r}: use letters, digits, '-', '_' or '.'")

    model = run.load_model()
    names = list(model.class_names)
    if run.dataset_dir and (run.dataset_dir / "train" / "_annotations.coco.json").exists():
        from_dataset = [name for _, name in dataset_classes(run.dataset_dir)]
        if from_dataset != names:
            raise SystemExit(f"class names in the checkpoint {names} differ from the dataset {from_dataset}")
    size = int(model.model_config.resolution)
    variant = VARIANTS.get(type(model).__name__, type(model).__name__)
    title = args.title or f"{stem} ({variant})"
    print(f"{run.checkpoint}: {variant}, {size}x{size}, classes {names}")

    args.out.mkdir(parents=True, exist_ok=True)
    start = time.perf_counter()
    onnx_path = Path(model.export(output_dir=str(args.out), output_name=stem, verbose=False))
    if onnx_path.suffix != ".onnx":
        onnx_path = next(onnx_path.glob("*.onnx"))
    labels_path = args.out / f"{stem}.labels.json"
    # label index i = logits column i; the last column (index len(names)) is "no object" and has no name
    write_labels(labels_path, labels_metadata(title, size, dict(enumerate(names))))
    print(f"exported in {time.perf_counter() - start:.0f} s: {onnx_path} ({onnx_path.stat().st_size / 1e6:.0f} MB)"
          f"\n                    {labels_path}")

    detector = OnnxDetector(onnx_path, "CPUExecutionProvider")
    columns = detector.session.get_outputs()[1].shape[-1]
    if columns != len(names) + 1:
        raise SystemExit(f"unexpected ONNX logits width {columns}, expected {len(names) + 1}")
    del detector

    ok = True
    if args.check > 0:
        images = []
        if run.dataset_dir:
            for split in ("test", "valid"):
                folder = run.dataset_dir / split
                if folder.is_dir():
                    images = sorted(p for p in folder.iterdir() if p.suffix.lower() in IMAGE_EXTENSIONS)
                    if images:
                        break
        if images:
            ok = verify(model, onnx_path, images[:args.check])
            print("check:", "PASSED" if ok else "FAILED")
        else:
            print("check skipped: no test/valid images found (use --dataset)")

    if args.install:
        if not ok:
            raise SystemExit("not installing: the ONNX model does not match PyTorch")
        target = hitcam_models_dir()
        target.mkdir(parents=True, exist_ok=True)
        shutil.copy2(onnx_path, target / onnx_path.name)
        shutil.copy2(labels_path, target / labels_path.name)
        print(f"installed into {target}: restart HitCam or reopen the model list, pick '{title}'")
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    main()
