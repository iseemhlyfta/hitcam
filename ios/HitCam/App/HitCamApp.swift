import SwiftUI
import UIKit

@main
struct HitCamApp: App {
    @UIApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var session = StreamSession()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(session)
                .preferredColorScheme(.dark)
                .tint(HC.accent)
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
    @State private var spinning = false

    var body: some View {
        VStack(spacing: 18) {
            ZStack {
                LogoMark(size: 56)
                Circle().stroke(HC.border, lineWidth: 3)
                Circle()
                    .trim(from: 0, to: 0.28)
                    .stroke(HC.accent, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                    .rotationEffect(.degrees(spinning ? 360 : 0))
                    .animation(.linear(duration: 1).repeatForever(autoreverses: false), value: spinning)
            }
            .frame(width: 96, height: 96)
            VStack(spacing: 4) {
                Text(title).font(.headline).foregroundStyle(HC.text)
                Text(subtitle).font(.subheadline).monospacedDigit().foregroundStyle(HC.text2)
            }
            Button(L10n.cancel) { session.disconnect() }
                .buttonStyle(SecondaryButtonStyle(fullWidth: false))
                .padding(.top, 16)
        }
        .padding()
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(HC.ground.ignoresSafeArea())
        .onAppear { spinning = true }
    }
}
