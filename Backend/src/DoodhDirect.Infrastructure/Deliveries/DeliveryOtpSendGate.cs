namespace DoodhDirect.Infrastructure.Deliveries;

public sealed class DeliveryOtpSendGate
{
    private readonly object sync = new();
    private readonly Dictionary<long, GateEntry> entries = [];

    internal async ValueTask<IAsyncDisposable> AcquireAsync(
        long deliveryId,
        CancellationToken cancellationToken)
    {
        GateEntry entry;
        lock (sync)
        {
            if (!entries.TryGetValue(deliveryId, out entry!))
            {
                entry = new GateEntry();
                entries.Add(deliveryId, entry);
            }

            entry.WaiterCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            return new Lease(this, deliveryId, entry);
        }
        catch
        {
            ReleaseReference(deliveryId, entry);
            throw;
        }
    }

    private void Release(long deliveryId, GateEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(deliveryId, entry);
    }

    private void ReleaseReference(long deliveryId, GateEntry entry)
    {
        lock (sync)
        {
            entry.WaiterCount--;
            if (entry.WaiterCount == 0)
            {
                entries.Remove(deliveryId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int WaiterCount { get; set; }
    }

    private sealed class Lease(
        DeliveryOtpSendGate owner,
        long deliveryId,
        GateEntry entry) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.Release(deliveryId, entry);
            }

            return ValueTask.CompletedTask;
        }
    }
}
