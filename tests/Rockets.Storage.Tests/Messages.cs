using System.Text.Json;
using Rockets.Domain.Messages;

namespace Rockets.Storage.Tests;

/// <summary>Builds valid messages for storage tests by parsing JSON envelopes, as the service will.</summary>
public static class Messages
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(2));

    public static RocketMessage Create(string channel, long number, string type, object message)
    {
        var json = JsonSerializer.Serialize(new
        {
            metadata = new { channel, messageNumber = number, messageTime = Start.AddSeconds(number), messageType = type },
            message,
        });
        var result = MessageParser.Parse(json);
        Assert.True(result.IsSuccess, result.Error);
        return result.Message!;
    }

    public static RocketMessage Launched(string channel, long number = 1, string mission = "ARTEMIS") =>
        Create(channel, number, "RocketLaunched", new { type = "Falcon-9", launchSpeed = 500, mission });

    public static RocketMessage SpeedIncreased(string channel, long number, long by = 100) =>
        Create(channel, number, "RocketSpeedIncreased", new { by });
}
