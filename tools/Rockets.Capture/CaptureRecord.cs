using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Rockets.Capture;

/// <summary>
/// One delivery attempt as received by the capture server: one line in the NDJSON capture file.
/// The metadata fields are null when the body could not be parsed as a rocket message.
/// </summary>
public sealed record CaptureRecord(
    DateTimeOffset ReceivedAt,
    int Attempt,
    int Status,
    string? Channel,
    long? MessageNumber,
    string? MessageType,
    DateTimeOffset? MessageTime,
    string? Message)
{
    public bool IsWellFormed => Channel is not null && MessageNumber is not null && MessageType is not null;

    public bool WasAcknowledged => Status is >= 200 and < 300;

    public string? Key => IsWellFormed ? MessageKey(Channel!, MessageNumber!.Value) : null;

    public static string MessageKey(string channel, long messageNumber) => $"{channel}#{messageNumber}";

    /// <summary>Returns the (channel, messageNumber) key of a raw request body, or null if it is not a rocket message.</summary>
    public static string? TryGetKey(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var (channel, number, _, _) = ReadMetadata(document.RootElement);
            return channel is null || number is null ? null : MessageKey(channel, number.Value);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serializes a delivery attempt as a single compact JSON line. Invalid JSON bodies are kept as a string.</summary>
    public static string Serialize(DateTimeOffset receivedAt, int attempt, int status, string body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("receivedAt", receivedAt);
            json.WriteNumber("attempt", attempt);
            json.WriteNumber("status", status);
            json.WritePropertyName("body");
            try
            {
                using var document = JsonDocument.Parse(body);
                document.RootElement.WriteTo(json);
            }
            catch (JsonException)
            {
                json.WriteStringValue(body);
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static CaptureRecord Parse(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var body = root.GetProperty("body");
        var (channel, number, type, time) = ReadMetadata(body);
        var message = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("message", out var m)
            ? m.GetRawText()
            : null;

        return new CaptureRecord(
            root.GetProperty("receivedAt").GetDateTimeOffset(),
            root.GetProperty("attempt").GetInt32(),
            root.GetProperty("status").GetInt32(),
            channel, number, type, time, message);
    }

    /// <summary>Reads a capture file, also while the capture server still has it open for writing.</summary>
    public static IReadOnlyList<CaptureRecord> ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var records = new List<CaptureRecord>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                records.Add(Parse(line));
            }
        }
        return records;
    }

    private static (string? Channel, long? Number, string? Type, DateTimeOffset? Time) ReadMetadata(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null, null);
        }

        string? channel = metadata.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        long? number = metadata.TryGetProperty("messageNumber", out var n) && n.TryGetInt64(out var value) ? value : null;
        string? type = metadata.TryGetProperty("messageType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        DateTimeOffset? time = metadata.TryGetProperty("messageTime", out var mt) && mt.TryGetDateTimeOffset(out var parsed) ? parsed : null;
        return (channel, number, type, time);
    }
}
