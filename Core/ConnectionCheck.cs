namespace Kot.Core;

// Uses only the captured running tunnel's health proxy. No new core or direct fallback.
public static class ConnectionCheck
{
    public static PingOptions Options(PingOptions saved)
    {
        var options = new PingOptions { Mode = "http", Url = saved.Url, TimeoutMs = saved.TimeoutMs, Attempts = saved.Attempts };
        options.Validate(); return options;
    }
    public static async Task<QualitySummary> Run(int port, PingOptions saved, Func<bool> sameConnection,
        Action<int?> sample, CancellationToken ct)
    {
        if (port is < 1 or > 65535) throw new UserError("Сначала подключите VPN.");
        var options = Options(saved); List<int?> samples = [];
        void CheckSession()
        {
            ct.ThrowIfCancellationRequested();
            if (!sameConnection()) throw new UserError("Соединение изменилось. Запустите проверку снова.");
        }
        for (int i = 0; i < options.Attempts; i++)
        {
            CheckSession(); int? ms = null;
            try { ms = await Latency.Http(options.Url, port, options.TimeoutMs, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UserError) { }
            CheckSession(); samples.Add(ms); sample(ms);
        }
        return ConnectionTools.Summarize(samples);
    }
}
