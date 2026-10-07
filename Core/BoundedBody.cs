using System.Text;
namespace Kot.Core;

public static class BoundedBody
{
    // Content-Length is optional and untrusted. Bound the actual streamed bytes too.
    public static async Task<string> ReadUtf8(HttpContent content, int maximum, CancellationToken ct)
    {
        if (maximum < 1 || maximum > 2 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (content.Headers.ContentLength > maximum) throw new UserError("Слишком большой ответ локального API.");
        using var body = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream(); byte[] block = new byte[Math.Min(maximum + 1, 8192)];
        while (true)
        {
            int read = await body.ReadAsync(block, ct); if (read == 0) break;
            if (buffer.Length + read > maximum) throw new UserError("Слишком большой ответ локального API.");
            buffer.Write(block, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
