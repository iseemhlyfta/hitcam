import AVFoundation
import SwiftUI

struct ConnectView: View {
    @EnvironmentObject private var session: StreamSession
    let error: String?

    @State private var addressText = LocalStore.recentServers.first?.display ?? ""
    @State private var showScanner = false
    @State private var inputError: String?
    @State private var cameraDenied = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("HitCam").font(.largeTitle.bold())
                        Text(L10n.appSubtitle).foregroundStyle(.secondary)
                    }
                    .listRowBackground(Color.clear)
                }

                Section {
                    TextField(L10n.addressPlaceholder, text: $addressText)
                        .keyboardType(.numbersAndPunctuation)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .submitLabel(.go)
                        .onSubmit(connectToTypedAddress)
                    Button(L10n.connect, action: connectToTypedAddress)
                        .disabled(addressText.trimmingCharacters(in: .whitespaces).isEmpty)
                    Button {
                        showScanner = true
                    } label: {
                        Label(L10n.scanQr, systemImage: "qrcode.viewfinder")
                    }
                } footer: {
                    if let message = inputError ?? error ?? (cameraDenied ? L10n.cameraDenied : nil) {
                        Text(message).foregroundStyle(.red)
                    }
                }

                let recent = LocalStore.recentServers
                if !recent.isEmpty {
                    Section(L10n.recent) {
                        ForEach(recent, id: \.self) { server in
                            Button {
                                start(server)
                            } label: {
                                VStack(alignment: .leading) {
                                    Text(server.name ?? server.display)
                                    if server.name != nil {
                                        Text(server.display).font(.caption).foregroundStyle(.secondary)
                                    }
                                }
                            }
                        }
                    }
                }
            }
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
    }

    private func connectToTypedAddress() {
        guard let address = ServerAddress.parse(addressText) else {
            inputError = L10n.invalidAddress
            return
        }
        // Reuse the id/name remembered from an earlier QR scan of the same PC, so its token is found.
        let known = LocalStore.recentServers.first { $0.host == address.host && $0.port == address.port }
        start(known ?? address)
    }

    private func start(_ address: ServerAddress) {
        inputError = nil
        AVCaptureDevice.requestAccess(for: .video) { granted in
            DispatchQueue.main.async {
                cameraDenied = !granted
                if granted { session.connect(to: address) }
            }
        }
    }
}
