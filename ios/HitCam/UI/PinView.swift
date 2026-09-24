import SwiftUI

struct PinView: View {
    @EnvironmentObject private var session: StreamSession
    let serverName: String
    let attemptsLeft: Int
    let wrongPin: Bool

    @State private var pin = ""
    /// Set once a PIN is sent; cleared when the PC answers, so one PIN never costs two attempts.
    @State private var awaitingResult = false
    @FocusState private var focused: Bool

    var body: some View {
        ScrollView {
            VStack(spacing: 20) {
                Image(systemName: "lock")
                    .font(.system(size: 26, weight: .medium))
                    .foregroundStyle(HC.accentSoftText)
                    .frame(width: 64, height: 64)
                    .background(HC.accentSoft, in: RoundedRectangle(cornerRadius: 20, style: .continuous))

                VStack(spacing: 6) {
                    Text(L10n.pairingRequest(serverName)).font(.footnote).foregroundStyle(HC.text2)
                    Text(L10n.pinTitle).font(.title2.weight(.semibold)).foregroundStyle(HC.text).multilineTextAlignment(.center)
                }

                pinBoxes

                if wrongPin {
                    NoticeBox(text: L10n.wrongPin(attemptsLeft))
                }

                Text(L10n.pinHint).font(.footnote).foregroundStyle(HC.text2).multilineTextAlignment(.center)

                HStack(spacing: 12) {
                    Button(L10n.cancel) { session.disconnect() }
                        .buttonStyle(SecondaryButtonStyle())
                    Button(L10n.pair, action: submit)
                        .buttonStyle(PrimaryButtonStyle())
                        .disabled(pin.count != 6 || awaitingResult)
                }
            }
            .padding(24)
            .card(padding: 0)
            .padding(20)
            .frame(maxWidth: 480)
            .frame(maxWidth: .infinity)
        }
        .background(HC.ground.ignoresSafeArea())
        .onAppear { focused = true }
        .onChange(of: attemptsLeft) { _, _ in
            pin = ""
            awaitingResult = false
        }
        .onChange(of: wrongPin) { _, _ in awaitingResult = false }
    }

    /// Six boxes like the PC shows; a hidden field takes the typing (and the one-time-code suggestion).
    private var pinBoxes: some View {
        ZStack {
            TextField("", text: $pin)
                .keyboardType(.numberPad)
                .textContentType(.oneTimeCode)
                .focused($focused)
                .opacity(0.02)
                .onChange(of: pin) { _, value in
                    let digits = String(value.filter(\.isNumber).prefix(6))
                    if digits != value { pin = digits }
                    if digits.count == 6 { submit() }
                }
                .accessibilityLabel(L10n.enterPin)

            HStack(spacing: 8) {
                ForEach(0..<6, id: \.self) { index in
                    if index == 3 { Spacer().frame(width: 6) }
                    box(index)
                }
            }
            .allowsHitTesting(false)
            .accessibilityHidden(true)
        }
        .contentShape(Rectangle())
        .onTapGesture { focused = true }
    }

    private func box(_ index: Int) -> some View {
        let digits = Array(pin)
        let isCurrent = focused && index == min(digits.count, 5) && digits.count < 6
        return Text(index < digits.count ? String(digits[index]) : "")
            .font(.system(size: 30, weight: .bold, design: .monospaced))
            .foregroundStyle(HC.text)
            .frame(width: 42, height: 56)
            .background(HC.surface2, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(isCurrent ? HC.accent : HC.border, lineWidth: isCurrent ? 2 : 1))
    }

    private func submit() {
        guard pin.count == 6, !awaitingResult else { return }
        awaitingResult = true
        session.submitPin(pin)
    }
}
