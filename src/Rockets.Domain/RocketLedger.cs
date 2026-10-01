using System.Collections.Immutable;
using Rockets.Domain.Messages;

namespace Rockets.Domain;

public enum LedgerOutcome
{
    /// <summary>The message number was already received; nothing changed.</summary>
    Duplicate,

    /// <summary>The message was added to pending, above a gap; the checkpoint did not move.</summary>
    Accepted,

    /// <summary>The message closed the gap after the checkpoint, so the checkpoint moved forward.</summary>
    Advanced,
}

/// <param name="PayloadMismatch">True when a duplicate's content differs from the message already received.</param>
public sealed record LedgerResult(RocketLedger Ledger, LedgerOutcome Outcome, bool PayloadMismatch);

/// <summary>
/// The ordering model for one rocket. The checkpoint is the exact state after messages 1..N with no gaps;
/// pending holds the messages above N; the current state is the checkpoint with pending applied in order, skipping gaps.
/// Immutable: <see cref="Apply"/> returns a new ledger.
/// </summary>
public sealed class RocketLedger
{
    private RocketLedger(
        string channel,
        RocketState checkpoint,
        long checkpointNumber,
        ImmutableSortedDictionary<long, RocketMessage> pending,
        RocketState current,
        long lastMessageNumber)
    {
        Channel = channel;
        Checkpoint = checkpoint;
        CheckpointNumber = checkpointNumber;
        Pending = pending;
        Current = current;
        LastMessageNumber = lastMessageNumber;
    }

    public string Channel { get; }

    public RocketState Checkpoint { get; }

    public long CheckpointNumber { get; }

    public ImmutableSortedDictionary<long, RocketMessage> Pending { get; }

    public RocketState Current { get; }

    public long LastMessageNumber { get; }

    public long MissingMessageCount => LastMessageNumber - CheckpointNumber - Pending.Count;

    public static RocketLedger Empty(string channel)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        return new RocketLedger(
            channel, RocketState.Initial, 0, ImmutableSortedDictionary<long, RocketMessage>.Empty, RocketState.Initial, 0);
    }

    /// <summary>
    /// Adds a message. Costs O(pending): the current state is recomputed from the checkpoint and the pending messages.
    /// If applying a message throws, the exception propagates and this ledger is unchanged.
    /// </summary>
    public LedgerResult Apply(RocketMessage message)
    {
        if (message.Channel != Channel)
        {
            throw new ArgumentException($"A message for rocket {message.Channel} was applied to the ledger of {Channel}.", nameof(message));
        }

        var number = message.MessageNumber;
        if (number <= CheckpointNumber)
        {
            // Messages at or below the checkpoint are not kept, so their content can't be compared here.
            // The store compares payload hashes when the insert hits the existing row.
            return new LedgerResult(this, LedgerOutcome.Duplicate, PayloadMismatch: false);
        }

        if (Pending.TryGetValue(number, out var existing))
        {
            return new LedgerResult(this, LedgerOutcome.Duplicate, existing.PayloadHash != message.PayloadHash);
        }

        var pending = Pending.Add(number, message);
        var checkpoint = Checkpoint;
        var checkpointNumber = CheckpointNumber;
        while (pending.TryGetValue(checkpointNumber + 1, out var next))
        {
            checkpoint = checkpoint.Apply(next);
            checkpointNumber++;
            pending = pending.Remove(checkpointNumber);
        }

        // Pending enumerates in messageNumber order, so this applies everything above the gap in order.
        var current = pending.Values.Aggregate(checkpoint, (state, pendingMessage) => state.Apply(pendingMessage));

        var ledger = new RocketLedger(Channel, checkpoint, checkpointNumber, pending, current, Math.Max(LastMessageNumber, number));
        var outcome = checkpointNumber > CheckpointNumber ? LedgerOutcome.Advanced : LedgerOutcome.Accepted;
        return new LedgerResult(ledger, outcome, PayloadMismatch: false);
    }
}
