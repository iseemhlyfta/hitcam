using System.Globalization;

namespace HitCam.Desktop.Services;

/// <summary>Tiny RU/EN string table; Russian when the Windows UI language is Russian, English otherwise.</summary>
public static class Loc
{
    private static readonly bool Russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    public static string WaitingTitle => Russian ? "Подключите iPhone" : "Connect your iPhone";
    public static string WaitingHint => Russian
        ? "Откройте HitCam на iPhone в той же Wi-Fi сети и отсканируйте QR-код или введите адрес:"
        : "Open HitCam on your iPhone on the same Wi-Fi network and scan the QR code or enter the address:";
    public static string NoNetwork => Russian ? "Нет подключения к локальной сети" : "No local network connection";
    public static string PairingTitle => Russian ? "Введите PIN на iPhone" : "Enter this PIN on the iPhone";
    public static string PairingFrom(string device) => Russian ? $"Запрос сопряжения от «{device}»" : $"Pairing request from “{device}”";
    public static string Connected(string device) => Russian ? $"Подключено: {device}" : $"Connected: {device}";
    public static string Disconnected(string reason) => Russian ? $"Отключено ({reason})" : $"Disconnected ({reason})";
    public static string Disconnect => Russian ? "Отключить" : "Disconnect";
    public static string ServerFailed(string error) => Russian ? $"Не удалось открыть порт: {error}" : $"Could not open the port: {error}";
    public static string CameraHint => Russian
        ? "В Zoom, Discord, Teams, OBS или браузере выберите камеру «HitCam»."
        : "In Zoom, Discord, Teams, OBS or a browser, pick the “HitCam” camera.";
    public static string CameraReady(string name) => Russian
        ? $"Виртуальная камера «{name}» доступна в других программах."
        : $"The “{name}” virtual camera is available to other apps.";
    public static string CameraNotInstalled => Russian
        ? "Виртуальная камера не установлена (нужны права администратора, один раз)."
        : "The virtual camera is not installed (needs admin rights once).";
    public static string CameraOutdated => Russian
        ? "Виртуальная камера работает, но установлена старая версия."
        : "The virtual camera works, but an older version is installed.";
    public static string CameraUnavailable => Russian
        ? "Виртуальная камера не собрана: нет HitCamVCam.dll рядом с программой."
        : "The virtual camera is not built: HitCamVCam.dll is missing next to the app.";
    public static string CameraFailed(string error) => Russian ? $"Не удалось включить камеру: {error}" : $"Could not start the camera: {error}";
    public static string CameraInstalling => Russian ? "Установка камеры…" : "Installing the camera…";
    public static string CameraInstallFailed => Russian ? "Камера не установлена: установка отменена или не удалась." : "Camera not installed: cancelled or failed.";
    public static string DecoderFailed(string error) => Russian ? $"Декодер H.264 не запустился: {error}" : $"The H.264 decoder failed to start: {error}";
    public static string InstallCamera => Russian ? "Установить камеру" : "Install camera";
    public static string UpdateCamera => Russian ? "Обновить камеру" : "Update camera";
    public static string ReinstallCamera => Russian ? "Переустановить" : "Reinstall";
    public static string Decoded => Russian ? "декодировано" : "decoded";
    public static string Stream => Russian ? "Поток" : "Stream";
    public static string Received => Russian ? "Принято" : "Received";
    public static string Latency => Russian ? "Задержка" : "Latency";
    public static string Phone => Russian ? "Телефон" : "Phone";
    public static string Charging => Russian ? "заряжается" : "charging";
}
