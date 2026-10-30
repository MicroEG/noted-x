using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace NotedX.Desktop.Services;

/// <summary>Renders pairing links as QR codes for the desktop pairing panel.</summary>
public static class PairingQrCode
{
    /// <summary>Creates a frozen bitmap of a QR code containing <paramref name="text"/>.</summary>
    public static BitmapImage Create(string text, int pixelsPerModule = 8)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("QR code text is required.", nameof(text));
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(Math.Clamp(pixelsPerModule, 4, 20));

        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
