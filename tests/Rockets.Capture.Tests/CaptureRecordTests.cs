using Rockets.Capture;

namespace Rockets.Capture.Tests;

public class CaptureRecordTests
{
    private const string Body = """
        {
            "metadata": {
                "channel": "193270a9-c9cf-404a-8f83-838e71d9ae67",
                "messageNumber": 1,
                "messageTime": "2022-02-02T19:39:05.86337+01:00",
                "messageType": "RocketLaunched"
            },
            "message": { "type": "Falcon-9", "launchSpeed": 500, "mission": "ARTEMIS" }
        }
        """;

    [Fact]
    public void A_message_survives_a_round_trip_as_a_single_line()
    {
        var receivedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        var line = CaptureRecord.Serialize(receivedAt, attempt: 2, status: 204, Body);
        var record = CaptureRecord.Parse(line);

        Assert.DoesNotContain('\n', line);
        Assert.Equal(receivedAt, record.ReceivedAt);
        Assert.Equal(2, record.Attempt);
        Assert.Equal(204, record.Status);
        Assert.Equal("193270a9-c9cf-404a-8f83-838e71d9ae67", record.Channel);
        Assert.Equal(1, record.MessageNumber);
        Assert.Equal("RocketLaunched", record.MessageType);
        Assert.Equal("""{"type":"Falcon-9","launchSpeed":500,"mission":"ARTEMIS"}""", record.Message);
        Assert.Equal("193270a9-c9cf-404a-8f83-838e71d9ae67#1", CaptureRecord.TryGetKey(Body));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"message":{}}""")]
    [InlineData("""{"metadata":{"channel":"x"}}""")]
    public void A_body_that_is_not_a_rocket_message_is_kept_but_marked_malformed(string body)
    {
        var record = CaptureRecord.Parse(CaptureRecord.Serialize(DateTimeOffset.UtcNow, 1, 204, body));

        Assert.False(record.IsWellFormed);
        Assert.Null(CaptureRecord.TryGetKey(body));
    }
}
