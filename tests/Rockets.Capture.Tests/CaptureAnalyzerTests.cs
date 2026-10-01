using Rockets.Capture;

namespace Rockets.Capture.Tests;

public class CaptureAnalyzerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static CaptureRecord Message(
        long number, string type = "RocketSpeedIncreased", string? message = null,
        string channel = "rocket-a", int status = 204, int attempt = 1, double atSeconds = 0) =>
        new(Start.AddSeconds(atSeconds), attempt, status, channel, number, type,
            Start.AddSeconds(number), message ?? DefaultPayload(type));

    private static string DefaultPayload(string type) => type switch
    {
        "RocketLaunched" => """{"type":"Falcon-9","launchSpeed":500,"mission":"ARTEMIS"}""",
        "RocketExploded" => """{"reason":"PRESSURE_VESSEL_FAILURE"}""",
        "RocketMissionChanged" => """{"newMission":"SHUTTLE_MIR"}""",
        _ => """{"by":100}""",
    };

    [Fact]
    public void In_order_messages_have_no_reordering_or_gaps()
    {
        var report = CaptureAnalyzer.Analyze([
            Message(1, "RocketLaunched", atSeconds: 1),
            Message(2, atSeconds: 2),
            Message(3, atSeconds: 3),
        ]);

        Assert.Equal(3, report.UniqueMessages);
        Assert.Equal(0, report.OutOfOrderArrivals);
        Assert.Equal(0, report.MaxPending);
        Assert.Equal(0, report.MissingMessages);
        Assert.Equal(0, report.RocketsWhereFirstIsNotLaunched);
        Assert.Equal(700, report.MaxSpeed);
    }

    [Fact]
    public void Out_of_order_arrival_is_measured_by_distance_and_pending_count()
    {
        var report = CaptureAnalyzer.Analyze([
            Message(1, "RocketLaunched", atSeconds: 1),
            Message(4, atSeconds: 2),
            Message(3, atSeconds: 3),
            Message(2, atSeconds: 4),
        ]);

        Assert.Equal(2, report.OutOfOrderArrivals);
        Assert.Equal(2, report.MaxReorderDistance);
        Assert.Equal(2, report.MaxPending);
        Assert.Equal(0, report.MissingMessages);
    }

    [Fact]
    public void Redeliveries_are_split_by_whether_an_earlier_attempt_succeeded()
    {
        var report = CaptureAnalyzer.Analyze([
            Message(1, "RocketLaunched", atSeconds: 1),
            Message(1, "RocketLaunched", attempt: 2, atSeconds: 2),
            Message(2, status: 500, atSeconds: 3),
            Message(2, attempt: 2, atSeconds: 5),
        ]);

        Assert.Equal(2, report.UniqueMessages);
        Assert.Equal(1, report.RedeliveriesAfterSuccess);
        Assert.Equal(1, report.RetriesAfterFailure);
        Assert.Equal(TimeSpan.FromSeconds(2), report.MaxRetryDelay);
        Assert.Equal(0, report.NeverAcknowledged);
        Assert.Equal(2, report.MaxAttempts);
    }

    [Fact]
    public void Messages_that_never_succeed_are_not_counted_as_delivered()
    {
        var report = CaptureAnalyzer.Analyze([
            Message(1, "RocketLaunched", atSeconds: 1),
            Message(2, status: 400, atSeconds: 2),
            Message(3, atSeconds: 3),
        ]);

        Assert.Equal(1, report.NeverAcknowledged);
        Assert.Equal(1, report.RocketsWithGaps);
        Assert.Equal(1, report.MissingMessages);
    }

    [Fact]
    public void A_redelivery_with_different_content_is_a_payload_mismatch()
    {
        var report = CaptureAnalyzer.Analyze([
            Message(1, "RocketLaunched", atSeconds: 1),
            Message(2, message: """{"by":100}""", atSeconds: 2),
            Message(2, message: """{"by":999}""", attempt: 2, atSeconds: 3),
        ]);

        Assert.Equal(1, report.PayloadMismatches);
    }

    [Fact]
    public void Messages_numbered_after_an_explosion_are_counted()
    {
        var report = CaptureAnalyzer.Analyze([
            Message(1, "RocketLaunched", atSeconds: 1),
            Message(2, "RocketExploded", atSeconds: 2),
            Message(3, "RocketMissionChanged", atSeconds: 3),
        ]);

        Assert.Equal(1, report.RocketsExploded);
        Assert.Equal(1, report.MessagesAfterExplosion);
    }

    [Fact]
    public void A_rocket_whose_first_message_is_not_a_launch_is_flagged()
    {
        var report = CaptureAnalyzer.Analyze([
            Message(2, "RocketLaunched", channel: "rocket-b", atSeconds: 1),
            Message(1, "RocketSpeedDecreased", channel: "rocket-b", atSeconds: 2),
        ]);

        Assert.Equal(1, report.RocketsWhereFirstIsNotLaunched);
        Assert.Equal(1, report.RocketsWithNegativeSpeed);
    }
}
