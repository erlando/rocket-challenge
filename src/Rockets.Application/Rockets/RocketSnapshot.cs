using Rockets.Domain;

namespace Rockets.Application.Rockets;

/// <summary>
/// What readers see of a rocket: its current state and how complete the received sequence is.
/// Immutable, and published only after the messages behind it are committed.
/// </summary>
public sealed record RocketSnapshot(
    string Channel,
    RocketState State,
    long LastMessageNumber,
    long CheckpointMessageNumber,
    int PendingMessageCount,
    long MissingMessageCount)
{
    /// <summary>True when every message up to the highest number received has been applied.</summary>
    public bool IsComplete => CheckpointMessageNumber == LastMessageNumber;

    public static RocketSnapshot From(RocketLedger ledger) => new(
        ledger.Channel,
        ledger.Current,
        ledger.LastMessageNumber,
        ledger.CheckpointNumber,
        ledger.Pending.Count,
        ledger.MissingMessageCount);
}
