using System.Text.Json;
using Rockets.Domain.Messages;

namespace Rockets.Domain.Tests;

/// <summary>Builds messages as the test program sends them: as JSON envelopes, run through the parser.</summary>
public static class TestMessages
{
    public const string Channel = "193270a9-c9cf-404a-8f83-838e71d9ae67";

    public static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset TimeOf(long number) => Start.AddSeconds(number);

    public static string Envelope(long number, string type, object message, string channel = Channel) =>
        JsonSerializer.Serialize(new
        {
            metadata = new { channel, messageNumber = number, messageTime = TimeOf(number), messageType = type },
            message,
        });

    public static RocketMessage Parse(string json)
    {
        var result = MessageParser.Parse(json);
        Assert.True(result.IsSuccess, $"Expected a valid message, got: {result.Error}");
        return result.Message!;
    }

    public static RocketMessage Launched(long number = 1, string type = "Falcon-9", long launchSpeed = 500, string mission = "ARTEMIS", string channel = Channel) =>
        Parse(Envelope(number, "RocketLaunched", new { type, launchSpeed, mission }, channel));

    public static RocketMessage SpeedIncreased(long number, long by, string channel = Channel) =>
        Parse(Envelope(number, "RocketSpeedIncreased", new { by }, channel));

    public static RocketMessage SpeedDecreased(long number, long by, string channel = Channel) =>
        Parse(Envelope(number, "RocketSpeedDecreased", new { by }, channel));

    public static RocketMessage MissionChanged(long number, string newMission, string channel = Channel) =>
        Parse(Envelope(number, "RocketMissionChanged", new { newMission }, channel));

    public static RocketMessage Exploded(long number, string reason = "PRESSURE_VESSEL_FAILURE", string channel = Channel) =>
        Parse(Envelope(number, "RocketExploded", new { reason }, channel));

    public static RocketMessage Unknown(long number, string channel = Channel) =>
        Parse(Envelope(number, "RocketRefuelled", new { litres = 1000 }, channel));

    /// <summary>The reference fold: apply messages in messageNumber order from the initial state.</summary>
    public static RocketState FoldInOrder(IEnumerable<RocketMessage> messages) =>
        messages.OrderBy(m => m.MessageNumber).Aggregate(RocketState.Initial, (state, message) => state.Apply(message));

    public static RocketLedger ApplyAll(IEnumerable<RocketMessage> messages, string channel = Channel) =>
        messages.Aggregate(RocketLedger.Empty(channel), (ledger, message) => ledger.Apply(message).Ledger);
}
