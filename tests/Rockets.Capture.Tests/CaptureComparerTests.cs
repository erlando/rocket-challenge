using Rockets.Capture;

namespace Rockets.Capture.Tests;

public class CaptureComparerTests
{
    private static CaptureRecord Message(long number, string by, int status = 204, int timeOffsetSeconds = 0) =>
        new(DateTimeOffset.UtcNow, 1, status, "rocket-a", number, "RocketSpeedIncreased",
            DateTimeOffset.UtcNow.AddSeconds(timeOffsetSeconds), $$"""{"by":{{by}}}""");

    [Fact]
    public void Captures_with_the_same_messages_are_equal_even_if_message_times_differ()
    {
        var result = CaptureComparer.Compare(
            [Message(1, "100"), Message(2, "200")],
            [Message(2, "200", timeOffsetSeconds: 60), Message(1, "100", timeOffsetSeconds: 60)]);

        Assert.True(result.AreEqual);
        Assert.Equal(2, result.Identical);
    }

    [Fact]
    public void Missing_extra_and_changed_messages_are_reported()
    {
        var result = CaptureComparer.Compare(
            [Message(1, "100"), Message(2, "200")],
            [Message(1, "999"), Message(3, "300")]);

        Assert.False(result.AreEqual);
        Assert.Equal(1, result.OnlyInFirst);
        Assert.Equal(1, result.OnlyInSecond);
        Assert.Equal(1, result.DifferentContent);
    }

    [Fact]
    public void Only_acknowledged_messages_are_compared()
    {
        var result = CaptureComparer.Compare(
            [Message(1, "100"), Message(2, "200", status: 500)],
            [Message(1, "100")]);

        Assert.True(result.AreEqual);
    }
}
