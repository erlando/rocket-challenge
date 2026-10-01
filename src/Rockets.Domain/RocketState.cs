using Rockets.Domain.Messages;

namespace Rockets.Domain;

public enum RocketStatus
{
    AwaitingLaunch,
    Launched,
    Exploded,
}

/// <summary>The state of one rocket, built by applying its messages in messageNumber order.</summary>
public sealed record RocketState(
    string? Type,
    string? Mission,
    long Speed,
    RocketStatus Status,
    string? ExplosionReason,
    DateTimeOffset? LaunchedAt,
    DateTimeOffset? UpdatedAt)
{
    public static RocketState Initial { get; } = new(null, null, 0, RocketStatus.AwaitingLaunch, null, null, null);

    /// <summary>
    /// Returns the state after <paramref name="message"/>. Callers apply messages in messageNumber order.
    /// Speed uses checked arithmetic, so an overflow throws instead of producing a wrong speed.
    /// </summary>
    public RocketState Apply(RocketMessage message)
    {
        var applied = message.Payload switch
        {
            RocketLaunched launched => this with
            {
                Type = launched.Type,
                Mission = launched.Mission,
                Speed = launched.LaunchSpeed,
                Status = Status == RocketStatus.Exploded ? RocketStatus.Exploded : RocketStatus.Launched,
                LaunchedAt = message.MessageTime,
            },
            RocketSpeedIncreased increased => this with { Speed = checked(Speed + increased.By) },
            RocketSpeedDecreased decreased => this with { Speed = checked(Speed - decreased.By) },
            RocketMissionChanged changed => this with { Mission = changed.NewMission },
            // An explosion is permanent; later messages still update the other fields.
            RocketExploded exploded => this with { Status = RocketStatus.Exploded, ExplosionReason = exploded.Reason },
            UnknownMessage => null,
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.Payload, "Unhandled payload type."),
        };

        return applied is null ? this : applied with { UpdatedAt = message.MessageTime };
    }
}
