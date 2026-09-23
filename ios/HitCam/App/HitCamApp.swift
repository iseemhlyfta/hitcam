import SwiftUI

@main
struct HitCamApp: App {
    @StateObject private var session = StreamSession()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(session)
                .preferredColorScheme(.dark)
        }
    }
}

struct RootView: View {
    @EnvironmentObject private var session: StreamSession

    var body: some View {
        switch session.phase {
        case .idle:
            ConnectView(error: nil)
        case .failed(let message):
            ConnectView(error: message)
        case .connecting(let address):
            ProgressScreen(title: L10n.connecting, subtitle: address.display)
        case .reconnecting(let address):
            ProgressScreen(title: L10n.reconnecting, subtitle: address.display)
        case .pairing(let address, let attemptsLeft, let wrongPin):
            PinView(serverName: address.name ?? address.display, attemptsLeft: attemptsLeft, wrongPin: wrongPin)
        case .streaming(_, let serverName):
            StreamingView(serverName: serverName)
        }
    }
}

struct ProgressScreen: View {
    @EnvironmentObject private var session: StreamSession
    let title: String
    let subtitle: String

    var body: some View {
        VStack(spacing: 16) {
            ProgressView().controlSize(.large)
            Text(title).font(.headline)
            Text(subtitle).font(.subheadline).foregroundStyle(.secondary)
            Button(L10n.cancel, role: .cancel) { session.disconnect() }
                .padding(.top, 24)
        }
        .padding()
    }
}
