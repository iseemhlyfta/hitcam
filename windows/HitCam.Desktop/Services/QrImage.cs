using Avalonia.Media.Imaging;
using QRCoder;

namespace HitCam.Desktop.Services;

public static class QrImage
{
    public static Bitmap Create(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 8);
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }
}
