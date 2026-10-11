using Kot.Core;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace Kot.Windows;

public static class QrImages
{
    public static string Decode(Image source)
    {
        if ((long)source.Width * source.Height > QrCodes.MaxPixels) throw new UserError("Изображение больше 16 млн пикселей. Обрежьте его до QR.");
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap)) { graphics.Clear(Color.White); graphics.DrawImage(source, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); }
        var bits = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] rgb = new byte[bitmap.Width * bitmap.Height * 3], row = new byte[bitmap.Width * 3];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                int offset = y * row.Length;
                for (int x = 0; x < row.Length; x += 3) { rgb[offset + x] = row[x + 2]; rgb[offset + x + 1] = row[x + 1]; rgb[offset + x + 2] = row[x]; }
            }
            return QrCodes.Decode(rgb, bitmap.Width, bitmap.Height);
        }
        finally { bitmap.UnlockBits(bits); }
    }
    public static Bitmap Encode(string text)
    {
        var matrix = QrCodes.Encode(text); var bitmap = new Bitmap(matrix.Width, matrix.Height, PixelFormat.Format24bppRgb);
        var bits = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] row = new byte[Math.Abs(bits.Stride)];
            for (int y = 0; y < matrix.Height; y++)
            {
                Array.Fill(row, (byte)255);
                for (int x = 0; x < matrix.Width; x++) { byte color = matrix[x, y] ? (byte)0 : (byte)255; row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = color; }
                Marshal.Copy(row, 0, IntPtr.Add(bits.Scan0, y * bits.Stride), row.Length);
            }
        }
        finally { bitmap.UnlockBits(bits); }
        return bitmap;
    }
}
