namespace Rockets.Domain.Messages;

/// <summary>
/// A validated message from a rocket. <see cref="PayloadJson"/> is the compact JSON of the
/// <c>message</c> object, and <see cref="PayloadHash"/> identifies the type and payload,
/// so redeliveries with different content can be detected.
/// </summary>
public sealed record RocketMessage(
    string Channel,
    long MessageNumber,
    DateTimeOffset MessageTime,
    string MessageType,
    MessagePayload Payload,
    string PayloadJson,
    string PayloadHash);

public abstract record MessagePayload;

public sealed record RocketLaunched(string Type, long LaunchSpeed, string Mission) : MessagePayload;

public sealed record RocketSpeedIncreased(long By) : MessagePayload;

public sealed record RocketSpeedDecreased(long By) : MessagePayload;

public sealed record RocketExploded(string Reason) : MessagePayload;

public sealed record RocketMissionChanged(string NewMission) : MessagePayload;

/// <summary>A message type this version does not know. It is stored and counts towards the sequence, but changes no state.</summary>
public sealed record UnknownMessage : MessagePayload;
