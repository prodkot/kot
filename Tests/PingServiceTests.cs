using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Kot.Core;
using Kot.Windows;

namespace Kot.Tests
{
    // Executes the production Windows orchestration on a single owner context.
    // TCP samples use a loopback listener. Only ProbeCore is a controlled fixture;
    // these tests do not claim to start Xray or exercise WebView2.
    internal static class PingServiceTests
    {
        public static void Run(Action<bool, string> check)
        {
            var previous = SynchronizationContext.Current;
            using var context = new OwnerContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                var task = Exercise(check);
                var watch = Stopwatch.StartNew();
                while (!task.IsCompleted)
                {
                    if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Ping owner fixture");
                    context.Pump();
                }
                task.GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); ProbeCore.Gates.Clear(); }
        }

        static async Task Exercise(Action<bool, string> check)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var life = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var accept = Accept(listener, life.Token);
            var service = new PingService();
            var owner = Environment.CurrentManagedThreadId;
            var callbacksOwned = true;
            service.Changed += () => callbacksOwned &= Environment.CurrentManagedThreadId == owner;
            var http = new PingOptions { Attempts = 1, TimeoutMs = 500, Parallelism = 1 };
            var tcp = new PingOptions { Mode = "tcp", Attempts = 1, TimeoutMs = 500, Parallelism = 1 };
            var a = Make("a", listener); var b = Make("b", listener);
            try
            {
                var first = ProbeCore.Hold("a");
                service.StartNode(a, http);
                service.StartNode(a, http); // Same UI tick, before first worker starts.
                service.StartNode(b, tcp); service.StartNode(b, tcp);
                check(service.Busy && service.Total == 2 && service.Get("a")?.Status == "checking" && service.Get("b")?.Status == "queued", "ping: per-node queue and duplicate suppression");
                first.SetResult(); await Until(() => !service.Busy);
                check(service.Get("a")?.Status == "error" && service.Get("b") is { Status: "ok", Successes: 1 } && service.Done == 2,
                    "ping: failed first probe releases queue; second measures real loopback TCP");

                var second = ProbeCore.Hold("b");
                service.StartNode(b, http);
                service.StartNode(a, tcp); // Retry previously failed first node while second is busy.
                check(service.Get("a")?.Status == "queued", "ping: retry independent node during another probe");
                bool batchBlocked = false;
                try { service.Start([a, b], tcp); } catch (UserError) { batchBlocked = true; }
                check(batchBlocked && service.Total == 2, "ping: batch still has one owner");
                second.SetResult(); await Until(() => !service.Busy);
                check(service.Get("a")?.Status == "ok" && service.Get("b")?.Status == "error", "ping: both rows can fail and retry independently");

                ProbeCore.Hold("a");
                service.StartNode(a, http); service.StartNode(b, tcp); service.Cancel();
                bool stoppingBlocked = false;
                try { service.StartNode(Make("c", listener), tcp); } catch (UserError) { stoppingBlocked = true; }
                await service.Stop();
                check(stoppingBlocked && !service.Busy && service.Get("a")?.Status == "cancelled" && service.Get("b")?.Status == "cancelled",
                    "ping: cancellation drains active and queued jobs; no work appended while stopping");
                await service.Stop(clear: true);
                check(service.Get("a") == null && service.Get("b") == null && service.Total == 0 && service.Done == 0,
                    "ping: profile/settings cleanup clears retained results and queue");
                service.StartNode(b, tcp); await Until(() => !service.Busy);
                check(service.Get("b")?.Status == "ok" && service.Total == 1 && service.Done == 1 && service.Get("a") == null,
                    "ping: fresh run after cancellation has no stale queue or callbacks");
                service.Start([a, b], tcp); await Until(() => !service.Busy);
                check(service.Done == 2 && service.Get("a")?.Status == "ok" && service.Get("b")?.Status == "ok" && callbacksOwned,
                    "ping: batch progress and all notifications stay on owner context");
            }
            finally { await service.Stop(clear: true); life.Cancel(); listener.Stop(); await accept; }
        }
        static Node Make(string id, TcpListener listener) => new(id, "Fixture " + id, "vless",
            new JsonObject { ["server"] = "127.0.0.1", ["server_port"] = ((IPEndPoint)listener.LocalEndpoint).Port });
        static async Task Until(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!ready()) await Task.Delay(5, timeout.Token);
        }
        static async Task Accept(TcpListener listener, CancellationToken ct)
        {
            try { while (true) { using var socket = await listener.AcceptTcpClientAsync(ct); } }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (SocketException) when (ct.IsCancellationRequested) { }
        }
        sealed class OwnerContext : SynchronizationContext, IDisposable
        {
            readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> callbacks = new();
            public override void Post(SendOrPostCallback d, object? state) => callbacks.Add((d, state));
            public void Pump() { if (callbacks.TryTake(out var item, 50)) item.Callback(item.State); }
            public void Dispose() => callbacks.Dispose();
        }
    }
}
namespace Kot.Windows
{
    internal sealed class ProbeCore : IAsyncDisposable
    {
        internal static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new();
        internal static TaskCompletionSource Hold(string id) => Gates[id] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Port => throw new InvalidOperationException("Fixture never creates a proxy");
        public static async Task<ProbeCore> Start(Node node, CancellationToken ct)
        {
            await Gates[node.Id].Task.WaitAsync(ct);
            throw new UserError("Synthetic probe failure");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
