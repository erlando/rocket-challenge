using Rockets.Api;
using Rockets.Api.Rockets;
using Rockets.Application.Ingestion;
using Rockets.Application.Rockets;
using Rockets.Application.Storage;
using Rockets.Storage.Sqlite;

// The content root is the app's own folder, not the working directory, so appsettings.json (port 8088, log levels,
// storage) is found however the service is started: dotnet run, or the built DLL from any directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RocketRegistry>();
builder.Services.AddSingleton<IngestionStats>();
builder.Services.AddSingleton(services => services.GetRequiredService<IConfiguration>()
    .GetSection("Ingestion").Get<IngestionOptions>() ?? new IngestionOptions());
builder.Services.AddSingleton<IMessageStore>(CreateStore);
builder.Services.AddSingleton<IngestionPipeline>();
// Hosted services start before the server accepts requests, so the log is replayed first.
builder.Services.AddHostedService<IngestionHostedService>();

var app = builder.Build();

app.MapPost("/messages", async (HttpRequest request, IngestionPipeline pipeline, CancellationToken requestAborted) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync(requestAborted);
    try
    {
        // Stored, duplicate and rejected messages are all acknowledged: none of them should be resent.
        await pipeline.SubmitAsync(body, requestAborted);
        return Results.NoContent();
    }
    catch (IngestionUnavailableException e)
    {
        // 503 makes the test program resend the message, which is safe because ingestion is idempotent.
        return Results.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/rockets/{channel}", (string channel, RocketRegistry registry) =>
    registry.Find(channel) is { } rocket
        ? Results.Ok(RocketResponse.From(rocket))
        : Results.Problem($"Rocket {channel} is not known.", statusCode: StatusCodes.Status404NotFound));

app.MapGet("/rockets", (string? sortBy, string? order, RocketRegistry registry) =>
{
    if (!RocketSorting.TryCreate(sortBy, order, out var sort, out var error))
    {
        return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);
    }

    var rockets = sort(registry.All()).Select(RocketResponse.From).ToList();
    return Results.Ok(new RocketListResponse(rockets.Count, rockets));
});

app.MapGet("/health", (RocketRegistry registry, IngestionStats stats) => Results.Ok(new
{
    status = "healthy",
    rockets = registry.All().Count,
    messages = new
    {
        stored = stats.Stored,
        duplicates = stats.Duplicates,
        rejected = stats.Rejected,
        payloadMismatches = stats.PayloadMismatches,
        storeFailures = stats.StoreFailures,
    },
}));

app.Run();

// Configuration is read when the store is first resolved, so test hosts can override it.
static IMessageStore CreateStore(IServiceProvider services)
{
    var configuration = services.GetRequiredService<IConfiguration>().GetSection("Storage");
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Rockets.Storage");
    switch (configuration["Provider"])
    {
        case null or "Sqlite":
            // A relative path is relative to the app's folder (the content root).
            var path = Path.GetFullPath(Path.Combine(
                services.GetRequiredService<IHostEnvironment>().ContentRootPath, configuration["DatabasePath"] ?? "data/rockets.db"));
            var synchronous = Enum.Parse<SqliteSynchronous>(configuration["Synchronous"] ?? nameof(SqliteSynchronous.Full), ignoreCase: true);
            logger.LogInformation("Storing messages in SQLite at {Path} (synchronous={Synchronous})", path, synchronous);
            return new SqliteMessageStore(new SqliteStoreOptions(path, synchronous), services.GetRequiredService<TimeProvider>());
        case var provider:
            throw new InvalidOperationException($"Unknown storage provider '{provider}'. Supported: Sqlite.");
    }
}

public partial class Program;
