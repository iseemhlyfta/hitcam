# Softcam in HitCam

[Softcam](https://github.com/tshino/softcam) v1.8.1 (commit `afb424c`), MIT — see [LICENSE](LICENSE).
`src/baseclasses` are Microsoft's DirectShow base classes from
[Windows-classic-samples](https://github.com/microsoft/Windows-classic-samples), also MIT.

Used for the "HitCam" camera on Windows 10, which has no Media Foundation virtual cameras.

Changes from upstream:
- `src/softcamcore/FrameBuffer.cpp`: the shared memory and mutex are named `HitCam DirectShow/...`, so HitCam never
  exchanges frames with other Softcam-based cameras.
- `src/softcamcore/FrameBuffer.cpp/.h`: `FrameBuffer::create` takes over an existing shared memory instead of failing.
  The section outlives its sender while any filter holds it (an app that created the camera but does not stream) and
  after a crash, so upstream refused to start the camera again. A new sender now takes the section over (resets the
  header like a fresh one and starts its heartbeat) when it is large enough and no live sender owns it: deactivated,
  or its heartbeat does not change for `TAKEOVER_WAIT` (0.2 s). A live sender still makes it fail. Filters holding
  the old stream need no change: one whose watchdog is still alive reads the new frames (after at most one 0.5 s
  wait, the frame counter restarts), one whose watchdog gave up releases the buffer on its next frame and reopens it.
- `src/softcamcore/DShowSoftcam.cpp/.h`: frames are stamped with the graph's stream time (live source) instead of a
  fixed per-frame interval that drifted from real time (renderers then held or dropped frames); frames prepared
  before the graph runs carry no timestamps. An unpaced camera advertises 30 fps instead of 60.
- Not copied: `src/softcam` (the DLL entry, replaced by `../../src/dshow/HitCamDShow.cpp` with HitCam's own CLSID
  and name), the Visual Studio projects, examples and tests.
