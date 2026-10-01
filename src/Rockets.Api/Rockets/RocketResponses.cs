using Rockets.Application.Rockets;
using Rockets.Domain;

namespace Rockets.Api.Rockets;

/// <summary>A rocket as the API returns it: its state, plus how complete the received sequence is.</summary>
public sealed record RocketResponse(
    string Channel,
    string? Type,
    string? Mission,
    long Speed,
    string Status,
    string? ExplosionReason,
    DateTimeOffset? LaunchedAt,
    DateTimeOffset? UpdatedAt,
    SequenceResponse Sequence)
{
    public static RocketResponse From(RocketSnapshot rocket) => new(
        rocket.Channel,
        rocket.State.Type,
        rocket.State.Mission,
        rocket.State.Speed,
        StatusName(rocket.State.Status),
        rocket.State.ExplosionReason,
        rocket.State.LaunchedAt,
        rocket.State.UpdatedAt,
        new SequenceResponse(
            rocket.LastMessageNumber,
            rocket.CheckpointMessageNumber,
            rocket.PendingMessageCount,
            rocket.MissingMessageCount,
            rocket.IsComplete));

    // Spelled out, so renaming the domain enum can't silently change the API.
    private static string StatusName(RocketStatus status) => status switch
    {
        RocketStatus.AwaitingLaunch => "awaitingLaunch",
        RocketStatus.Launched => "launched",
        RocketStatus.Exploded => "exploded",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown rocket status."),
    };
}

/// <param name="LastMessageNumber">The highest message number received.</param>
/// <param name="CheckpointMessageNumber">Every message up to this number has been received and applied.</param>
/// <param name="PendingMessageCount">Messages received above the checkpoint, waiting for a gap to fill.</param>
/// <param name="MissingMessageCount">Message numbers below <paramref name="LastMessageNumber"/> not received yet.</param>
/// <param name="IsComplete">True when nothing is missing, so the state is exact.</param>
public sealed record SequenceResponse(
    long LastMessageNumber,
    long CheckpointMessageNumber,
    int PendingMessageCount,
    long MissingMessageCount,
    bool IsComplete);

/// <summary>The rocket list. An object rather than a bare array, so fields such as paging can be added without breaking clients.</summary>
public sealed record RocketListResponse(int Count, IReadOnlyList<RocketResponse> Rockets);
