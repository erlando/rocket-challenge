using System.Collections.Concurrent;

namespace Rockets.Capture;

/// <summary>
/// A stand-in for the real service that records every delivery attempt.
/// Probe options make the first attempt of each message fail or stall, to observe how the test program retries.
/// </summary>
public static class CaptureServer
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var config = builder.Configuration;

        var urls = config["Urls"] ?? "http://localhost:8088";
        var path = config["Capture:Path"] ?? "artifacts/capture/capture.ndjson";
        var firstAttemptStatus = config.GetValue<int?>("Probe:FirstAttemptStatus");
        var firstAttemptDelay = TimeSpan.FromMilliseconds(config.GetValue("Probe:FirstAttemptDelayMs", 0));
        builder.WebHost.UseUrls(urls);

        using var log = new CaptureLog(path);
        var attempts = new ConcurrentDictionary<string, int>();

        var app = builder.Build();
        app.MapPost("/messages", async (HttpRequest request) =>
        {
            var receivedAt = DateTimeOffset.UtcNow;
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();

            var key = CaptureRecord.TryGetKey(body);
            var attempt = key is null ? 1 : attempts.AddOrUpdate(key, 1, (_, count) => count + 1);

            var status = StatusCodes.Status204NoContent;
            if (attempt == 1)
            {
                if (firstAttemptDelay > TimeSpan.Zero)
                {
                    await Task.Delay(firstAttemptDelay);
                }
                status = firstAttemptStatus ?? status;
            }

            log.Append(receivedAt, attempt, status, body);
            return Results.StatusCode(status);
        });

        Console.WriteLine($"Capturing {urls}/messages to {Path.GetFullPath(path)}");
        if (firstAttemptStatus is not null || firstAttemptDelay > TimeSpan.Zero)
        {
            Console.WriteLine($"Probe: first attempt of each message gets status {firstAttemptStatus ?? 204} after {firstAttemptDelay.TotalMilliseconds} ms");
        }

        await app.RunAsync();
        return 0;
    }
}
