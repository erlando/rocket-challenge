using Rockets.Domain.Messages;

namespace Rockets.Application.Storage;

/// <summary>
/// The append-only message log, which is the only stored state. Rocket state is derived from it by replay.
/// This interface is the seam for swapping SQLite for Postgres; every implementation must pass
/// the contract tests in <c>Rockets.Storage.Tests</c>.
/// Implementations are used by the single writer and at startup, so they need not support concurrent writes.
/// </summary>
public interface IMessageStore
{
    /// <summary>Creates the schema if it does not exist. Safe to call more than once.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a batch in one transaction: either everything is stored or nothing is.
    /// A message whose (channel, messageNumber) is already stored is left untouched and reported as a duplicate,
    /// including a duplicate of an earlier message in the same batch.
    /// </summary>
    Task<CommitResult> CommitAsync(
        IReadOnlyList<RocketMessage> messages,
        IReadOnlyList<RejectedMessage> rejections,
        CancellationToken cancellationToken = default);

    /// <summary>Streams every stored message, grouped by channel and in messageNumber order within a channel.</summary>
    IAsyncEnumerable<RocketMessage> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Streams one rocket's stored messages in messageNumber order.</summary>
    IAsyncEnumerable<RocketMessage> ReadChannelAsync(string channel, CancellationToken cancellationToken = default);

    /// <summary>Streams the rejected messages in the order they were recorded.</summary>
    IAsyncEnumerable<RejectedMessage> ReadRejectedAsync(CancellationToken cancellationToken = default);
}

/// <summary>A request body that could not be accepted as a message, kept for diagnosis.</summary>
public sealed record RejectedMessage(DateTimeOffset ReceivedAt, string Reason, string Body);

/// <param name="PayloadMismatch">True when the stored message has different content; the first write wins.</param>
public sealed record StoredDuplicate(string Channel, long MessageNumber, bool PayloadMismatch);

public sealed record CommitResult(IReadOnlyList<StoredDuplicate> Duplicates)
{
    public static CommitResult Empty { get; } = new([]);
}
