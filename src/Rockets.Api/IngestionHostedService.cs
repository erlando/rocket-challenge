using Rockets.Application.Ingestion;

namespace Rockets.Api;

/// <summary>Ties the pipeline to the host: replay the log on start, drain the queue on shutdown.</summary>
public sealed class IngestionHostedService(IngestionPipeline pipeline) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => pipeline.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => pipeline.StopAsync();
}
