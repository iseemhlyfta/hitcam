import Foundation

/// Tiny RU/EN string table (Russian when the phone language is Russian).
enum L10n {
    private static let ru = Locale.preferredLanguages.first?.hasPrefix("ru") ?? false

    static var appSubtitle: String { ru ? "Камера iPhone как веб-камера для ПК" : "Your iPhone camera as a PC webcam" }
    static var addressPlaceholder: String { ru ? "IP-адрес ПК, например 192.168.1.5" : "PC IP address, e.g. 192.168.1.5" }
    static var connect: String { ru ? "Подключиться" : "Connect" }
    static var scanQr: String { ru ? "Сканировать QR-код" : "Scan QR code" }
    static var recent: String { ru ? "Недавние" : "Recent" }
    static var invalidAddress: String { ru ? "Неверный адрес" : "Invalid address" }
    static var connecting: String { ru ? "Подключение…" : "Connecting…" }
    static var reconnecting: String { ru ? "Связь потеряна, переподключаюсь…" : "Connection lost, reconnecting…" }
    static var cancel: String { ru ? "Отмена" : "Cancel" }
    static var enterPin: String { ru ? "Введите PIN с экрана ПК" : "Enter the PIN shown on the PC" }
    static func wrongPin(_ left: Int) -> String { ru ? "Неверный PIN, осталось попыток: \(left)" : "Wrong PIN, \(left) attempts left" }
    static var pair: String { ru ? "Сопрячь" : "Pair" }
    static var disconnect: String { ru ? "Отключить" : "Disconnect" }
    static var dim: String { ru ? "Затемнить" : "Dim" }
    static var tapToWake: String { ru ? "Трансляция идёт. Коснитесь, чтобы вернуть экран." : "Streaming. Tap to wake the screen." }
    static var mirror: String { ru ? "Зеркало" : "Mirror" }
    static var torch: String { ru ? "Фонарик" : "Torch" }
    static var rotate: String { ru ? "Поворот" : "Rotate" }
    static var quality: String { ru ? "Качество" : "Quality" }
    static var zoom: String { ru ? "Зум" : "Zoom" }
    static var exposure: String { ru ? "Экспозиция" : "Exposure" }
    static var autoFocus: String { ru ? "Автофокус" : "Auto focus" }
    static var focus: String { ru ? "Фокус" : "Focus" }
    static var keepAppOpen: String {
        ru ? "Не закрывайте приложение: iOS не даёт снимать камерой в фоне." : "Keep the app open: iOS does not allow the camera in the background."
    }
    static var busy: String { ru ? "К этому ПК уже подключён другой телефон" : "Another phone is already connected to this PC" }
    static var pairingLocked: String {
        ru ? "Слишком много неверных PIN. Попробуйте через 30 секунд." : "Too many wrong PINs. Try again in 30 seconds."
    }
    static var versionMismatch: String { ru ? "Версии HitCam на ПК и iPhone несовместимы" : "HitCam versions on the PC and iPhone don't match" }
    static var protocolError: String { ru ? "ПК ответил что-то непонятное" : "Unexpected response from the PC" }
    static var otherPc: String {
        ru ? "Это другой компьютер: отсканируйте его QR-код заново" : "This is a different PC: scan its QR code again"
    }
    static var closedByPc: String { ru ? "ПК завершил соединение" : "The PC closed the connection" }
    static var connectionClosed: String { ru ? "Соединение закрыто" : "Connection closed" }
    static func connectionFailed(_ reason: String) -> String {
        ru ? "Не удалось подключиться: \(reason). Проверьте, что ПК и iPhone в одной Wi-Fi сети и HitCam на ПК запущен."
           : "Could not connect: \(reason). Make sure the PC and iPhone are on the same Wi-Fi and HitCam is running on the PC."
    }
    static func cameraFailed(_ reason: String) -> String { ru ? "Ошибка камеры: \(reason)" : "Camera error: \(reason)" }
    static var connectTitle: String { ru ? "Подключение к ПК" : "Connect to your PC" }
    static var connectHint: String {
        ru ? "Запустите HitCam на ПК в той же Wi-Fi сети и отсканируйте QR-код с его экрана или введите адрес."
           : "Start HitCam on a PC in the same Wi-Fi network, then scan the QR code on its screen or type its address."
    }
    static var stepOpen: String { ru ? "Откройте HitCam на ПК" : "Open HitCam on the PC" }
    static var stepScan: String { ru ? "Сканируйте код" : "Scan the code" }
    static var stepPin: String { ru ? "Введите PIN" : "Enter the PIN" }
    static func pairingRequest(_ server: String) -> String { ru ? "Сопряжение с «\(server)»" : "Pairing with “\(server)”" }
    static var pinTitle: String { ru ? "Введите код с экрана ПК" : "Enter the code shown on the PC" }
    static var pinHint: String {
        ru ? "После сопряжения iPhone будет подключаться к этому ПК без кода." : "Once paired, this iPhone connects to the PC without a code."
    }
    static var camera: String { ru ? "Камера" : "Camera" }
    static var appliesInstantly: String { ru ? "Настройки сразу применяются к трансляции" : "Changes apply to the stream right away" }
    static var lens: String { ru ? "Объектив" : "Lens" }
    static var tapToFocus: String { ru ? "Нажмите на картинку, чтобы сфокусироваться" : "Tap the picture to focus" }
    static var auto: String { ru ? "Авто" : "Auto" }
    static func lensName(_ id: String, fallback: String) -> String {
        switch id {
        case "back-ultrawide": return ru ? "Ультра" : "Ultra"
        case "back-wide": return ru ? "Широкий" : "Wide"
        case "back-tele": return ru ? "Теле" : "Tele"
        case "front": return ru ? "Фронт" : "Front"
        default: return fallback
        }
    }
    static var cameraDenied: String {
        ru ? "Нет доступа к камере. Разрешите его в Настройках → HitCam." : "No camera access. Allow it in Settings → HitCam."
    }
}
