namespace Kot.Core;

// Register before waiting for the mutation gate or opening a file dialog, so
// cancellation can also reach an operation which has not started its work yet.
public sealed class ImportOperations
{
    Operation? current;
    readonly HashSet<Guid> issued = [];
    public Operation Begin(string id, CancellationToken lifetime)
    {
        if (!Guid.TryParseExact(id, "D", out var key)) throw new UserError("Некорректный ID импорта.");
        if (!issued.Add(key)) throw new UserError("ID импорта уже использован.");
        current?.Cancel();
        return current = new Operation(this, key.ToString("D"), lifetime);
    }
    public void Cancel(string id) { if (Guid.TryParseExact(id, "D", out var key) && current?.Id == key.ToString("D")) current.Cancel(); }
    public void CancelCurrent() => current?.Cancel();
    public sealed class Operation : IDisposable
    {
        readonly ImportOperations owner;
        readonly CancellationTokenSource cancellation;
        public string Id { get; }
        public CancellationToken Token => cancellation.Token;
        internal Operation(ImportOperations owner, string id, CancellationToken lifetime)
        { this.owner = owner; Id = id; cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); }
        internal void Cancel() => cancellation.Cancel();
        public void Dispose()
        {
            if (owner.current == this) owner.current = null;
            cancellation.Dispose();
        }
    }
}
