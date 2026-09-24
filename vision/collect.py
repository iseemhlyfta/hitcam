"""Grabs frames from the "HitCam" virtual camera (or any other camera) into vision/data/<dataset>/images.

HitCam for PC must be running with the phone connected and streaming, otherwise the virtual camera gives no frames
(or a placeholder picture). Close other programs that hold the camera exclusively if it does not open.

    python collect.py --list                     # cameras OpenCV can see, with their backends
    python collect.py mycups                     # camera "HitCam": Space saves a frame, A toggles auto, Q quits
    python collect.py mycups --every 2           # additionally save a frame every 2 seconds
    python collect.py mycups --every 1 --max 200 --no-preview     # no window: auto only, stop after 200 frames
    python collect.py mycups --camera "USB Camera" --backend dshow
    python collect.py mycups --index 1 --backend msmf

Tips: move the object and the phone between shots (angles, distances, backgrounds, lighting); auto mode skips frames
that barely differ from the previous saved one (--min-change).
"""

from __future__ import annotations

import argparse
import time
from datetime import datetime

import cv2
import numpy as np

from common import DATA

BACKENDS = {"msmf": cv2.CAP_MSMF, "dshow": cv2.CAP_DSHOW}


def list_cameras() -> list[tuple[str, int, str]]:
    """[(backend, index, name)] for Media Foundation and DirectShow; needs the cv2_enumerate_cameras package."""
    try:
        from cv2_enumerate_cameras import enumerate_cameras
    except ImportError:
        raise SystemExit("pip install cv2_enumerate_cameras  (or pass --index N --backend msmf|dshow)") from None
    found = []
    for backend, api in BACKENDS.items():
        for camera in enumerate_cameras(api):
            # the package may return the index already offset by the backend id; OpenCV wants the plain index
            index = camera.index - api if camera.index >= api else camera.index
            found.append((backend, index, camera.name))
    return found


def open_camera(args) -> tuple[cv2.VideoCapture, str]:
    if args.index is not None:
        candidates = [(b, args.index, f"#{args.index}") for b in ([args.backend] if args.backend != "auto"
                                                                  else ["msmf", "dshow"])]
    else:
        wanted = args.camera.lower()
        candidates = [c for c in list_cameras() if wanted in c[2].lower()
                      and (args.backend == "auto" or c[0] == args.backend)]
        if not candidates:
            names = sorted({c[2] for c in list_cameras()})
            raise SystemExit(f"no camera named like {args.camera!r}; cameras: {names}\n"
                             "Is HitCam for PC running with the phone streaming?")
    for backend, index, name in candidates:
        capture = cv2.VideoCapture(index, BACKENDS[backend])
        if not capture.isOpened():
            continue
        if args.size:
            w, h = (int(v) for v in args.size.lower().split("x"))
            capture.set(cv2.CAP_PROP_FRAME_WIDTH, w)
            capture.set(cv2.CAP_PROP_FRAME_HEIGHT, h)
        ok, frame = capture.read()
        if ok and frame is not None:
            return capture, f"{name} ({backend} #{index}, {frame.shape[1]}x{frame.shape[0]})"
        capture.release()
    raise SystemExit(f"could not read frames from {[c[2] for c in candidates]}: "
                     "is the phone streaming? is another program using the camera?")


def signature(frame: np.ndarray) -> np.ndarray:
    small = cv2.resize(cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY), (64, 36), interpolation=cv2.INTER_AREA)
    return small.astype(np.float32)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dataset", nargs="?", help="dataset name: frames go to vision/data/<dataset>/images")
    parser.add_argument("--list", action="store_true", help="list cameras and exit")
    parser.add_argument("--camera", default="HitCam", help="part of the camera name (default: HitCam)")
    parser.add_argument("--index", type=int, help="open the camera by index instead of by name")
    parser.add_argument("--backend", choices=["auto", "msmf", "dshow"], default="auto")
    parser.add_argument("--size", help="ask the camera for this resolution, e.g. 1920x1080")
    parser.add_argument("--every", type=float, default=0, help="auto-save a frame every N seconds (0 = keys only)")
    parser.add_argument("--min-change", type=float, default=4.0,
                        help="auto mode: skip frames whose mean pixel difference from the last saved one is below "
                             "this (0..255, default 4; 0 = save all)")
    parser.add_argument("--max", type=int, default=0, help="stop after saving this many frames (0 = no limit)")
    parser.add_argument("--no-preview", action="store_true", help="no window (requires --every)")
    parser.add_argument("--quality", type=int, default=95, help="JPEG quality (default 95)")
    args = parser.parse_args()

    if args.list:
        for backend, index, name in list_cameras():
            print(f"{backend:6} {index:2}  {name}")
        return
    if not args.dataset:
        parser.error("dataset name is required (or --list)")
    if args.no_preview and args.every <= 0:
        parser.error("--no-preview needs --every N")

    out = DATA / args.dataset / "images"
    out.mkdir(parents=True, exist_ok=True)
    capture, description = open_camera(args)
    print(f"camera: {description}\nsaving to {out} ({len(list(out.glob('*.jpg')))} frames already there)")
    if not args.no_preview:
        print("keys: Space = save frame, A = auto on/off, Q or Esc = quit")

    auto = args.every > 0
    saved, last_auto, last_signature, failures = 0, 0.0, None, 0
    try:
        while True:
            ok, frame = capture.read()
            if not ok or frame is None:
                failures += 1
                if failures > 50:
                    raise SystemExit("the camera stopped giving frames (phone disconnected?)")
                time.sleep(0.05)
                continue
            failures = 0
            now = time.monotonic()
            save = False
            if auto and now - last_auto >= args.every:
                last_auto = now
                current = signature(frame)
                change = 255.0 if last_signature is None else float(np.abs(current - last_signature).mean())
                save = change >= args.min_change
            key = -1
            if not args.no_preview:
                view = frame.copy()
                status = f"{saved} saved   auto {'ON every %gs' % args.every if auto else 'off'}   Space/A/Q"
                cv2.putText(view, status, (12, 32), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 0, 0), 4, cv2.LINE_AA)
                cv2.putText(view, status, (12, 32), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (255, 255, 255), 1, cv2.LINE_AA)
                scale = min(1.0, 1280 / view.shape[1])
                if scale < 1.0:
                    view = cv2.resize(view, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
                cv2.imshow("HitCam Vision - collect", view)
                key = cv2.waitKey(1) & 0xFF
                if key in (ord("q"), ord("Q"), 27):
                    break
                if key in (ord("a"), ord("A")):
                    auto = not auto
                    if auto and args.every <= 0:
                        args.every = 2.0
                if key == ord(" "):
                    save = True
            if save:
                name = f"{args.dataset}_{datetime.now():%Y%m%d_%H%M%S_%f}"[:-3] + ".jpg"
                cv2.imwrite(str(out / name), frame, [cv2.IMWRITE_JPEG_QUALITY, args.quality])
                last_signature = signature(frame)
                saved += 1
                print(f"  {name}")
                if args.max and saved >= args.max:
                    break
    except KeyboardInterrupt:
        pass
    finally:
        capture.release()
        cv2.destroyAllWindows()
    print(f"saved {saved} frames into {out}")


if __name__ == "__main__":
    main()
