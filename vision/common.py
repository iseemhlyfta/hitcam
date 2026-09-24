"""Shared helpers for the HitCam Vision scripts: paths, model metadata (labels.json), ONNX pre/post-processing,
box matching and drawing.

The ONNX contract (the same one the HitCam PC app implements):
  input  "input": float32 [1, 3, H, W], RGB, image stretched to W x H, scaled to 0..1, then (x - mean) / std
  output "dets":   float32 [1, Q, 4]  boxes as (cx, cy, w, h), normalised to 0..1 of the input image
  output "labels": float32 [1, Q, C]  class logits; score = sigmoid(logit); column index = class id in labels.json
"""

from __future__ import annotations

import json
import os
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
DATA = HERE / "data"
RUNS = HERE / "runs"
OUTPUT = HERE / "output"

MEAN = [0.485, 0.456, 0.406]
STD = [0.229, 0.224, 0.225]

# Only these two variants are wired up: both are Apache 2.0, small enough for 8 GB of VRAM and fast on DirectML.
MODELS = {
    "nano": ("RFDETRNano", 384),
    "small": ("RFDETRSmall", 512),
}

IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".bmp", ".webp"}


def quiet() -> None:
    """Hides the harmless noise torch/rfdetr print on Windows (no Triton, ONNX tracer and deprecation warnings)."""
    import logging
    import warnings

    logging.getLogger("torch.utils.flop_counter").setLevel(logging.ERROR)
    for category in (FutureWarning, DeprecationWarning):
        warnings.filterwarnings("ignore", category=category)
    warnings.filterwarnings("ignore", message=".*Triton.*")
    warnings.filterwarnings("ignore", message=".*trace might not generalize.*")
    warnings.filterwarnings("ignore", message=".*not optimized for inference.*")


def model_class(model: str):
    """Returns the rfdetr class for "nano" / "small" (imported lazily: rfdetr pulls in torch)."""
    import rfdetr

    return getattr(rfdetr, MODELS[model][0])


def hitcam_models_dir() -> Path:
    """Folder the HitCam PC app scans for <stem>.onnx + <stem>.labels.json."""
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(base) / "HitCam" / "models"


# ----------------------------------------------------------------------------------------------------------------
# labels.json


def labels_metadata(name: str, size: int, classes: dict[int, str]) -> dict:
    """Builds the <stem>.labels.json the HitCam app reads. `classes` maps a logits column to a class name."""
    return {
        "name": name,
        "format": "rfdetr",
        "input": [size, size],
        "mean": MEAN,
        "std": STD,
        "resize": "stretch",
        "outputs": {"boxes": "dets", "logits": "labels"},
        "classes": {str(k): v for k, v in sorted(classes.items())},
        "license": "Apache-2.0",
    }


def write_labels(path: Path, metadata: dict) -> None:
    path.write_text(json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def read_labels(onnx_path: Path) -> dict | None:
    """Reads <stem>.labels.json next to <stem>.onnx, or None."""
    path = onnx_path.parent / (onnx_path.stem + ".labels.json")
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


# ----------------------------------------------------------------------------------------------------------------
# Detections


@dataclass
class Detection:
    class_id: int
    score: float
    box: tuple[float, float, float, float]  # x1, y1, x2, y2 in source image pixels

    def __str__(self) -> str:
        x1, y1, x2, y2 = (round(v) for v in self.box)
        return f"{self.class_id}:{self.score:.2f}@[{x1},{y1},{x2},{y2}]"


def iou(a, b) -> float:
    ix1, iy1 = max(a[0], b[0]), max(a[1], b[1])
    ix2, iy2 = min(a[2], b[2]), min(a[3], b[3])
    inter = max(0.0, ix2 - ix1) * max(0.0, iy2 - iy1)
    union = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / union if union > 0 else 0.0


def match(predicted: list, truth: list, threshold: float = 0.5, same_class: bool = True):
    """Greedy one-to-one matching, highest score first. Items need .box and .class_id; predictions also .score.

    Returns (pairs [(pred_index, truth_index, iou)], unmatched predictions, unmatched truths)."""
    order = sorted(range(len(predicted)), key=lambda i: -getattr(predicted[i], "score", 1.0))
    free = set(range(len(truth)))
    pairs, lonely = [], []
    for i in order:
        best, best_iou = None, threshold
        for j in free:
            if same_class and predicted[i].class_id != truth[j].class_id:
                continue
            value = iou(predicted[i].box, truth[j].box)
            if value >= best_iou:
                best, best_iou = j, value
        if best is None:
            lonely.append(i)
        else:
            free.discard(best)
            pairs.append((i, best, best_iou))
    return pairs, lonely, sorted(free)


def from_supervision(detections) -> list[Detection]:
    """rfdetr's predict() returns supervision.Detections; convert to plain Detection objects."""
    return [Detection(int(c), float(s), tuple(float(v) for v in b))
            for c, s, b in zip(detections.class_id, detections.confidence, detections.xyxy)]


# ----------------------------------------------------------------------------------------------------------------
# ONNX Runtime


def preprocess(image: Image.Image, size: int, mean=MEAN, std=STD) -> np.ndarray:
    """RGB PIL image -> float32 [1, 3, size, size], stretched (no letterbox), normalised with ImageNet mean/std.

    Plain bilinear without antialiasing (cv2.INTER_LINEAR), exactly like rfdetr's predict(). PIL's BILINEAR
    antialiases when shrinking, which shifts scores by up to ~0.1 on small objects."""
    import cv2

    rgb = np.asarray(image.convert("RGB"))
    resized = cv2.resize(rgb, (size, size), interpolation=cv2.INTER_LINEAR)
    x = resized.astype(np.float32) / 255.0
    x = (x - np.asarray(mean, np.float32)) / np.asarray(std, np.float32)
    return np.ascontiguousarray(x.transpose(2, 0, 1)[None])


def decode(boxes: np.ndarray, logits: np.ndarray, width: int, height: int, threshold: float = 0.5,
           classes: dict | None = None, top_k: int = 300) -> list[Detection]:
    """Same post-processing as rfdetr: sigmoid, top-k over all (query, class) pairs, keep score >= threshold.

    No NMS: DETR-style models are trained to predict each object once. Columns without a name in `classes`
    (for example the unused background column of a fine-tuned model) are skipped."""
    scores = 1.0 / (1.0 + np.exp(-logits.astype(np.float32)))  # (Q, C)
    if classes is not None:
        known = np.zeros(scores.shape[1], bool)
        for key in classes:
            if int(key) < scores.shape[1]:
                known[int(key)] = True
        scores = np.where(known[None, :], scores, 0.0)
    flat = scores.ravel()
    k = min(top_k, flat.size)
    top = np.argpartition(-flat, k - 1)[:k]
    top = top[np.argsort(-flat[top])]
    result = []
    for index in top:
        score = float(flat[index])
        if score < threshold:
            break
        q, c = divmod(int(index), scores.shape[1])
        cx, cy, w, h = boxes[q].astype(np.float64)
        result.append(Detection(c, score, ((cx - w / 2) * width, (cy - h / 2) * height,
                                           (cx + w / 2) * width, (cy + h / 2) * height)))
    return result


class OnnxDetector:
    """RF-DETR ONNX model + its labels.json on one ONNX Runtime execution provider."""

    def __init__(self, onnx_path: Path, provider: str = "DmlExecutionProvider"):
        import onnxruntime as ort

        self.path = Path(onnx_path)
        self.meta = read_labels(self.path) or {}
        options = ort.SessionOptions()
        options.log_severity_level = 3
        providers = [provider] if provider == "CPUExecutionProvider" else [provider, "CPUExecutionProvider"]
        self.session = ort.InferenceSession(str(self.path), options, providers=providers)
        self.provider = self.session.get_providers()[0]
        inp = self.session.get_inputs()[0]
        self.input_name = inp.name
        if inp.type != "tensor(float)":
            raise RuntimeError(f"{self.path.name}: input is {inp.type}; HitCam needs an FP32 model")
        self.size = int(self.meta.get("input", [inp.shape[3]])[0])
        outputs = self.meta.get("outputs", {"boxes": "dets", "logits": "labels"})
        names = [o.name for o in self.session.get_outputs()]
        missing = [n for n in outputs.values() if n not in names]
        if missing:
            raise RuntimeError(f"{self.path.name}: outputs {names}, labels.json expects {list(outputs.values())}")
        self.boxes_name, self.logits_name = outputs["boxes"], outputs["logits"]
        self.classes = self.meta.get("classes")
        self.mean, self.std = self.meta.get("mean", MEAN), self.meta.get("std", STD)

    def raw(self, image: Image.Image):
        x = preprocess(image, self.size, self.mean, self.std)
        boxes, logits = self.session.run([self.boxes_name, self.logits_name], {self.input_name: x})
        return boxes[0], logits[0]

    def detect(self, image: Image.Image, threshold: float = 0.5) -> list[Detection]:
        boxes, logits = self.raw(image)
        return decode(boxes, logits, image.width, image.height, threshold, self.classes)

    def benchmark(self, image: Image.Image, runs: int = 30, warmup: int = 3) -> float:
        x = preprocess(image, self.size, self.mean, self.std)
        feed = {self.input_name: x}
        for _ in range(warmup):
            self.session.run(None, feed)
        start = time.perf_counter()
        for _ in range(runs):
            self.session.run(None, feed)
        return (time.perf_counter() - start) * 1000 / runs

    def name_of(self, class_id: int) -> str:
        return (self.classes or {}).get(str(class_id), str(class_id))


# ----------------------------------------------------------------------------------------------------------------
# Drawing

# no greens: eval.py draws the ground truth in green
PALETTE = [(230, 25, 75), (0, 130, 200), (245, 130, 48), (145, 30, 180), (240, 50, 230), (0, 90, 160),
           (170, 110, 40), (128, 0, 0), (255, 200, 0), (90, 90, 90), (200, 0, 130), (0, 60, 120)]


def _font(size: int = 16):
    for name in ("segoeui.ttf", "arial.ttf", "DejaVuSans.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def draw(image: Image.Image, detections, names: dict | None = None, color=None, width: int = 3,
         label_below: bool = False) -> Image.Image:
    """Draws boxes with "name score" captions. `color` None = colour by class."""
    out = image.convert("RGB").copy()
    canvas = ImageDraw.Draw(out)
    font = _font(max(12, out.width // 60))
    for d in detections:
        rgb = color or PALETTE[d.class_id % len(PALETTE)]
        x1, y1, x2, y2 = d.box
        canvas.rectangle([x1, y1, x2, y2], outline=rgb, width=width)
        text = (names or {}).get(str(d.class_id), str(d.class_id))
        if getattr(d, "score", None) is not None and d.score < 1.0:
            text += f" {d.score:.2f}"
        tw, th = canvas.textbbox((0, 0), text, font=font)[2:]
        ty = y2 if label_below else max(0, y1 - th - 4)
        canvas.rectangle([x1, ty, x1 + tw + 6, ty + th + 4], fill=rgb)
        canvas.text((x1 + 3, ty + 1), text, fill=(255, 255, 255), font=font)
    return out
