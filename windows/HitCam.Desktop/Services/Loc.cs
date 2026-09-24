using System.Globalization;

namespace HitCam.Desktop.Services;

/// <summary>Tiny RU/EN string table; Russian when the Windows UI language is Russian, English otherwise.</summary>
public static class Loc
{
    private static readonly bool Russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    // Waiting for a phone
    public static string WaitingTitle => Russian ? "Подключите телефон" : "Connect your phone";
    public static string WaitingHint => Russian
        ? "Откройте HitCam на телефоне в той же Wi-Fi сети и отсканируйте QR-код. Или введите адрес вручную:"
        : "Open HitCam on your phone on the same Wi-Fi network and scan the QR code. Or enter the address manually:";
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
    public static string PairingTitle => Russian ? "Введите этот код на телефоне" : "Enter this code on the phone";
    public static string PairingHint => Russian
        ? "Код действует 2 минуты. После сопряжения телефон\nбудет подключаться к этому ПК без кода."
        : "The code is valid for 2 minutes. Once paired, the phone\nconnects to this PC without a code.";
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
    public static string CameraSettingsHint => Russian ? "Настройки сразу применяются на телефоне" : "Settings apply on the phone right away";
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

    // Phone camera noise reduction (on the phone, before compression)
    public static string PhoneNoiseReduction => Russian ? "Шумоподавление камеры телефона" : "Phone camera noise reduction";
    public static string PhoneNoiseReductionHint => Russian
        ? "Работает на телефоне до сжатия видео. «Качество» чище, но может немного снизить частоту кадров"
        : "Runs on the phone before the video is compressed. “Quality” is cleaner but may lower the frame rate a little";
    public static string PhoneNoiseReductionOff => Russian ? "Выкл" : "Off";
    public static string PhoneNoiseReductionFast => Russian ? "Быстрое" : "Fast";
    public static string PhoneNoiseReductionHigh => Russian ? "Качество" : "Quality";

    // Processing on the PC
    public static string Processing => Russian ? "Обработка" : "Processing";
    public static string ProcessingHint => Russian
        ? "Работает на этом ПК и сразу видна в камере «HitCam». Телефон не нагружает"
        : "Runs on this PC and shows in the “HitCam” camera right away. Does not load the phone";
    public static string ProcessingOff => Russian ? "Выкл" : "Off";
    public static string NoiseReduction => Russian ? "Шумоподавление" : "Noise reduction";
    public static string TemporalNoiseReduction => Russian ? "Временное шумоподавление" : "Temporal noise reduction";
    public static string TemporalNoiseReductionHint => Russian
        ? "Усредняет соседние кадры и убирает зерно при слабом свете. На больших значениях движение может слегка смазываться"
        : "Averages neighbouring frames to remove grain in low light. High values may slightly smear motion";
    public static string ArtifactReduction => Russian ? "Удаление артефактов сжатия (NVIDIA RTX)" : "Compression artifact removal (NVIDIA RTX)";
    public static string ArtifactReductionHint => Russian
        ? "Нейросеть NVIDIA убирает блоки и ореолы сжатия. Работает на видеокарте RTX"
        : "An NVIDIA neural network removes compression blocks and halos. Runs on the RTX GPU";
    public static string ArtifactReductionGentle => Russian ? "Мягко" : "Gentle";
    public static string ArtifactReductionStrong => Russian ? "Сильно" : "Strong";
    public static string ColorAndSharpness => Russian ? "Цвет и резкость" : "Colour and sharpness";
    public static string Brightness => Russian ? "Яркость" : "Brightness";
    public static string Contrast => Russian ? "Контраст" : "Contrast";
    public static string Saturation => Russian ? "Насыщенность" : "Saturation";
    public static string Shadows => Russian ? "Тени" : "Shadows";
    public static string ShadowsHint => Russian ? "Вправо — тёмные места светлее" : "Right: dark areas get brighter";
    public static string Highlights => Russian ? "Света" : "Highlights";
    public static string HighlightsHint => Russian ? "Влево — возвращает детали в пересвеченных местах" : "Left: recovers detail in overexposed areas";
    public static string Sharpness => Russian ? "Резкость" : "Sharpness";
    public static string ResetColor => Russian ? "Сбросить" : "Reset";
    public static string ResetColorHint => Russian ? "Вернуть цвет и резкость к исходным" : "Return colour and sharpness to neutral";
    public static string ProcessingTime(double milliseconds) =>
        Russian ? $"Обработка: {milliseconds:0.0} мс" : $"Processing: {milliseconds:0.0} ms";
    public static string ArtifactReductionTime(double milliseconds) =>
        Russian ? $"артефакты: {milliseconds:0.0} мс" : $"artifacts: {milliseconds:0.0} ms";
    public static string ArtifactReductionFailed(int code) => Russian
        ? $"Удаление артефактов не работает (код NVIDIA {code}). Например, вертикальное видео не поддерживается."
        : $"Artifact removal is not working (NVIDIA code {code}). Portrait video, for example, is not supported.";

    // Virtual camera status
    public static string CameraReadyTitle => Russian ? "Виртуальная камера «HitCam»" : "“HitCam” virtual camera";
    public static string CameraReadyDetail => Russian
        ? "Выберите её в Zoom, Discord, Teams, OBS или браузере"
        : "Pick it in Zoom, Discord, Teams, OBS or a browser";
    public static string CameraNotInstalledTitle => Russian ? "Виртуальная камера не установлена" : "The virtual camera is not installed";
    public static string CameraNotInstalledDetail => Russian
        ? "Нужны права администратора, один раз. После этого камера «HitCam» появится в других программах. " + CamerasRestartNote
        : "Needs admin rights once. Then the “HitCam” camera shows up in other apps. " + CamerasRestartNote;
    public static string CameraOutdatedTitle => Russian ? "Доступно обновление камеры" : "A camera update is available";
    public static string CameraOutdatedDetail => Russian
        ? "Камера работает, но установлена старая версия. " + CamerasRestartNote
        : "The camera works, but an older version is installed. " + CamerasRestartNote;
    public static string CameraFailedTitle => Russian ? "Камера не запустилась" : "The camera did not start";
    public static string CameraFailedDetail(string error) => Russian
        ? $"Ошибка {error}. Попробуйте переустановить. {CamerasRestartNote}"
        : $"Error {error}. Try reinstalling. {CamerasRestartNote}";
    /// <summary>Installing stops the Windows camera service (FrameServer).</summary>
    public static string CamerasRestartNote => Russian
        ? "Во время установки все камеры Windows, включая встроенную, на пару секунд перезапустятся — идущий звонок потеряет видео."
        : "While installing, all Windows cameras, the built-in one too, restart for a couple of seconds; a call in progress loses video.";
    public static string CameraUnavailableTitle => Russian ? "Камера не собрана" : "The camera is not built";
    public static string CameraUnavailableDetail => Russian ? "Нет HitCamVCam.dll рядом с программой." : "HitCamVCam.dll is missing next to the app.";
    public static string CameraTamperedTitle => Russian ? "Файл камеры повреждён или подменён" : "The camera file is damaged or replaced";
    public static string CameraTamperedDetail => Russian
        ? "HitCamVCam.dll рядом с программой не совпадает с этой версией HitCam, поэтому он не будет установлен. Переустановите HitCam."
        : "HitCamVCam.dll next to the app does not match this HitCam version, so it will not be installed. Reinstall HitCam.";
    public static string DebugInstanceTitle => Russian ? "Отладочный экземпляр: камера отключена" : "Debug instance: camera disabled";
    public static string DebugInstanceDetail => Russian
        ? "Запущено с --port: виртуальная камера «HitCam» не добавляется, работает только превью."
        : "Started with --port: the “HitCam” virtual camera is not added, only the preview works.";
    public static string CameraInstallError(string error) => Russian ? $"Ошибка установки: {error}" : $"Installation error: {error}";
    public static string ActionFailed(string error) => Russian ? $"Не удалось: {error}" : $"Failed: {error}";
    public static string AlreadyRunningNotResponding => Russian
        ? "HitCam уже запущен, но не отвечает. Завершите его в диспетчере задач и запустите снова."
        : "HitCam is already running but not responding. End it in Task Manager and start it again.";
    public static string CameraInstallingTitle => Russian ? "Установка камеры…" : "Installing the camera…";
    public static string CameraInstallingDetail => Russian ? "Подтвердите запрос администратора." : "Confirm the administrator prompt.";
    public static string CameraInstallFailedTitle => Russian ? "Камера не установлена" : "Camera not installed";
    public static string CameraInstallFailedDetail => Russian ? "Установка отменена или не удалась." : "The installation was cancelled or failed.";
    public static string DecoderFailed(string error) => Russian ? $"Декодер H.264 не запустился: {error}" : $"The H.264 decoder failed to start: {error}";
    public static string NativeDllMissing => Russian
        ? "Рядом с HitCam.exe нет файла HitCamVCam.dll. Распакуйте архив HitCam целиком в папку (правой кнопкой → «Извлечь всё») и запускайте HitCam.exe оттуда, а не прямо из архива."
        : "HitCamVCam.dll is missing next to HitCam.exe. Extract the whole HitCam archive to a folder (right-click → Extract All) and start HitCam.exe from there, not from inside the archive.";
    public static string MediaFoundationMissing => Russian
        ? "В этой Windows нет компонентов мультимедиа (выпуск N или KN). Установите их: Параметры → Приложения → Дополнительные компоненты → Добавить компонент → «Пакет компонентов мультимедиа», затем перезагрузите ПК."
        : "This Windows has no media components (an N or KN edition). Install them: Settings → Apps → Optional features → Add a feature → Media Feature Pack, then restart the PC.";
    public static string CameraBusy => Russian
        ? "Камера «HitCam» уже занята другим запущенным HitCam. Закройте его и перезапустите этот."
        : "The “HitCam” camera is already used by another running HitCam. Close it and restart this one.";
    public static string VirtualCameraNeedsWindows11 => Russian
        ? "Виртуальная камера работает только в Windows 11. Превью в HitCam будет работать, но в других программах камеры «HitCam» не будет."
        : "The virtual camera needs Windows 11. The preview in HitCam works, but other apps will not see the “HitCam” camera.";
    public static string InstallCamera =>Russian ? "Установить камеру" : "Install camera";
    public static string UpdateCamera => Russian ? "Обновить камеру" : "Update camera";
    public static string ReinstallCamera => Russian ? "Переустановить" : "Reinstall";
}
