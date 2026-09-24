import AVFoundation
import SwiftUI

struct ConnectView: View {
    @EnvironmentObject private var session: StreamSession
    let error: String?

    @State private var addressText = LocalStore.recentServers.first?.display ?? ""
    @State private var showScanner = false
    @State private var inputError: String?
    @State private var cameraDenied = false
    @FocusState private var addressFocused: Bool

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                header
                connectCard
                if let message = inputError ?? error ?? (cameraDenied ? L10n.cameraDenied : nil) {
                    NoticeBox(text: message)
                }
                recentCard
            }
            .padding(20)
            .frame(maxWidth: 520)
            .frame(maxWidth: .infinity)
        }
        .scrollDismissesKeyboard(.interactively)
        .background(HC.ground.ignoresSafeArea())
        .sheet(isPresented: $showScanner) {
            QRScannerView { code in
                showScanner = false
                if let address = ServerAddress.parse(code), code.hasPrefix(ProtocolInfo.uriScheme) {
                    start(address)
                } else {
                    inputError = L10n.invalidAddress
                }
            }
            .ignoresSafeArea()
        }
    }

    private var header: some View {
        HStack(spacing: 14) {
            LogoMark(size: 56)
                .padding(6)
                .background(HC.surface, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(HC.border))
            VStack(alignment: .leading, spacing: 2) {
                Text("HitCam").font(.largeTitle.bold()).foregroundStyle(HC.text)
                Text(L10n.appSubtitle).font(.subheadline).foregroundStyle(HC.text2)
            }
        }
        .padding(.top, 12)
    }

    private var connectCard: some View {
        VStack(alignment: .leading, spacing: 14) {
            VStack(alignment: .leading, spacing: 4) {
                Text(L10n.connectTitle).font(.headline).foregroundStyle(HC.text)
                Text(L10n.connectHint).font(.footnote).foregroundStyle(HC.text2).fixedSize(horizontal: false, vertical: true)
            }

            Button {
                showScanner = true
            } label: {
                Label(L10n.scanQr, systemImage: "qrcode.viewfinder")
            }
            .buttonStyle(PrimaryButtonStyle())

            TextField("", text: $addressText, prompt: Text(L10n.addressPlaceholder).foregroundStyle(HC.text2.opacity(0.7)))
                .keyboardType(.numbersAndPunctuation)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .submitLabel(.go)
                .onSubmit(connectToTypedAddress)
                .focused($addressFocused)
                .font(.body.monospacedDigit())
                .foregroundStyle(HC.text)
                .padding(.horizontal, 12)
                .frame(minHeight: 44)
                .background(HC.surface2, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(addressFocused ? HC.accent : HC.border))

            Button(L10n.connect, action: connectToTypedAddress)
                .buttonStyle(SecondaryButtonStyle())
                .disabled(addressText.trimmingCharacters(in: .whitespaces).isEmpty)

            HStack(spacing: 14) {
                step(1, L10n.stepOpen)
                step(2, L10n.stepScan)
                step(3, L10n.stepPin)
            }
            .padding(.top, 2)
        }
        .card()
    }

    private func step(_ number: Int, _ text: String) -> some View {
        HStack(spacing: 6) {
            Text("\(number)")
                .font(.caption2.weight(.bold))
                .foregroundStyle(HC.accentSoftText)
                .frame(width: 20, height: 20)
                .background(HC.accentSoft, in: Circle())
            Text(text).font(.caption2).foregroundStyle(HC.text2).lineLimit(2).minimumScaleFactor(0.85)
        }
    }

    @ViewBuilder
    private var recentCard: some View {
        let recent = LocalStore.recentServers
        if !recent.isEmpty {
            VStack(alignment: .leading, spacing: 8) {
                Text(L10n.recent).font(.caption).foregroundStyle(HC.text2).padding(.leading, 4)
                VStack(spacing: 0) {
                    ForEach(Array(recent.enumerated()), id: \.element) { index, server in
                        if index > 0 { Divider().overlay(HC.border) }
                        Button {
                            start(server)
                        } label: {
                            HStack(spacing: 12) {
                                Image(systemName: "desktopcomputer")
                                    .font(.system(size: 16))
                                    .foregroundStyle(HC.text)
                                    .frame(width: 36, height: 36)
                                    .background(HC.surface2, in: RoundedRectangle(cornerRadius: 9, style: .continuous))
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(server.name ?? server.display).foregroundStyle(HC.text)
                                    if server.name != nil {
                                        Text(server.display).font(.caption).monospacedDigit().foregroundStyle(HC.text2)
                                    }
                                }
                                Spacer()
                                Image(systemName: "chevron.right").font(.footnote.weight(.semibold)).foregroundStyle(HC.text2)
                            }
                            .padding(.horizontal, 14)
                            .padding(.vertical, 10)
                            .contentShape(Rectangle())
                        }
                    }
                }
                .background(HC.surface, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(HC.border))
            }
        }
    }

    private func connectToTypedAddress() {
        guard let address = ServerAddress.parse(addressText) else {
            inputError = L10n.invalidAddress
            return
        }
        // Reuse the id/name remembered for the same PC, so its token is found. Recents hold only a trusted id
        // (from a QR code or a completed pairing), never one a host merely claimed in HelloAck.
        let known = LocalStore.recentServers.first { $0.host == address.host && $0.port == address.port }
        start(known ?? address)
    }

    private func start(_ address: ServerAddress) {
        inputError = nil
        addressFocused = false
        AVCaptureDevice.requestAccess(for: .video) { granted in
            DispatchQueue.main.async {
                cameraDenied = !granted
                if granted { session.connect(to: address) }
            }
        }
    }
}
