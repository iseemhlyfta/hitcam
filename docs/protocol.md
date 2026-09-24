# HitCam protocol v1

Transport: one TCP connection per session. The PC listens (default port **47800**), the phone (iPhone or Android) connects.
All multi-byte integers are **little-endian**. No external servers are ever involved.

## Framing

Every message is a fixed 16-byte header followed by `length` bytes of payload.

| Offset | Size | Field        | Notes                                                    |
|-------:|-----:|--------------|----------------------------------------------------------|
| 0      | 1    | `type`       | see table below                                          |
| 1      | 1    | `flags`      | per-type bit flags                                       |
| 2      | 2    | `reserved`   | must be 0                                                |
| 4      | 4    | `length`     | payload length, max 8 MiB (`8 * 1024 * 1024`)            |
| 8      | 8    | `timestamp`  | microseconds, sender's monotonic clock (0 if unused)     |

A receiver closes the connection on an unknown `type` **before the handshake**, on `length` above the limit,
or on a non-zero `reserved` field. After the handshake unknown types are skipped (forward compatibility).

The PC checks `length` against a per-state limit before reading the payload:

| When                                   | Messages                        | Max `length` |
|----------------------------------------|---------------------------------|-------------:|
| before the session is established      | `Hello`, `PairRequest`          | 4 KiB        |
| established session                    | `VideoFrame`                    | 8 MiB        |
| established session                    | everything else (JSON, unknown) | 64 KiB       |

## Message types

| Type | Name            | Direction  | Payload                                   |
|-----:|-----------------|------------|-------------------------------------------|
| 0x01 | Hello           | phone → pc | JSON `Hello`                              |
| 0x02 | HelloAck        | pc → phone | JSON `HelloAck`                           |
| 0x03 | PairRequest     | phone → pc | JSON `PairRequest`                        |
| 0x04 | PairResult      | pc → phone | JSON `PairResult`                         |
| 0x10 | StreamConfig    | phone → pc | JSON `StreamConfig`                       |
| 0x11 | VideoFrame      | phone → pc | H.264/HEVC Annex-B bytes; flag bit0 = keyframe |
| 0x12 | RequestKeyframe | pc → phone | empty                                     |
| 0x20 | Capabilities    | phone → pc | JSON `Capabilities`                       |
| 0x21 | CameraState     | phone → pc | JSON `CameraState`                        |
| 0x22 | Control         | pc → phone | JSON `Control`                            |
| 0x30 | Status          | phone → pc | JSON `Status`                             |
| 0x31 | Ping            | both       | empty; header timestamp = sender clock    |
| 0x32 | Pong            | both       | 8 bytes: echoed ping timestamp            |
| 0x3F | Bye             | both       | JSON `{ "reason": "..." }` (optional)     |

JSON payloads are UTF-8, camelCase, unknown fields are ignored. Optional fields are `Hello.model`,
`Hello.appVersion`, `Hello.token`, `PairResult.token`, `Bye.reason`, the fields added in app 0.2 (see below) and
every `Control` field; all other fields are required and must not be `null`. The PC treats a missing or `null` required field as a protocol error and closes
the connection. Arrays are bounded: `cameras` ≤ 16, `presets` ≤ 16, `presets[].fps` ≤ 8, `stabilizationModes` ≤ 8,
and must not contain `null`.

## Session flow

```
phone                                   pc
  | --- Hello {deviceId, token?} -------> |
  | <-- HelloAck {status} --------------- |   status = "accepted" | "pairingRequired" | "pairingLocked" | "busy" | "versionMismatch"
  |   (pairingRequired: PC shows 6-digit PIN)
  | --- PairRequest {pin} --------------> |
  | <-- PairResult {ok, token?, attemptsLeft} |
  | --- StreamConfig -------------------> |
  | --- Capabilities -------------------> |
  | --- CameraState --------------------> |
  | --- VideoFrame (keyframe first) ----> |
  | --- VideoFrame ... -----------------> |
  | <-- Control / RequestKeyframe ------- |
  | <-> Ping / Pong every 1 s ----------> |
```

* Only one phone streams or pairs at a time; a second one gets `HelloAck{status:"busy"}` and is closed.
  Exceptions: a phone with a valid token replaces a stale session of the same `deviceId`, and replaces a
  pairing that is still waiting for its PIN (the unpaired phone is disconnected), so a stranger typing PINs
  cannot keep a paired phone out.
* The PC accepts at most 8 connections at once and at most 2 unfinished handshakes (including pairing) per
  IP address; further connections are closed without a reply.
* Pairing: PIN is 6 digits, regenerated for every pairing window (connection); the user has 2 minutes to
  type it. Wrong PINs count across connections: a new connection, or one dropped mid-pairing, does not get
  the attempts back, and `attemptsLeft` reports what is left overall. After **5** wrong PINs (counted until
  5 minutes pass without a wrong PIN or lockout) the connection is closed and pairing is refused for 30 s;
  every further lockout doubles that time, up to 10 minutes. A correct PIN, or 5 quiet minutes after the
  last wrong PIN or lockout, resets the count and the lockout time. On success the PC issues a random
  256-bit `token` (hex) that the phone stores and presents in future `Hello`s. The PC stores only a
  SHA-256 hash of the token. A successful `PairResult` means the session is accepted (no second `HelloAck`).
  While locked out the PC answers `HelloAck{status:"pairingLocked"}` and closes.
* Clock sync: every `Ping` is answered with a `Pong` whose payload echoes the ping's header timestamp and whose
  own header timestamp is the replier's clock. The PC uses this to estimate capture-to-receive latency.
* Liveness: either side closes the connection if nothing was received for **5 s**.
* Startup order: once the session is accepted (`HelloAck{status:"accepted"}` or `PairResult{ok:true}`) the
  phone starts its camera and sends, in this order, `StreamConfig`, `Capabilities`, `CameraState`, then
  `VideoFrame`s starting with a keyframe (`Status` and `Ping` follow periodically). A later lens or format
  change sends a new `StreamConfig` before the updated `CameraState`.
* A `StreamConfig` is always followed by a keyframe. The PC sends `RequestKeyframe` after decoder errors.

## Low-latency rules

* The phone never queues more than ~2 frames for sending. When the socket is congested it drops frames
  until the next keyframe (and forces one).
* The PC decodes everything but hands only the **newest** decoded frame to the virtual camera.

## JSON schemas

```jsonc
// Hello
{ "protocolVersion": 1, "deviceId": "uuid", "deviceName": "iPhone Hitnes", "model": "iPhone15,2",
  "appVersion": "0.1.0", "token": "hex or null" }

// HelloAck
{ "protocolVersion": 1, "status": "accepted", "serverName": "DESKTOP-01", "serverId": "uuid" }

// PairRequest / PairResult
{ "pin": "123456" }
{ "ok": true, "token": "hex", "attemptsLeft": 4 }

// StreamConfig
{ "codec": "h264", "width": 1920, "height": 1080, "fps": 30, "bitrateKbps": 8000 }

// Capabilities
{ "cameras": [ { "id": "back-wide", "name": "Wide", "position": "back",
                 "minZoom": 1.0, "maxZoom": 10.0, "hasTorch": true, "supportsFocus": true,
                 "supportsWhiteBalance": true, "supportsExposureLock": true } ],
  "presets": [ { "width": 1280, "height": 720, "fps": [30, 60] }, { "width": 1920, "height": 1080, "fps": [30, 60] } ] }

// CameraState  (full snapshot, sent on every change)
{ "cameraId": "back-wide", "zoom": 1.0, "torch": false, "focusMode": "auto", "lensPosition": 0.5,
  "exposureBias": 0.0, "mirror": false, "rotation": 0, "width": 1920, "height": 1080, "fps": 30, "bitrateKbps": 8000,
  "whiteBalanceMode": "auto", "whiteBalanceTemperature": 5200, "whiteBalanceTint": 0, "exposureMode": "auto",
  "stabilization": "off", "stabilizationModes": ["off", "standard", "cinematic"] }

// Control (every field optional; only present fields are applied)
{ "cameraId": "front", "zoom": 2.0, "torch": true, "focusMode": "locked", "lensPosition": 0.3,
  "focusPoint": { "x": 0.5, "y": 0.5 }, "exposureBias": -0.5, "mirror": true, "rotation": 90,
  "width": 1280, "height": 720, "fps": 60, "bitrateKbps": 6000,
  "whiteBalanceMode": "locked", "whiteBalanceTemperature": 4200, "whiteBalanceTint": -10, "exposureMode": "locked",
  "stabilization": "standard" }

// Status
{ "battery": 0.82, "charging": true, "thermal": "nominal", "fps": 29.9, "bitrateKbps": 7900, "droppedFrames": 3 }
```

`rotation` ∈ {0, 90, 180, 270}; `focusMode` ∈ {"auto", "continuous", "locked"}; `thermal` ∈
{"nominal", "fair", "serious", "critical"}.

Added in app 0.2 (optional, absent from older apps: hide the controls then): `whiteBalanceMode` and
`exposureMode` ∈ {"auto", "locked"}; a `whiteBalanceTemperature` (K, 2000–10000) or `whiteBalanceTint` (−150…150)
without a mode means "locked at this value", the other one keeps its current value. A locked exposure ignores
`exposureBias`. `stabilization` ∈ {"off", "standard", "cinematic"}, limited to `stabilizationModes`, which the
phone recomputes for every format; stabilization adds latency. Switching lens or format resets white balance
and exposure to "auto".

`Hello.model` is a free-form device model string for display and diagnostics only, e.g. `"iPhone15,2"` or
`"Xiaomi 23049PCD8G"`; the PC must not parse it. `whiteBalanceTint` units are device-specific: the value is on a
−150…150 green–magenta scale (the iPhone uses it as-is, Android maps it to its own white-balance gains), so the
same number may look slightly different on different phones. Android offers only `"off"` and `"standard"` in
`stabilizationModes`.
