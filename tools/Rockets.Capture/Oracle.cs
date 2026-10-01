using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Rockets.Capture;

/// <summary>One rocket's expected final state, derived from a capture, independently of the service's code.</summary>
public sealed record ExpectedRocket(
    string Channel,
    string? Type,
    string? Mission,
    long Speed,
    string Status,
    string? ExplosionReason,
    long LastMessageNumber,
    int MessageCount,
    string ContentHash);

public sealed record ExpectedState(string Source, int MessageCount, IReadOnlyList<ExpectedRocket> Rockets);

/// <summary>A rocket as the service reports it through GET /rockets.</summary>
public sealed record ActualRocket(
    string Channel,
    string? Type,
    string? Mission,
    long Speed,
    string Status,
    string? ExplosionReason,
    long LastMessageNumber,
    long CheckpointMessageNumber,
    long MissingMessageCount,
    bool IsComplete);

/// <summary>A row of the service's message log.</summary>
public sealed record StoredMessage(string Channel, long MessageNumber, string MessageType, string PayloadJson);

/// <summary>
/// The end-to-end oracle. It is deliberately separate from the service: it does not reference Rockets.Domain,
/// and folds each rocket from scratch with its own simple rules, so a bug in the service's ordering or state
/// logic can't hide in both. Ground truth is a capture of a run with the same seed, which Phase 0 showed is deterministic.
/// Message times are not compared: they are set when the test program sends a message, so they differ between runs.
/// </summary>
public static class Oracle
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>expect: derive the expected state from a capture file and write it as JSON.</summary>
    public static int RunExpect(string capturePath, string outputPath)
    {
        var expected = Expect(CaptureRecord.ReadFile(capturePath), Path.GetFileName(capturePath));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(expected, Json) + "\n");
        Console.WriteLine($"Expected state of {expected.Rockets.Count} rockets ({expected.MessageCount} messages) written to {outputPath}");
        return 0;
    }

    /// <summary>verify: compare the running service and its database with an expected state.</summary>
    public static async Task<int> RunVerifyAsync(string expectedPath, string serviceUrl, string databasePath)
    {
        var expected = JsonSerializer.Deserialize<ExpectedState>(File.ReadAllText(expectedPath), Json)
            ?? throw new InvalidDataException($"{expectedPath} is empty.");

        using var http = new HttpClient { BaseAddress = new Uri(serviceUrl) };
        var actual = ParseRockets(await http.GetStringAsync("/rockets"));
        var stored = ReadStoredMessages(databasePath);

        var differences = Compare(expected, actual, stored);
        foreach (var difference in differences)
        {
            Console.WriteLine($"  FAIL {difference}");
        }
        Console.WriteLine(differences.Count == 0
            ? $"  PASS {expected.Rockets.Count} rockets and {expected.MessageCount} messages match {expected.Source}"
            : $"  {differences.Count} difference(s) from {expected.Source}");
        return differences.Count == 0 ? 0 : 1;
    }

    public static ExpectedState Expect(IEnumerable<CaptureRecord> records, string source)
    {
        // The first acknowledged delivery of each (channel, messageNumber) is the message; later ones are redeliveries.
        var messages = new Dictionary<string, CaptureRecord>();
        foreach (var record in records.Where(r => r.IsWellFormed && r.WasAcknowledged))
        {
            messages.TryAdd(record.Key!, record);
        }

        var rockets = messages.Values
            .GroupBy(m => m.Channel!)
            .Select(g => Fold(g.Key, g.OrderBy(m => m.MessageNumber).ToList()))
            .OrderBy(r => r.Channel, StringComparer.Ordinal)
            .ToList();
        return new ExpectedState(source, messages.Count, rockets);
    }

    public static string ContentHash(IEnumerable<(long Number, string Type, string Payload)> messages)
    {
        var text = new StringBuilder();
        foreach (var (number, type, payload) in messages.OrderBy(m => m.Number))
        {
            text.Append(number).Append('|').Append(type).Append('|').Append(payload).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static List<string> Compare(ExpectedState expected, IReadOnlyList<ActualRocket> actual, IReadOnlyList<StoredMessage> stored)
    {
        var differences = new List<string>();
        var actualByChannel = actual.ToDictionary(r => r.Channel, StringComparer.Ordinal);
        var storedByChannel = stored.GroupBy(m => m.Channel).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        if (actual.Count != expected.Rockets.Count)
        {
            differences.Add($"the service has {actual.Count} rockets, expected {expected.Rockets.Count}");
        }
        if (stored.Count != expected.MessageCount)
        {
            differences.Add($"the message log has {stored.Count} messages, expected {expected.MessageCount}");
        }

        foreach (var rocket in expected.Rockets)
        {
            var name = rocket.Channel;
            if (!actualByChannel.TryGetValue(name, out var service))
            {
                differences.Add($"{name}: missing from GET /rockets");
            }
            else
            {
                Check(differences, name, "type", rocket.Type, service.Type);
                Check(differences, name, "mission", rocket.Mission, service.Mission);
                Check(differences, name, "speed", rocket.Speed, service.Speed);
                Check(differences, name, "status", rocket.Status, service.Status);
                Check(differences, name, "explosionReason", rocket.ExplosionReason, service.ExplosionReason);
                Check(differences, name, "sequence.lastMessageNumber", rocket.LastMessageNumber, service.LastMessageNumber);
                Check(differences, name, "sequence.checkpointMessageNumber", rocket.LastMessageNumber, service.CheckpointMessageNumber);
                Check(differences, name, "sequence.missingMessageCount", 0L, service.MissingMessageCount);
                Check(differences, name, "sequence.isComplete", true, service.IsComplete);
            }

            var log = storedByChannel.GetValueOrDefault(name) ?? [];
            Check(differences, name, "stored message count", rocket.MessageCount, log.Count);
            Check(differences, name, "stored content hash", rocket.ContentHash,
                ContentHash(log.Select(m => (m.MessageNumber, m.MessageType, m.PayloadJson))));
        }

        return differences;
    }

    private static ExpectedRocket Fold(string channel, List<CaptureRecord> messages)
    {
        string? type = null, mission = null, reason = null;
        long speed = 0;
        var status = "awaitingLaunch";

        foreach (var message in messages)
        {
            using var document = JsonDocument.Parse(message.Message ?? "{}");
            var body = document.RootElement;
            switch (message.MessageType)
            {
                case "RocketLaunched":
                    type = body.GetProperty("type").GetString();
                    mission = body.GetProperty("mission").GetString();
                    speed = body.GetProperty("launchSpeed").GetInt64();
                    status = status == "exploded" ? "exploded" : "launched";
                    break;
                case "RocketSpeedIncreased":
                    speed += body.GetProperty("by").GetInt64();
                    break;
                case "RocketSpeedDecreased":
                    speed -= body.GetProperty("by").GetInt64();
                    break;
                case "RocketMissionChanged":
                    mission = body.GetProperty("newMission").GetString();
                    break;
                case "RocketExploded":
                    status = "exploded";
                    reason = body.GetProperty("reason").GetString();
                    break;
            }
        }

        return new ExpectedRocket(
            channel, type, mission, speed, status, reason,
            messages[^1].MessageNumber!.Value,
            messages.Count,
            ContentHash(messages.Select(m => (m.MessageNumber!.Value, m.MessageType!, m.Message ?? ""))));
    }

    private static void Check<T>(List<string> differences, string channel, string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            differences.Add($"{channel}: {field} is {actual?.ToString() ?? "null"}, expected {expected?.ToString() ?? "null"}");
        }
    }

    public static IReadOnlyList<ActualRocket> ParseRockets(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("rockets").EnumerateArray().Select(r =>
        {
            var sequence = r.GetProperty("sequence");
            return new ActualRocket(
                r.GetProperty("channel").GetString()!,
                r.GetProperty("type").GetString(),
                r.GetProperty("mission").GetString(),
                r.GetProperty("speed").GetInt64(),
                r.GetProperty("status").GetString()!,
                r.GetProperty("explosionReason").GetString(),
                sequence.GetProperty("lastMessageNumber").GetInt64(),
                sequence.GetProperty("checkpointMessageNumber").GetInt64(),
                sequence.GetProperty("missingMessageCount").GetInt64(),
                sequence.GetProperty("isComplete").GetBoolean());
        }).ToList();
    }

    /// <summary>Reads the service's message log: a SQLite file path, or a Postgres connection string (contains "Host=").</summary>
    private static List<StoredMessage> ReadStoredMessages(string database)
    {
        using DbConnection connection = database.Contains("Host=", StringComparison.OrdinalIgnoreCase)
            ? new NpgsqlConnection(database)
            : new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT channel, message_number, message_type, payload_json FROM messages";
        using var reader = command.ExecuteReader();
        var messages = new List<StoredMessage>();
        while (reader.Read())
        {
            messages.Add(new StoredMessage(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        }
        return messages;
    }
}
