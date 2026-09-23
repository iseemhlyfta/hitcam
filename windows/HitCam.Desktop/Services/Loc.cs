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
    public static string PreviewPending => Russian
        ? "Видео принимается. Превью и виртуальная камера появятся в следующих фазах."
        : "Video is being received. Preview and the virtual camera arrive in the next phases.";
    public static string Stream => Russian ? "Поток" : "Stream";
    public static string Received => Russian ? "Принято" : "Received";
    public static string Latency => Russian ? "Задержка" : "Latency";
    public static string Phone => Russian ? "Телефон" : "Phone";
    public static string Charging => Russian ? "заряжается" : "charging";
}
