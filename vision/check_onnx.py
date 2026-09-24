"""Runs an exported model on images through ONNX Runtime on DirectML (the GPU path HitCam uses) and on the CPU:
detections, speed, and whether the two agree.

    python check_onnx.py output/models/rfdetr-nano.onnx photo.jpg
    python check_onnx.py %LOCALAPPDATA%/HitCam/models/cups.onnx data/cups/test --save output/check
    python check_onnx.py model.onnx photo.jpg --providers dml --threshold 0.3

Needs <stem>.labels.json next to <stem>.onnx (input size, mean/std, class names); without it the COCO defaults of
RF-DETR Nano are assumed and classes are shown as numbers.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image

from common import IMAGE_EXTENSIONS, OnnxDetector, draw, match

PROVIDERS = {"dml": "DmlExecutionProvider", "cpu": "CPUExecutionProvider"}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("model", type=Path, help="<stem>.onnx")
    parser.add_argument("images", type=Path, nargs="+", help="image files or folders")
    parser.add_argument("--threshold", type=float, default=0.5)
    parser.add_argument("--providers", default="dml,cpu", help="comma list of dml, cpu (default: dml,cpu)")
    parser.add_argument("--runs", type=int, default=30, help="timed runs per provider (default 30)")
    parser.add_argument("--save", type=Path, help="folder for images with the boxes drawn (from the first provider)")
    args = parser.parse_args()

    files = []
    for p in args.images:
        files += sorted(f for f in p.iterdir() if f.suffix.lower() in IMAGE_EXTENSIONS) if p.is_dir() else [p]
    if not files:
        raise SystemExit("no images")

    detectors = []
    for key in args.providers.split(","):
        detector = OnnxDetector(args.model, PROVIDERS[key.strip()])
        if detector.provider != PROVIDERS[key.strip()]:
            print(f"WARNING: {PROVIDERS[key.strip()]} is not available, ONNX Runtime fell back to {detector.provider}")
        detectors.append(detector)
    first = detectors[0]
    print(f"{args.model}: input {first.size}x{first.size}, "
          f"{len(first.classes) if first.classes else '?'} classes, meta {'yes' if first.meta else 'NO labels.json'}")
    if args.save:
        args.save.mkdir(parents=True, exist_ok=True)

    agree = True
    for path in files:
        image = Image.open(path).convert("RGB")
        results = []
        for detector in detectors:
            found = detector.detect(image, args.threshold)
            results.append(found)
            text = ", ".join(f"{detector.name_of(d.class_id)} {d.score:.2f} "
                             f"[{', '.join(str(round(v)) for v in d.box)}]" for d in found) or "nothing"
            print(f"  {path.name} {detector.provider:22} {text}")
        if len(results) > 1:
            pairs, a, b = match(results[0], results[1], 0.9)
            if a or b:
                agree = False
                print(f"  {path.name}: providers DISAGREE ({len(a)} + {len(b)} unmatched boxes)")
        if args.save:
            draw(image, results[0], first.classes).save(args.save / f"{path.stem}.jpg", quality=90)

    print("\nspeed (one frame, after warm-up):")
    for detector in detectors:
        ms = detector.benchmark(Image.open(files[0]).convert("RGB"), runs=args.runs)
        print(f"  {detector.provider:22} {ms:7.1f} ms  ({1000 / ms:.0f} FPS)")
    if len(detectors) > 1:
        print("providers agree" if agree else "providers DISAGREE")
    raise SystemExit(0 if agree else 1)


if __name__ == "__main__":
    main()
