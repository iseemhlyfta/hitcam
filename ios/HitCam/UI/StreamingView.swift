import SwiftUI

struct StreamingView: View {
    @EnvironmentObject private var session: StreamSession
    @Environment(\.scenePhase) private var scenePhase
    let serverName: String

    @State private var dimmed = false
    @State private var savedBrightness: CGFloat = UIScreen.main.brightness
    @State private var showControls = true

    private var state: CameraState { session.cameraState }
    private var camera: CameraInfo? { session.capabilities.cameras.first { $0.id == state.cameraId } }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            CameraPreview(camera: session.camera) { point in
                session.apply(Control(focusPoint: NormalizedPoint(x: point.x, y: point.y)))
            }
            .ignoresSafeArea()

            // Chrome over the picture. Each layer fills the screen and pins its content to a corner,
            // so hiding the settings panel never moves the top bar.
            VStack(alignment: .leading, spacing: 0) {
                topBar
                Spacer(minLength: 0)
                Pill(text: L10n.tapToFocus)
            }
            .padding(12)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)

            if showControls {
                controlsPanel
                    .frame(width: 350)
                    .padding(.top, 68)
                    .padding([.trailing, .bottom], 12)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topTrailing)
                    .transition(.move(edge: .trailing).combined(with: .opacity))
            }

            if dimmed {
                Color.black
                    .ignoresSafeArea()
                    .overlay(Text(L10n.tapToWake).foregroundStyle(.gray).multilineTextAlignment(.center).padding())
                    .onTapGesture { setDimmed(false) }
            }
        }
        .statusBarHidden(true)
        .onAppear { OrientationLock.set(.landscape) }
        .onChange(of: scenePhase) { _, phase in
            // The brightness outlives the app: restore it before leaving the foreground.
            if phase != .active { setDimmed(false) }
        }
        .onDisappear {
            setDimmed(false)
            OrientationLock.set(.allButUpsideDown)
        }
    }

    // MARK: Top bar

    private var topBar: some View {
        HStack(spacing: 8) {
            Pill(text: "LIVE · \(state.height)p · \(state.fps) fps", dot: HC.live)
            Pill(text: serverName, dot: HC.ok)
                .lineLimit(1)
            Pill(text: String(format: "%.0f fps · %.1f Mbit/s", session.sentFps, Double(session.sentKbps) / 1000), foreground: HC.text2)
            Spacer(minLength: 8)
            OverlayIconButton(systemImage: "slider.horizontal.3", label: L10n.camera, active: showControls) {
                withAnimation(.spring(response: 0.35, dampingFraction: 0.9)) { showControls.toggle() }
            }
            OverlayIconButton(systemImage: "moon.fill", label: L10n.dim) { setDimmed(true) }
            OverlayIconButton(systemImage: "xmark", label: L10n.disconnect, tint: HC.danger) { session.disconnect() }
        }
    }

    // MARK: Settings panel (same layout as the PC's "Camera" panel)

    private var controlsPanel: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(L10n.camera).font(.headline).foregroundStyle(HC.text)
                    Text(L10n.appliesInstantly).font(.caption).foregroundStyle(HC.text2)
                }

                if session.capabilities.cameras.count > 1 {
                    VStack(alignment: .leading, spacing: 6) {
                        FieldLabel(L10n.lens)
                        Segmented(
                            options: session.capabilities.cameras.map { (id: $0.id, label: L10n.lensName($0.id, fallback: $0.name)) },
                            selection: state.cameraId
                        ) { session.apply(Control(cameraId: $0)) }
                    }
                }

                VStack(alignment: .leading, spacing: 6) {
                    FieldLabel(L10n.quality)
                    qualityMenu
                }

                if let camera, camera.maxZoom > camera.minZoom {
                    SliderRow(
                        title: L10n.zoom,
                        valueText: String(format: "%.1f×", state.zoom),
                        value: Binding(get: { state.zoom }, set: { session.apply(Control(zoom: $0)) }),
                        range: camera.minZoom...camera.maxZoom)
                }

                SliderRow(
                    title: L10n.exposure,
                    valueText: String(format: "%+.1f EV", state.exposureBias),
                    value: Binding(get: { state.exposureBias }, set: { session.apply(Control(exposureBias: $0)) }),
                    range: -2...2)

                if camera?.supportsFocus != false {
                    SliderRow(
                        title: L10n.focus,
                        valueText: String(format: "%.2f", state.lensPosition),
                        value: Binding(get: { state.lensPosition }, set: { session.apply(Control(focusMode: "locked", lensPosition: $0)) }),
                        range: 0...1
                    ) {
                        ChipButton(title: L10n.auto, isOn: state.focusMode == "continuous") {
                            session.apply(Control(focusMode: "continuous"))
                        }
                    }
                }

                HStack(spacing: 8) {
                    if camera?.hasTorch == true {
                        ToggleTile(systemImage: state.torch ? "flashlight.on.fill" : "flashlight.off.fill", label: L10n.torch, isOn: state.torch) {
                            session.apply(Control(torch: !state.torch))
                        }
                    }
                    ToggleTile(systemImage: "arrow.left.and.right.righttriangle.left.righttriangle.right", label: L10n.mirror, isOn: state.mirror) {
                        session.apply(Control(mirror: !state.mirror))
                    }
                    ToggleTile(systemImage: "rotate.right", label: "\(state.rotation)°") {
                        session.apply(Control(rotation: (state.rotation + 90) % 360))
                    }
                    .accessibilityLabel(L10n.rotate)
                }

                Text(L10n.keepAppOpen).font(.caption2).foregroundStyle(HC.text2).fixedSize(horizontal: false, vertical: true)
            }
            .padding(16)
        }
        .scrollIndicators(.hidden)
        .background(HC.surface.opacity(0.95), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(HC.border))
    }

    private var qualityMenu: some View {
        Menu {
            ForEach(qualityOptions, id: \.label) { option in
                Button {
                    session.apply(Control(width: option.width, height: option.height, fps: option.fps, bitrateKbps: option.bitrate))
                } label: {
                    if option.width == state.width, option.fps == state.fps {
                        Label(option.label, systemImage: "checkmark")
                    } else {
                        Text(option.label)
                    }
                }
            }
        } label: {
            HStack {
                Text("\(state.height)p · \(state.fps) fps").font(.subheadline).foregroundStyle(HC.text)
                Spacer()
                Image(systemName: "chevron.down").font(.caption.weight(.semibold)).foregroundStyle(HC.text2)
            }
            .padding(.horizontal, 12)
            .frame(minHeight: 40)
            .background(HC.surface2, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(HC.border))
        }
    }

    private var qualityOptions: [(label: String, width: Int, height: Int, fps: Int, bitrate: Int)] {
        [
            ("720p · 30 fps", 1280, 720, 30, 4000),
            ("720p · 60 fps", 1280, 720, 60, 6000),
            ("1080p · 30 fps", 1920, 1080, 30, 8000),
            ("1080p · 60 fps", 1920, 1080, 60, 12000),
        ]
    }

    private func setDimmed(_ on: Bool) {
        guard on != dimmed else { return }
        if on {
            savedBrightness = UIScreen.main.brightness
            UIScreen.main.brightness = 0
        } else {
            UIScreen.main.brightness = savedBrightness
        }
        dimmed = on
    }
}
