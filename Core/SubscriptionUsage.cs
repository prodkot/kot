using System.Globalization;
namespace Kot.Core;

// Provider counters are independent of this device's locally measured traffic.
public sealed record SubscriptionUsage(long? Upload, long? Download, long? Total, DateTimeOffset? Expires, DateTimeOffset CheckedAt)
{
    public long? Used => Upload.HasValue && Download.HasValue ? (Upload.Value > long.MaxValue - Download.Value ? long.MaxValue : Upload.Value + Download.Value) : null;
    public long? Remaining => Total > 0 && Used.HasValue ? Math.Max(0, Total.Value - Used.Value) : null;
    public int? DaysLeft(DateTimeOffset now) => Expires.HasValue ? (int)Math.Ceiling((Expires.Value - now).TotalDays) : null;
    public bool Valid() => (Upload is null or >= 0) && (Download is null or >= 0) && (Total is null or >= 0) && CheckedAt != default;
    public static SubscriptionUsage? Parse(string header, DateTimeOffset now)
    {
        if (header.Length > 4096) return null;
        Dictionary<string, long> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in header.Split(';'))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair.Length != 2 || pair[0].Trim().ToLowerInvariant() is not ("upload" or "download" or "total" or "expire")) continue;
            string key = pair[0].Trim();
            if (fields.ContainsKey(key) || !long.TryParse(pair[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long n)) return null;
            fields[key] = n;
        }
        long? Get(string key) => fields.TryGetValue(key, out long n) ? n : null;
        DateTimeOffset? expires = null;
        if (Get("expire") is long timestamp && timestamp > 0)
            try { expires = DateTimeOffset.FromUnixTimeSeconds(timestamp); } catch (ArgumentOutOfRangeException) { return null; }
        if (!fields.ContainsKey("upload") && !fields.ContainsKey("download") && !fields.ContainsKey("total") && expires == null) return null;
        return new(Get("upload"), Get("download"), Get("total"), expires, now);
    }
}
