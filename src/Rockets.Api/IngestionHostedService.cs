using Rockets.Application.Ingestion;
using Rockets.Application.Storage;

namespace Rockets.Api;

/// <summary>
/// Ties the pipeline to the host: replay the log on start, drain the queue on shutdown.
/// With <c>Storage:ResetOnStart=true</c> the log is cleared first, so the service starts with no rockets.
/// </summary>
public sealed class IngestionHostedService(
    IngestionPipeline pipeline,
    IMessageStore store,
    IConfiguration configuration,
    ILogger<IngestionHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.GetValue<bool>("Storage:ResetOnStart"))
        {
            // The schema must exist before it can be cleared; the pipeline initializes again, which is harmless.
            await store.InitializeAsync(cancellationToken);
            var deleted = await store.ClearAsync(cancellationToken);
            logger.LogWarning("Storage:ResetOnStart is set: deleted {MessageCount} stored messages", deleted);
        }

        await pipeline.StartAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => pipeline.StopAsync();
}
