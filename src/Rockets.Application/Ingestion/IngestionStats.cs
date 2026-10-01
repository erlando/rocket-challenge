namespace Rockets.Application.Ingestion;

/// <summary>Counters for monitoring, updated by the writer and read by anyone.</summary>
public sealed class IngestionStats
{
    private long _stored;
    private long _duplicates;
    private long _rejected;
    private long _payloadMismatches;
    private long _storeFailures;

    public long Stored => Interlocked.Read(ref _stored);

    public long Duplicates => Interlocked.Read(ref _duplicates);

    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>Redeliveries whose content differed from the stored message. The first write wins.</summary>
    public long PayloadMismatches => Interlocked.Read(ref _payloadMismatches);

    public long StoreFailures => Interlocked.Read(ref _storeFailures);

    internal void Add(IngestionOutcome outcome)
    {
        switch (outcome)
        {
            case IngestionOutcome.Stored: Interlocked.Increment(ref _stored); break;
            case IngestionOutcome.Duplicate: Interlocked.Increment(ref _duplicates); break;
            case IngestionOutcome.Rejected: Interlocked.Increment(ref _rejected); break;
        }
    }

    internal void AddPayloadMismatch() => Interlocked.Increment(ref _payloadMismatches);

    internal void AddStoreFailure() => Interlocked.Increment(ref _storeFailures);
}
