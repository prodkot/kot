using System.Text;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
namespace Kot.Core;

public static class QrCodes
{
    public const int MaxPixels = 16_000_000;
    public static BitMatrix Encode(string text, int size = 512)
    {
        if (size is < 128 or > 1024) throw new ArgumentOutOfRangeException(nameof(size));
        if (Encoding.UTF8.GetByteCount(text) > 2000) throw new UserError("Ссылка слишком длинная для QR. Скопируйте её текстом.");
        ValidatePayload(text);
        return new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size, new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "UTF-8", [EncodeHintType.MARGIN] = 4 });
    }
    public static string Decode(byte[] rgb, int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height > MaxPixels || rgb.Length != (long)width * height * 3) throw new UserError("Изображение слишком большое или повреждено.");
        var reader = new BarcodeReaderGeneric { AutoRotate = true, Options = new DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = [BarcodeFormat.QR_CODE], CharacterSet = "UTF-8" } };
        var results = reader.DecodeMultiple(new RGBLuminanceSource(rgb, width, height, RGBLuminanceSource.BitmapFormat.RGB24));
        if (results == null || results.Length == 0) throw new UserError("QR не найден. Выберите изображение, где код виден целиком.");
        var payloads = results.Select(r => r.Text.Trim()).Distinct().ToArray();
        if (payloads.Length != 1) throw new UserError("На изображении несколько QR. Обрежьте его до одного кода.");
        ValidatePayload(payloads[0]); return payloads[0];
    }
    public static void ValidatePayload(string text)
    {
        if (text.Length > 32768 || text.Any(char.IsControl)) throw new UserError("Неверное содержимое QR.");
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 && text.Length <= 2048) return;
        _ = Subscriptions.ParseNode(text);
    }
}
