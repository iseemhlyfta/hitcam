"""Exports the stock COCO-trained RF-DETR (80 everyday classes: person, cup, dog, ...) to ONNX FP32 + labels.json.
This is the default model bundled with HitCam; CI runs it on a CPU-only machine.

    python export_default.py                            # Nano  -> output/models/rfdetr-nano.onnx + .labels.json
    python export_default.py --model small --out dist/models
    python export_default.py --image photo.jpg          # also print what it finds on a photo

The pretrained weights (~100 MB) are downloaded once from Roboflow into %USERPROFILE%\\.roboflow\\models
(set RF_HOME to change that). No GPU is needed: export traces the model on the CPU when CUDA is not available.
"""

from __future__ import annotations

import argparse
import time
from pathlib import Path

import numpy as np

from common import MODELS, OUTPUT, OnnxDetector, labels_metadata, model_class, quiet, write_labels

TITLES = {"nano": "RF-DETR Nano (COCO)", "small": "RF-DETR Small (COCO)"}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", choices=list(MODELS), default="nano")
    parser.add_argument("--out", type=Path, default=OUTPUT / "models", help="output folder (default output/models)")
    parser.add_argument("--image", type=Path, help="optional photo to run the exported model on")
    parser.add_argument("--cpu", action="store_true", help="export on the CPU even if a CUDA GPU is present")
    args = parser.parse_args()
    quiet()

    import torch
    from rfdetr.assets.coco_classes import COCO_CLASSES

    stem = f"rfdetr-{args.model}"
    size = MODELS[args.model][1]
    device = "cuda" if torch.cuda.is_available() and not args.cpu else "cpu"
    print(f"{TITLES[args.model]}: exporting on {device}")
    start = time.perf_counter()
    model = model_class(args.model)(device=device)
    if model.model_config.resolution != size:
        raise SystemExit(f"unexpected resolution {model.model_config.resolution}, expected {size}")
    args.out.mkdir(parents=True, exist_ok=True)
    onnx_path = Path(model.export(output_dir=str(args.out), output_name=stem, verbose=False))
    if onnx_path.suffix != ".onnx":
        onnx_path = next(onnx_path.glob("*.onnx"))
    # The COCO head has 91 columns and column i is COCO category id i (1 = person ... 90 = toothbrush);
    # the 11 ids COCO never used (0, 12, 26, ...) have no name and are ignored.
    labels_path = args.out / f"{stem}.labels.json"
    write_labels(labels_path, labels_metadata(TITLES[args.model], size, dict(COCO_CLASSES)))
    print(f"exported in {time.perf_counter() - start:.0f} s: {onnx_path} ({onnx_path.stat().st_size / 1e6:.0f} MB)")
    print(f"                   {labels_path}")

    # Sanity check on ONNX Runtime's CPU provider (always present, also in CI): FP32 input, output names and shapes.
    detector = OnnxDetector(onnx_path, "CPUExecutionProvider")
    shapes = {o.name: o.shape for o in detector.session.get_outputs()}
    print(f"ONNX inputs {[(i.name, i.shape, i.type) for i in detector.session.get_inputs()]}, outputs {shapes}")
    if shapes.get("dets", [0])[-1] != 4 or shapes.get("labels", [0])[-1] != max(COCO_CLASSES) + 1:
        raise SystemExit("unexpected ONNX outputs")
    from PIL import Image

    gray = Image.fromarray(np.full((size, size, 3), 128, np.uint8))
    print(f"CPU inference: {detector.benchmark(gray, runs=5, warmup=1):.0f} ms per frame")
    if args.image:
        image = Image.open(args.image).convert("RGB")
        for d in detector.detect(image, 0.5):
            print(f"  {detector.name_of(d.class_id)} {d.score:.2f} [{', '.join(str(round(v)) for v in d.box)}]")


if __name__ == "__main__":
    main()
