using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Rockets.Domain.Messages;

/// <summary>The outcome of parsing a request body: either a valid message or the reason it was rejected.</summary>
public sealed record ParseResult(RocketMessage? Message, string? Error)
{
    public bool IsSuccess => Message is not null;

    public static ParseResult Success(RocketMessage message) => new(message, null);

    public static ParseResult Failure(string error) => new(null, error);
}

/// <summary>
/// Parses and validates a message envelope. The payload is read according to <c>metadata.messageType</c>;
/// an unknown type is accepted as <see cref="UnknownMessage"/>, so new message types don't break ingestion.
/// </summary>
public static class MessageParser
{
    private static readonly Dictionary<string, Func<JsonElement, PayloadResult>> PayloadReaders = new()
    {
        ["RocketLaunched"] = ReadLaunched,
        ["RocketSpeedIncreased"] = m => ReadAmount(m, by => new RocketSpeedIncreased(by)),
        ["RocketSpeedDecreased"] = m => ReadAmount(m, by => new RocketSpeedDecreased(by)),
        ["RocketMissionChanged"] = m => TryReadString(m, "newMission", out var mission)
            ? PayloadResult.Ok(new RocketMissionChanged(mission))
            : PayloadResult.Fail("message.newMission must be a non-empty string"),
        ["RocketExploded"] = m => TryReadString(m, "reason", out var reason)
            ? PayloadResult.Ok(new RocketExploded(reason))
            : PayloadResult.Fail("message.reason must be a non-empty string"),
    };

    public static ParseResult Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            return ParseResult.Failure($"invalid JSON: {e.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.Object)
            {
                return ParseResult.Failure("metadata must be an object");
            }

            if (!TryReadString(metadata, "channel", out var channel))
            {
                return ParseResult.Failure("metadata.channel must be a non-empty string");
            }

            if (!TryReadInteger(metadata, "messageNumber", out var number) || number < 1)
            {
                return ParseResult.Failure("metadata.messageNumber must be a positive integer");
            }

            if (!metadata.TryGetProperty("messageTime", out var timeElement)
                || timeElement.ValueKind != JsonValueKind.String
                || !timeElement.TryGetDateTimeOffset(out var time))
            {
                return ParseResult.Failure("metadata.messageTime must be an ISO 8601 timestamp");
            }

            if (!TryReadString(metadata, "messageType", out var messageType))
            {
                return ParseResult.Failure("metadata.messageType must be a non-empty string");
            }

            return root.TryGetProperty("message", out var message)
                ? Build(channel, number, time, messageType, message)
                : ParseResult.Failure("message must be an object");
        }
    }

    /// <summary>
    /// Rebuilds a message from the columns it was stored with. The payload goes through the same
    /// validation as on arrival, so a stored message can't bypass the rules.
    /// </summary>
    public static ParseResult FromStored(
        string channel, long messageNumber, DateTimeOffset messageTime, string messageType, string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return Build(channel, messageNumber, messageTime, messageType, document.RootElement);
        }
        catch (JsonException e)
        {
            return ParseResult.Failure($"invalid JSON: {e.Message}");
        }
    }

    /// <summary>Reads the payload for its message type and completes the message with its compact JSON and hash.</summary>
    private static ParseResult Build(
        string channel, long messageNumber, DateTimeOffset messageTime, string messageType, JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            return ParseResult.Failure("message must be an object");
        }

        var payload = PayloadReaders.TryGetValue(messageType, out var read)
            ? read(message)
            : PayloadResult.Ok(new UnknownMessage());
        if (payload.Error is not null)
        {
            return ParseResult.Failure(payload.Error);
        }

        var payloadJson = Compact(message);
        return ParseResult.Success(new RocketMessage(
            channel, messageNumber, messageTime, messageType, payload.Payload!, payloadJson, Hash(messageType, payloadJson)));
    }

    private sealed record PayloadResult(MessagePayload? Payload, string? Error)
    {
        public static PayloadResult Ok(MessagePayload payload) => new(payload, null);

        public static PayloadResult Fail(string error) => new(null, error);
    }

    private static PayloadResult ReadLaunched(JsonElement message)
    {
        if (!TryReadString(message, "type", out var type))
        {
            return PayloadResult.Fail("message.type must be a non-empty string");
        }
        if (!TryReadInteger(message, "launchSpeed", out var launchSpeed) || launchSpeed < 0)
        {
            return PayloadResult.Fail("message.launchSpeed must be a non-negative integer");
        }
        if (!TryReadString(message, "mission", out var mission))
        {
            return PayloadResult.Fail("message.mission must be a non-empty string");
        }
        return PayloadResult.Ok(new RocketLaunched(type, launchSpeed, mission));
    }

    /// <summary>Speed changes carry a magnitude; the message type gives the direction.</summary>
    private static PayloadResult ReadAmount(JsonElement message, Func<long, MessagePayload> create) =>
        TryReadInteger(message, "by", out var by) && by >= 0
            ? PayloadResult.Ok(create(by))
            : PayloadResult.Fail("message.by must be a non-negative integer");

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
        return value.Length > 0;
    }

    private static bool TryReadInteger(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }

    private static string Compact(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            element.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string Hash(string messageType, string payloadJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{messageType}\n{payloadJson}")));
}
