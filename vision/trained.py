"""Finding a training run, its checkpoint, its dataset and its classes (used by eval.py and export.py)."""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from common import DATA, RUNS


def dataset_classes(dataset_dir: Path) -> list[tuple[int, str]]:
    """(COCO category id, name) in the order of the model's output columns.

    RF-DETR sorts the categories of train/_annotations.coco.json by id (dropping Roboflow-style unannotated parent
    categories) and gives them contiguous label indices 0..N-1; label index = logits column in the ONNX model. The
    extra column N is the unused "no object" slot. We call rfdetr's own function so the mapping cannot drift."""
    from rfdetr.datasets.coco import annotated_category_ids, filter_parent_categories

    data = json.loads((dataset_dir / "train" / "_annotations.coco.json").read_text(encoding="utf-8"))
    kept = filter_parent_categories(data["categories"], annotated_category_ids(data))
    return [(int(c["id"]), c["name"]) for c in kept]


@dataclass
class Run:
    dir: Path
    checkpoint: Path
    dataset_dir: Path | None
    config: dict

    @property
    def name(self) -> str:
        return self.dir.name

    @property
    def class_names(self) -> list[str] | None:
        return self.config.get("class_names")

    @property
    def model_name(self) -> str:
        return self.config.get("model_config", {}).get("model_name", "")

    @property
    def resolution(self) -> int | None:
        return self.config.get("model_config", {}).get("resolution")

    def load_model(self):
        """The fine-tuned model in PyTorch (rfdetr picks Nano/Small from the checkpoint)."""
        from rfdetr.detr import RFDETR

        try:
            return RFDETR.from_checkpoint(str(self.checkpoint))
        except Exception:  # noqa: BLE001 - older rfdetr checkpoints need full unpickling; this is our own file
            return RFDETR.from_checkpoint(str(self.checkpoint), trust_checkpoint=True)


def load_run(run: str, dataset: str | None = None) -> Run:
    """`run` is a run name (vision/runs/<run>), a run folder or a .pth file."""
    path = Path(run)
    if not path.exists():
        path = RUNS / run
    if path.is_dir():
        checkpoint = path / "checkpoint_best_total.pth"
        if not checkpoint.exists():
            raise SystemExit(f"{checkpoint} not found: has training finished at least one epoch?")
        run_dir = path
    elif path.is_file():
        checkpoint, run_dir = path, path.parent
    else:
        raise SystemExit(f"no run {run!r}: expected vision/runs/{run}/checkpoint_best_total.pth")
    config_path = run_dir / "training_config.json"
    config = json.loads(config_path.read_text(encoding="utf-8")) if config_path.exists() else {}
    if dataset:
        dataset_dir = DATA / dataset
    else:
        recorded = config.get("train_config", {}).get("dataset_dir")
        dataset_dir = Path(recorded) if recorded else None
        if dataset_dir is not None and not dataset_dir.exists():
            # the run was copied from another machine or folder: try the same dataset name here
            dataset_dir = DATA / dataset_dir.name
    return Run(run_dir, checkpoint, dataset_dir, config)
