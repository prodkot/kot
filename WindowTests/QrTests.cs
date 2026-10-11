using Kot.Windows;
using System.Drawing.Imaging;
namespace Kot.WindowTests;

public static class QrTests
{
    public static void Run(Action<bool, string> check)
    {
        const string link = "trojan://p%40ss%3Aword@example.com:443?sni=example.com#QR%20Тест";
        using var bitmap = QrImages.Encode(link);
        using var png = new MemoryStream(); bitmap.Save(png, ImageFormat.Png); png.Position = 0;
        using var loaded = Image.FromStream(png);
        check(QrImages.Decode(loaded) == link, "native PNG export can be reimported with credentials and Unicode intact");
        using var screenshot = new Bitmap(641, 677, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(screenshot)) { graphics.Clear(Color.White); graphics.DrawImage(bitmap, new Point(61, 83)); }
        screenshot.RotateFlip(RotateFlipType.Rotate90FlipNone);
        check(QrImages.Decode(screenshot) == link, "native QR reader handles a rotated screenshot and odd bitmap stride");
        try
        {
            Clipboard.SetImage(bitmap);
            using var image = Clipboard.GetImage();
            check(image != null && QrImages.Decode(image) == link, "Windows clipboard QR image survives native copy and read");
        }
        finally { Clipboard.Clear(); }
    }
}
