using Kot.Core;
using System.Text;
namespace Kot.Tests;
public static class BodyTests
{
    public static async Task Run(Action<bool, string> check)
    {
        using var small = new StringContent("{\"now\":\"node-test\"}", Encoding.UTF8);
        check(await BoundedBody.ReadUtf8(small, 65536, CancellationToken.None) == "{\"now\":\"node-test\"}", "local API reads a valid response");
        using var known = new StringContent(new string('x', 65537)); bool rejected = false;
        try { await BoundedBody.ReadUtf8(known, 65536, CancellationToken.None); } catch (UserError) { rejected = true; }
        check(rejected, "local API rejects an oversized Content-Length");
        using var stream = new UnknownLengthStream(new byte[1024 * 1024]); using var chunked = new StreamContent(stream); rejected = false;
        try { await BoundedBody.ReadUtf8(chunked, 65536, CancellationToken.None); } catch (UserError) { rejected = true; }
        check(rejected && stream.BytesRead <= 65536 + 8192, "unknown-length local API body is stopped at the byte limit");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); using var content = new StringContent("x"); bool cancelled = false;
        try { await BoundedBody.ReadUtf8(content, 65536, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "local API body respects cancellation");
    }
    sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public long BytesRead { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing && CanRead) BytesRead = Position; base.Dispose(disposing); }
    }
}
