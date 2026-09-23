import SwiftUI

struct PinView: View {
    @EnvironmentObject private var session: StreamSession
    let serverName: String
    let attemptsLeft: Int
    let wrongPin: Bool

    @State private var pin = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 20) {
            Image(systemName: "lock.shield").font(.system(size: 48)).foregroundStyle(.tint)
            Text(serverName).font(.headline)
            Text(L10n.enterPin).foregroundStyle(.secondary)

            TextField("000000", text: $pin)
                .keyboardType(.numberPad)
                .textContentType(.oneTimeCode)
                .font(.system(size: 40, weight: .bold, design: .monospaced))
                .multilineTextAlignment(.center)
                .focused($focused)
                .onChange(of: pin) { _, value in
                    let digits = String(value.filter(\.isNumber).prefix(6))
                    if digits != value { pin = digits }
                    if digits.count == 6 { submit() }
                }

            if wrongPin {
                Text(L10n.wrongPin(attemptsLeft)).foregroundStyle(.red)
            }

            HStack(spacing: 16) {
                Button(L10n.cancel, role: .cancel) { session.disconnect() }
                Button(L10n.pair, action: submit)
                    .buttonStyle(.borderedProminent)
                    .disabled(pin.count != 6)
            }
        }
        .padding(32)
        .onAppear { focused = true }
        .onChange(of: attemptsLeft) { _, _ in pin = "" }
    }

    private func submit() {
        guard pin.count == 6 else { return }
        session.submitPin(pin)
    }
}
