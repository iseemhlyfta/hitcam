import SwiftUI

struct StreamingView: View {
    @EnvironmentObject private var session: StreamSession
    let serverName: String

    @State private var dimmed = false
    @State private var savedBrightness: CGFloat = UIScreen.main.brightness
    @State private var showControls = true

    private var state: CameraState { session.cameraState }

    var body: some View {
        ZStack {
            Color.black.ignoresSafeArea()

            CameraPreview(camera: session.camera) { point in
                session.apply(Control(focusPoint: NormalizedPoint(x: point.x, y: point.y)))
            }
            .ignoresSafeArea()

            // The screen is landscape-only, so the controls sit in a column on the right instead of covering the preview.
            VStack(spacing: 0) {
                topBar
                HStack(spacing: 0) {
                    Spacer()
                    if showControls {
                        ScrollView { controls }
                            .frame(width: 380)
                            .background(.ultraThinMaterial)
                    }
                }
            }

            if dimmed {
                Color.black
                    .ignoresSafeArea()
                    .overlay(Text(L10n.tapToWake).foregroundStyle(.gray).multilineTextAlignment(.center).padding())
                    .onTapGesture { setDimmed(false) }
            }
        }
        .statusBarHidden(dimmed)
        .onAppear { OrientationLock.set(.landscape) }
        .onDisappear {
            setDimmed(false)
            OrientationLock.set(.allButUpsideDown)
        }
    }

    private var topBar: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Label(serverName, systemImage: "dot.radiowaves.left.and.right").font(.subheadline.bold())
                Text("\(Int(session.sentFps)) fps · \(String(format: "%.1f", Double(session.sentKbps) / 1000)) Mbit/s · \(state.width)×\(state.height)")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            Spacer()
            Button { withAnimation { showControls.toggle() } } label: {
                Image(systemName: showControls ? "slider.horizontal.below.rectangle" : "slider.horizontal.3")
            }
            Button { setDimmed(true) } label: { Image(systemName: "moon.fill") }
                .accessibilityLabel(L10n.dim)
            Button(role: .destructive) { session.disconnect() } label: { Image(systemName: "xmark.circle.fill") }
                .accessibilityLabel(L10n.disconnect)
        }
        .font(.title3)
        .padding(12)
        .background(.ultraThinMaterial)
    }

    private var controls: some View {
        VStack(spacing: 12) {
            // Camera selection
            Picker("", selection: Binding(get: { state.cameraId }, set: { session.apply(Control(cameraId: $0)) })) {
                ForEach(session.capabilities.cameras, id: \.id) { camera in
                    Text(camera.name).tag(camera.id)
                }
            }
            .pickerStyle(.segmented)

            if let camera = session.capabilities.cameras.first(where: { $0.id == state.cameraId }), camera.maxZoom > camera.minZoom {
                HStack {
                    Text(L10n.zoom).font(.caption)
                    Slider(value: Binding(get: { state.zoom }, set: { session.apply(Control(zoom: $0)) }),
                           in: camera.minZoom...camera.maxZoom)
                    Text(String(format: "%.1f×", state.zoom)).font(.caption.monospacedDigit()).frame(width: 44)
                }
            }

            HStack {
                Text(L10n.exposure).font(.caption)
                Slider(value: Binding(get: { state.exposureBias }, set: { session.apply(Control(exposureBias: $0)) }), in: -2...2)
            }

            HStack {
                Text(L10n.focus).font(.caption)
                Slider(value: Binding(get: { state.lensPosition }, set: { session.apply(Control(focusMode: "locked", lensPosition: $0)) }), in: 0...1)
                Button(L10n.autoFocus) { session.apply(Control(focusMode: "continuous")) }
                    .font(.caption)
                    .buttonStyle(.bordered)
                    .tint(state.focusMode == "continuous" ? .accentColor : .gray)
            }

            HStack(spacing: 10) {
                Menu {
                    ForEach(qualityOptions, id: \.label) { option in
                        Button(option.label) {
                            session.apply(Control(width: option.width, height: option.height, fps: option.fps, bitrateKbps: option.bitrate))
                        }
                    }
                } label: {
                    Label("\(state.height)p\(state.fps)", systemImage: "dial.medium")
                }
                .buttonStyle(.bordered)

                Toggle(isOn: Binding(get: { state.mirror }, set: { session.apply(Control(mirror: $0)) })) {
                    Image(systemName: "arrow.left.and.right.righttriangle.left.righttriangle.right")
                }
                .toggleStyle(.button)
                .accessibilityLabel(L10n.mirror)

                Button { session.apply(Control(rotation: (state.rotation + 90) % 360)) } label: {
                    Label("\(state.rotation)°", systemImage: "rotate.right")
                }
                .buttonStyle(.bordered)

                if session.capabilities.cameras.first(where: { $0.id == state.cameraId })?.hasTorch == true {
                    Toggle(isOn: Binding(get: { state.torch }, set: { session.apply(Control(torch: $0)) })) {
                        Image(systemName: state.torch ? "flashlight.on.fill" : "flashlight.off.fill")
                    }
                    .toggleStyle(.button)
                    .accessibilityLabel(L10n.torch)
                }
            }

            Text(L10n.keepAppOpen).font(.caption2).foregroundStyle(.secondary)
        }
        .padding(12)
    }

    private var qualityOptions: [(label: String, width: Int, height: Int, fps: Int, bitrate: Int)] {
        [
            ("720p 30", 1280, 720, 30, 4000),
            ("720p 60", 1280, 720, 60, 6000),
            ("1080p 30", 1920, 1080, 30, 8000),
            ("1080p 60", 1920, 1080, 60, 12000),
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
