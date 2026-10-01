namespace Rockets.Application.Ingestion;

public sealed record IngestionOptions
{
    /// <summary>How many requests may wait for the writer. When full, new requests wait up to <see cref="EnqueueTimeout"/>.</summary>
    public int QueueCapacity { get; init; } = 1024;

    /// <summary>The most messages committed in one transaction.</summary>
    public int MaxBatchSize { get; init; } = 256;

    /// <summary>
    /// How long a request waits for room in a full queue before getting 503. Must stay well below the test program's
    /// client timeout of about 10 s, so the service answers before the client gives up and sends a duplicate.
    /// </summary>
    public TimeSpan EnqueueTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
