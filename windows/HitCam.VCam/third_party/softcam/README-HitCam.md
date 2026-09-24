# Softcam in HitCam

[Softcam](https://github.com/tshino/softcam) v1.8.1 (commit `afb424c`), MIT — see [LICENSE](LICENSE).
`src/baseclasses` are Microsoft's DirectShow base classes from
[Windows-classic-samples](https://github.com/microsoft/Windows-classic-samples), also MIT.

Used for the "HitCam" camera on Windows 10, which has no Media Foundation virtual cameras.

Changes from upstream:
- `src/softcamcore/FrameBuffer.cpp`: the shared memory and mutex are named `HitCam DirectShow/...`, so HitCam never
  exchanges frames with other Softcam-based cameras.
- Not copied: `src/softcam` (the DLL entry, replaced by `../../src/dshow/HitCamDShow.cpp` with HitCam's own CLSID
  and name), the Visual Studio projects, examples and tests.
