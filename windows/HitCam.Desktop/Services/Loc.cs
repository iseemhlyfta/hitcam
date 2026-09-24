using System.Globalization;

namespace HitCam.Desktop.Services;

/// <summary>Tiny RU/EN string table; Russian when the Windows UI language is Russian, English otherwise.</summary>
public static class Loc
{
    private static readonly bool Russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    // Waiting for a phone
    public static string WaitingTitle => Russian ? "Подключите iPhone" : "Connect your iPhone";
    public static string WaitingHint => Russian
        ? "Откройте HitCam на iPhone в той же Wi-Fi сети и отсканируйте QR-код. Или введите адрес вручную:"
        : "Open HitCam on your iPhone on the same Wi-Fi network and scan the QR code. Or enter the address manually:";
    public static string OtherAddresses(string list) => Russian ? $"Другие адреса этого ПК: {list}" : $"Other addresses of this PC: {list}";
    public static string NoNetwork => Russian ? "Нет подключения к локальной сети" : "No local network connection";
    public static string CopyAddress => Russian ? "Скопировать адрес" : "Copy address";
    public static string Step1 => Russian ? "Откройте HitCam" : "Open HitCam";
    public static string Step2 => Russian ? "Сканируйте код" : "Scan the code";
    public static string Step3 => Russian ? "Введите PIN с экрана" : "Enter the PIN shown here";
    public static string Disconnected(string reason) => Russian ? $"Отключено ({reason})" : $"Disconnected ({reason})";
    public static string ServerFailed(string error) => Russian ? $"Не удалось открыть порт: {error}" : $"Could not open the port: {error}";

    // Pairing
    public static string PairingRequest(string device, string address) =>
        Russian ? $"Запрос сопряжения от «{device}» · {address}" : $"Pairing request from “{device}” · {address}";
    public static string PairingTitle => Russian ? "Введите этот код на iPhone" : "Enter this code on the iPhone";
    public static string PairingHint => Russian
        ? "Код действует 2 минуты. После сопряжения iPhone\nбудет подключаться к этому ПК без кода."
        : "The code is valid for 2 minutes. Once paired, the iPhone\nconnects to this PC without a code.";
    public static string Cancel => Russian ? "Отменить" : "Cancel";

    // Streaming
    public static string DeviceSubtitle(string address) => Russian ? $"Wi-Fi · {address} · сопряжён" : $"Wi-Fi · {address} · paired";
    public static string CameraLiveBadge => Russian ? "Камера «HitCam» в эфире" : "“HitCam” camera is live";
    public static string Disconnect => Russian ? "Отключить" : "Disconnect";
    public static string WaitingForFrame => Russian ? "Ждём первый кадр…" : "Waiting for the first frame…";
    public static string Fullscreen => Russian ? "Во весь экран" : "Full screen";
    public static string ExitFullscreen => Russian ? "Выйти из полноэкранного режима (Esc)" : "Exit full screen (Esc)";
    public static string Stream => Russian ? "Поток" : "Stream";
    public static string Received => Russian ? "Принято" : "Received";
    public static string Latency => Russian ? "Задержка" : "Latency";
    public static string Phone => Russian ? "Телефон" : "Phone";
    public static string Charging => Russian ? "заряжается" : "charging";
    public static string Milliseconds => Russian ? "мс" : "ms";
    public static string Megabits => Russian ? "Мбит/с" : "Mbit/s";

    // Camera settings
    public static string CameraSettings => Russian ? "Камера" : "Camera";
    public static string CameraSettingsHint => Russian ? "Настройки сразу применяются на iPhone" : "Settings apply on the iPhone right away";
    public static string WaitingForPhoneSettings => Russian ? "Ждём настройки от телефона…" : "Waiting for the phone's settings…";
    public static string Lens => Russian ? "Объектив" : "Lens";
    public static string LensUltraWide => Russian ? "Ультра" : "Ultra";
    public static string LensWide => Russian ? "Широкий" : "Wide";
    public static string LensTele => Russian ? "Теле" : "Tele";
    public static string LensFront => Russian ? "Фронт" : "Front";
    public static string Quality => Russian ? "Качество" : "Quality";
    public static string Zoom => Russian ? "Зум" : "Zoom";
    public static string Exposure => Russian ? "Экспозиция" : "Exposure";
    public static string Focus => Russian ? "Фокус" : "Focus";
    public static string AutoFocus => Russian ? "Авто" : "Auto";
    public static string Torch => Russian ? "Фонарик" : "Torch";
    public static string Mirror => Russian ? "Зеркало" : "Mirror";
    public static string Rotate => Russian ? "Повернуть" : "Rotate";
    public static string WhiteBalance => Russian ? "Баланс белого" : "White balance";
    public static string WhiteBalanceHint => Russian
        ? "Укажите температуру света в комнате: лампы накаливания около 2700 K, дневной свет около 5500 K"
        : "Set the light temperature in the room: incandescent about 2700 K, daylight about 5500 K";
    public static string Tint => Russian ? "Оттенок" : "Tint";
    public static string TintHint => Russian ? "Влево — зеленее, вправо — пурпурнее" : "Left: greener, right: more magenta";
    public static string ExposureLock => Russian ? "Фиксация" : "Lock";
    public static string ExposureLockHint => Russian
        ? "Яркость перестаёт подстраиваться, когда вы двигаетесь или меняется свет"
        : "Brightness stops adapting when you move or the light changes";
    public static string Stabilization => Russian ? "Стабилизация" : "Stabilization";
    public static string StabilizationHint => Russian
        ? "Убирает дрожание, если телефон в руке. Добавляет задержку, «Кино» — заметную"
        : "Removes shake when the phone is handheld. Adds latency; “Cinematic” adds a noticeable amount";
    public static string StabilizationOff => Russian ? "Выкл" : "Off";
    public static string StabilizationStandard => Russian ? "Обычная" : "Standard";
    public static string StabilizationCinematic => Russian ? "Кино" : "Cinematic";

    // Virtual camera status
    public static string CameraReadyTitle => Russian ? "Виртуальная камера «HitCam»" : "“HitCam” virtual camera";
    public static string CameraReadyDetail => Russian
        ? "Выберите её в Zoom, Discord, Teams, OBS или браузере"
        : "Pick it in Zoom, Discord, Teams, OBS or a browser";
    public static string CameraNotInstalledTitle => Russian ? "Виртуальная камера не установлена" : "The virtual camera is not installed";
    public static string CameraNotInstalledDetail => Russian
        ? "Нужны права администратора, один раз. После этого камера «HitCam» появится в других программах."
        : "Needs admin rights once. Then the “HitCam” camera shows up in other apps.";
    public static string CameraOutdatedTitle => Russian ? "Доступно обновление камеры" : "A camera update is available";
    public static string CameraOutdatedDetail => Russian ? "Камера работает, но установлена старая версия." : "The camera works, but an older version is installed.";
    public static string CameraFailedTitle => Russian ? "Камера не запустилась" : "The camera did not start";
    public static string CameraFailedDetail(string error) => Russian ? $"Ошибка {error}. Попробуйте переустановить." : $"Error {error}. Try reinstalling.";
    public static string CameraUnavailableTitle => Russian ? "Камера не собрана" : "The camera is not built";
    public static string CameraUnavailableDetail => Russian ? "Нет HitCamVCam.dll рядом с программой." : "HitCamVCam.dll is missing next to the app.";
    public static string CameraInstallingTitle => Russian ? "Установка камеры…" : "Installing the camera…";
    public static string CameraInstallingDetail => Russian ? "Подтвердите запрос администратора." : "Confirm the administrator prompt.";
    public static string CameraInstallFailedTitle => Russian ? "Камера не установлена" : "Camera not installed";
    public static string CameraInstallFailedDetail => Russian ? "Установка отменена или не удалась." : "The installation was cancelled or failed.";
    public static string DecoderFailed(string error) => Russian ? $"Декодер H.264 не запустился: {error}" : $"The H.264 decoder failed to start: {error}";
    public static string InstallCamera => Russian ? "Установить камеру" : "Install camera";
    public static string UpdateCamera => Russian ? "Обновить камеру" : "Update camera";
    public static string ReinstallCamera => Russian ? "Переустановить" : "Reinstall";
}
