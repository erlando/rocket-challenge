using System.Text.Json;

namespace Rockets.Api.Tests;

/// <summary>Request bodies as the test program sends them.</summary>
public static class Bodies
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));

    public static DateTimeOffset TimeOf(long number) => Start.AddSeconds(number);

    public static string Envelope(string channel, long number, string type, object message) =>
        JsonSerializer.Serialize(new
        {
            metadata = new { channel, messageNumber = number, messageTime = TimeOf(number), messageType = type },
            message,
        });

    public static string Launched(string channel, long number = 1, string type = "Falcon-9", long launchSpeed = 500, string mission = "ARTEMIS") =>
        Envelope(channel, number, "RocketLaunched", new { type, launchSpeed, mission });

    public static string SpeedIncreased(string channel, long number, long by) =>
        Envelope(channel, number, "RocketSpeedIncreased", new { by });

    public static string Exploded(string channel, long number, string reason = "PRESSURE_VESSEL_FAILURE") =>
        Envelope(channel, number, "RocketExploded", new { reason });
}
