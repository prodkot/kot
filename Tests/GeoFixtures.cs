namespace Kot.Tests;

static class GeoFixtures
{
    // geodat.proto: RU -> example.org, RU -> 127.0.0.0/8.
    public static readonly byte[] Site = [10, 21, 10, 2, 82, 85, 18, 15, 8, 2, 18, 11, .. System.Text.Encoding.UTF8.GetBytes("example.org")];
    public static readonly byte[] Ip = [10, 14, 10, 2, 82, 85, 18, 8, 10, 4, 127, 0, 0, 0, 16, 8];
    public static byte[] For(Uri uri) => uri.AbsolutePath.EndsWith("geoip.dat") ? Ip : Site;
}
