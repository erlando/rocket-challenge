using Rockets.Capture;

namespace Rockets.Capture.Tests;

public class OracleTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static CaptureRecord Message(long number, string type, string message, string channel = "a", int status = 204, int attempt = 1) =>
        new(Start.AddSeconds(number), attempt, status, channel, number, type, Start.AddSeconds(number), message);

    private static readonly CaptureRecord[] Capture =
    [
        Message(3, "RocketMissionChanged", """{"newMission":"SHUTTLE_MIR"}"""),
        Message(1, "RocketLaunched", """{"type":"Falcon-9","launchSpeed":500,"mission":"ARTEMIS"}"""),
        Message(2, "RocketSpeedIncreased", """{"by":300}""", status: 503),
        Message(2, "RocketSpeedIncreased", """{"by":300}""", attempt: 2),
        Message(2, "RocketSpeedIncreased", """{"by":999}""", attempt: 3),
        Message(4, "RocketSpeedDecreased", """{"by":100}"""),
        Message(5, "RocketExploded", """{"reason":"ENGINE_FAILURE"}"""),
        Message(1, "RocketLaunched", """{"type":"Juno-I","launchSpeed":100,"mission":"APOLLO"}""", channel: "b"),
    ];

    private static ExpectedState Expected => Oracle.Expect(Capture, "test");

    private static ActualRocket Matching(ExpectedRocket rocket) => new(
        rocket.Channel, rocket.Type, rocket.Mission, rocket.Speed, rocket.Status, rocket.ExplosionReason,
        rocket.LastMessageNumber, rocket.LastMessageNumber, 0, true);

    private static List<StoredMessage> StoredFrom(IEnumerable<CaptureRecord> records) => records
        .Where(r => r.WasAcknowledged)
        .DistinctBy(r => r.Key)
        .Select(r => new StoredMessage(r.Channel!, r.MessageNumber!.Value, r.MessageType!, r.Message!))
        .ToList();

    [Fact]
    public void Each_rocket_is_folded_from_its_first_acknowledged_deliveries_in_number_order()
    {
        var expected = Expected;

        Assert.Equal(6, expected.MessageCount);
        Assert.Equal(["a", "b"], expected.Rockets.Select(r => r.Channel));
        Assert.Equal(new ExpectedRocket("a", "Falcon-9", "SHUTTLE_MIR", 700, "exploded", "ENGINE_FAILURE", 5, 5, expected.Rockets[0].ContentHash),
            expected.Rockets[0]);
        Assert.Equal("launched", expected.Rockets[1].Status);
        Assert.Equal(100, expected.Rockets[1].Speed);
    }

    [Fact]
    public void The_content_hash_ignores_input_order_but_not_content()
    {
        (long, string, string)[] messages = [(1, "RocketLaunched", "{}"), (2, "RocketSpeedIncreased", """{"by":1}""")];

        Assert.Equal(Oracle.ContentHash(messages), Oracle.ContentHash(messages.Reverse()));
        Assert.NotEqual(Oracle.ContentHash(messages), Oracle.ContentHash([messages[0], (2, "RocketSpeedIncreased", """{"by":2}""")]));
    }

    [Fact]
    public void A_service_that_matches_has_no_differences()
    {
        var expected = Expected;

        var differences = Oracle.Compare(expected, expected.Rockets.Select(Matching).ToList(), StoredFrom(Capture));

        Assert.Empty(differences);
    }

    [Fact]
    public void Wrong_state_an_incomplete_sequence_and_a_missing_rocket_are_reported()
    {
        var expected = Expected;
        var actual = new List<ActualRocket> { Matching(expected.Rockets[0]) with { Speed = 600, IsComplete = false } };

        var differences = Oracle.Compare(expected, actual, StoredFrom(Capture));

        Assert.Contains("a: speed is 600, expected 700", differences);
        Assert.Contains("a: sequence.isComplete is False, expected True", differences);
        Assert.Contains("b: missing from GET /rockets", differences);
        Assert.Contains("the service has 1 rockets, expected 2", differences);
    }

    [Fact]
    public void A_missing_or_altered_stored_message_is_reported()
    {
        var expected = Expected;
        var actual = expected.Rockets.Select(Matching).ToList();
        var stored = StoredFrom(Capture);
        stored.RemoveAll(m => m is { Channel: "a", MessageNumber: 3 });
        stored[0] = stored[0] with { PayloadJson = """{"type":"Falcon-9","launchSpeed":501,"mission":"ARTEMIS"}""" };

        var differences = Oracle.Compare(expected, actual, stored);

        Assert.Contains("the message log has 5 messages, expected 6", differences);
        Assert.Contains("a: stored message count is 4, expected 5", differences);
        Assert.Contains(differences, d => d.StartsWith("a: stored content hash", StringComparison.Ordinal));
        Assert.DoesNotContain(differences, d => d.StartsWith("b:", StringComparison.Ordinal));
    }
}
