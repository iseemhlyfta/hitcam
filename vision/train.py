"""Fine-tunes RF-DETR (COCO-pretrained) on your own objects.

    python train.py mycups                      # RF-DETR Nano, runs/mycups-nano
    python train.py mycups --model small        # RF-DETR Small (more accurate, ~2x slower)
    python train.py mycups --epochs 100 --name mycups-long
    python train.py mycups --resume runs/mycups-nano/last.ckpt

The dataset must already be split (python split.py <dataset>): data/<dataset>/{train,valid,test}/_annotations.coco.json.
Defaults are sized for an 8 GB GPU; lower --batch (and raise --accum by the same factor) if you run out of memory.

Written to runs/<name>/:
    checkpoint_best_total.pth   best weights by validation mAP -> eval.py / export.py
    metrics.csv                 loss and mAP per epoch (open in Excel or any spreadsheet)
    training_config.json        every setting of this run, including the class names
"""

from __future__ import annotations

import argparse
import importlib.util
import time
from pathlib import Path

from common import DATA, MODELS, RUNS, model_class, quiet

# (batch, gradient accumulation): effective batch 16 as the RF-DETR authors recommend, fits 8 GB with AMP.
DEFAULT_BATCH = {"nano": (8, 2), "small": (4, 4)}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dataset", help="dataset name: folder vision/data/<dataset>")
    parser.add_argument("--model", choices=list(MODELS), default="nano")
    parser.add_argument("--name", help="run name, folder vision/runs/<name> (default: <dataset>-<model>)")
    parser.add_argument("--epochs", type=int, default=50, help="maximum passes over the train set (default 50)")
    parser.add_argument("--batch", type=int, help="images per step (default: nano 8, small 4)")
    parser.add_argument("--accum", type=int, help="steps per weight update (default: nano 2, small 4)")
    parser.add_argument("--lr", type=float, default=1e-4, help="learning rate (default 1e-4)")
    parser.add_argument("--patience", type=int, default=10,
                        help="stop after this many epochs without validation mAP improvement; 0 = never (default 10)")
    parser.add_argument("--skip-best", type=int,
                        help="ignore the first N epochs when picking the best checkpoint (default: epochs / 5): "
                             "an early lucky mAP often comes with low confidences")
    parser.add_argument("--workers", type=int, default=2, help="data loader processes (default 2)")
    parser.add_argument("--resume", type=Path, help="continue a run from its last.ckpt")
    parser.add_argument("--device", help="cuda / cpu (default: cuda if available)")
    args = parser.parse_args()
    quiet()

    dataset_dir = DATA / args.dataset
    if not (dataset_dir / "train" / "_annotations.coco.json").exists():
        raise SystemExit(f"{dataset_dir} is not split yet: python split.py {args.dataset}")
    batch, accum = DEFAULT_BATCH[args.model]
    batch, accum = args.batch or batch, args.accum or accum
    name = args.name or f"{args.dataset}-{args.model}"
    output_dir = RUNS / name

    options = dict(
        dataset_dir=str(dataset_dir),
        output_dir=str(output_dir),
        epochs=args.epochs,
        batch_size=batch,
        grad_accum_steps=accum,
        lr=args.lr,
        num_workers=args.workers,
        early_stopping=args.patience > 0,
        early_stopping_patience=max(1, args.patience),
        skip_best_epochs=args.skip_best if args.skip_best is not None else args.epochs // 5,
        # training curves in TensorBoard only if it is installed (pip install tensorboard)
        tensorboard=importlib.util.find_spec("tensorboard") is not None,
        progress_bar="tqdm",
    )
    if args.resume:
        options["resume"] = str(args.resume)
    if args.device:
        options["device"] = args.device

    import torch

    device = args.device or ("cuda" if torch.cuda.is_available() else "cpu")
    print(f"RF-DETR {args.model} on {dataset_dir.name}: batch {batch} x accum {accum}, up to {args.epochs} epochs, "
          f"device {device}" + (f" ({torch.cuda.get_device_name(0)})" if device.startswith("cuda") else ""))
    if device == "cpu":
        print("WARNING: training on the CPU is very slow; install torch with CUDA (see README)")

    model = model_class(args.model)()
    start = time.perf_counter()
    model.train(**options)
    minutes = (time.perf_counter() - start) / 60
    if torch.cuda.is_available():
        print(f"peak GPU memory: {torch.cuda.max_memory_allocated() / 2**30:.1f} GB")
    print(f"done in {minutes:.1f} min -> {output_dir}")
    print(f"next: python eval.py {name}\n      python export.py {name} --install")


if __name__ == "__main__":
    main()
