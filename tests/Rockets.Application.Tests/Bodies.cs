using System.Text.Json;
using Rockets.Domain;
using Rockets.Domain.Messages;

namespace Rockets.Application.Tests;

/// <summary>Request bodies as the test program sends them.</summary>
public static class Bodies
{
    public const string Channel = "193270a9-c9cf-404a-8f83-838e71d9ae67";

    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static string Envelope(long number, string type, object message, string channel = Channel) =>
        JsonSerializer.Serialize(new
        {
            metadata = new { channel, messageNumber = number, messageTime = Start.AddSeconds(number), messageType = type },
            message,
        });

    public static string Launched(long number = 1, long launchSpeed = 500, string channel = Channel) =>
        Envelope(number, "RocketLaunched", new { type = "Falcon-9", launchSpeed, mission = "ARTEMIS" }, channel);

    public static string SpeedIncreased(long number, long by = 100, string channel = Channel) =>
        Envelope(number, "RocketSpeedIncreased", new { by }, channel);

    public static string SpeedDecreased(long number, long by = 100, string channel = Channel) =>
        Envelope(number, "RocketSpeedDecreased", new { by }, channel);

    public static RocketMessage Message(string body) => MessageParser.Parse(body).Message
        ?? throw new ArgumentException("Not a valid message body.", nameof(body));

    /// <summary>The reference: the bodies' messages applied in messageNumber order.</summary>
    public static RocketState FoldInOrder(IEnumerable<string> bodies) =>
        bodies.Select(Message).OrderBy(m => m.MessageNumber)
            .Aggregate(RocketState.Initial, (state, message) => state.Apply(message));
}
