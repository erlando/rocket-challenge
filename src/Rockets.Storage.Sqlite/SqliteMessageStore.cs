using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Rockets.Application.Storage;
using Rockets.Domain.Messages;

namespace Rockets.Storage.Sqlite;

public enum SqliteSynchronous
{
    /// <summary>Every commit is synced to disk: survives power loss.</summary>
    Full,

    /// <summary>With WAL, commits survive a process crash but may be lost on power loss.</summary>
    Normal,
}

public sealed record SqliteStoreOptions(string DatabasePath, SqliteSynchronous Synchronous = SqliteSynchronous.Full);

/// <summary>
/// The message log in a SQLite file, in WAL mode. Each operation opens a pooled connection;
/// <c>synchronous</c> is a per-connection setting, so it is applied on every open.
/// </summary>
public sealed class SqliteMessageStore : IMessageStore
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS messages (
            channel        TEXT    NOT NULL,
            message_number INTEGER NOT NULL,
            message_type   TEXT    NOT NULL,
            message_time   TEXT    NOT NULL,
            payload_json   TEXT    NOT NULL,
            payload_hash   TEXT    NOT NULL,
            received_at    TEXT    NOT NULL,
            PRIMARY KEY (channel, message_number)
        ) WITHOUT ROWID;

        CREATE TABLE IF NOT EXISTS rejected_messages (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            received_at TEXT NOT NULL,
            reason      TEXT NOT NULL,
            body        TEXT NOT NULL
        );
        """;

    private const string SelectMessages =
        "SELECT channel, message_number, message_type, message_time, payload_json FROM messages";

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly string _connectionPragmas;
    private readonly TimeProvider _time;

    public SqliteMessageStore(SqliteStoreOptions options, TimeProvider? time = null)
    {
        _databasePath = options.DatabasePath;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, Pooling = true }.ToString();
        var synchronous = options.Synchronous switch
        {
            SqliteSynchronous.Full => "FULL",
            SqliteSynchronous.Normal => "NORMAL",
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Synchronous, "Unknown synchronous level."),
        };
        _connectionPragmas = $"PRAGMA synchronous = {synchronous}; PRAGMA busy_timeout = 5000;";
        _time = time ?? TimeProvider.System;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        // WAL is stored in the database file, so setting it once applies to every later connection.
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecuteAsync(connection, Schema, cancellationToken);
    }

    public async Task<CommitResult> CommitAsync(
        IReadOnlyList<RocketMessage> messages,
        IReadOnlyList<RejectedMessage> rejections,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0 && rejections.Count == 0)
        {
            return CommitResult.Empty;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        // Disposing the transaction without committing rolls it back, so an exception anywhere leaves nothing behind.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var duplicates = await InsertMessagesAsync(connection, transaction, messages, cancellationToken);
        await InsertRejectionsAsync(connection, transaction, rejections, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return duplicates.Count == 0 ? CommitResult.Empty : new CommitResult(duplicates);
    }

    public IAsyncEnumerable<RocketMessage> ReadAllAsync(CancellationToken cancellationToken = default) =>
        ReadMessagesAsync($"{SelectMessages} ORDER BY channel, message_number", channel: null, cancellationToken);

    public IAsyncEnumerable<RocketMessage> ReadChannelAsync(string channel, CancellationToken cancellationToken = default) =>
        ReadMessagesAsync($"{SelectMessages} WHERE channel = $channel ORDER BY message_number", channel, cancellationToken);

    public async IAsyncEnumerable<RejectedMessage> ReadRejectedAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT received_at, reason, body FROM rejected_messages ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new RejectedMessage(ParseTime(reader.GetString(0)), reader.GetString(1), reader.GetString(2));
        }
    }

    public async Task<long> ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM rejected_messages";
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "DELETE FROM messages";
        long deleted = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, _connectionPragmas, cancellationToken);
        return connection;
    }

    private async Task<List<StoredDuplicate>> InsertMessagesAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<RocketMessage> messages, CancellationToken cancellationToken)
    {
        var duplicates = new List<StoredDuplicate>();
        if (messages.Count == 0)
        {
            return duplicates;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO messages (channel, message_number, message_type, message_time, payload_json, payload_hash, received_at)
            VALUES ($channel, $number, $type, $time, $payload, $hash, $receivedAt)
            ON CONFLICT (channel, message_number) DO NOTHING
            """;
        var channel = insert.Parameters.Add("$channel", SqliteType.Text);
        var number = insert.Parameters.Add("$number", SqliteType.Integer);
        var type = insert.Parameters.Add("$type", SqliteType.Text);
        var time = insert.Parameters.Add("$time", SqliteType.Text);
        var payload = insert.Parameters.Add("$payload", SqliteType.Text);
        var hash = insert.Parameters.Add("$hash", SqliteType.Text);
        insert.Parameters.AddWithValue("$receivedAt", FormatTime(_time.GetUtcNow()));

        await using var storedHash = connection.CreateCommand();
        storedHash.Transaction = transaction;
        storedHash.CommandText = "SELECT payload_hash FROM messages WHERE channel = $channel AND message_number = $number";
        var storedChannel = storedHash.Parameters.Add("$channel", SqliteType.Text);
        var storedNumber = storedHash.Parameters.Add("$number", SqliteType.Integer);

        foreach (var message in messages)
        {
            channel.Value = message.Channel;
            number.Value = message.MessageNumber;
            type.Value = message.MessageType;
            time.Value = FormatTime(message.MessageTime);
            payload.Value = message.PayloadJson;
            hash.Value = message.PayloadHash;

            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                // The row already exists: the first write wins, and a content change is reported.
                storedChannel.Value = message.Channel;
                storedNumber.Value = message.MessageNumber;
                var existingHash = (string?)await storedHash.ExecuteScalarAsync(cancellationToken);
                duplicates.Add(new StoredDuplicate(message.Channel, message.MessageNumber, existingHash != message.PayloadHash));
            }
        }

        return duplicates;
    }

    private static async Task InsertRejectionsAsync(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<RejectedMessage> rejections, CancellationToken cancellationToken)
    {
        if (rejections.Count == 0)
        {
            return;
        }

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO rejected_messages (received_at, reason, body) VALUES ($receivedAt, $reason, $body)";
        var receivedAt = insert.Parameters.Add("$receivedAt", SqliteType.Text);
        var reason = insert.Parameters.Add("$reason", SqliteType.Text);
        var body = insert.Parameters.Add("$body", SqliteType.Text);

        foreach (var rejection in rejections)
        {
            receivedAt.Value = FormatTime(rejection.ReceivedAt);
            reason.Value = rejection.Reason;
            body.Value = rejection.Body;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async IAsyncEnumerable<RocketMessage> ReadMessagesAsync(
        string sql, string? channel, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (channel is not null)
        {
            command.Parameters.AddWithValue("$channel", channel);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var storedChannel = reader.GetString(0);
            var messageNumber = reader.GetInt64(1);
            var result = MessageParser.FromStored(
                storedChannel, messageNumber, ParseTime(reader.GetString(3)), reader.GetString(2), reader.GetString(4));

            yield return result.Message
                ?? throw new InvalidDataException($"Stored message {storedChannel}#{messageNumber} is invalid: {result.Error}");
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Round-trip format keeps the offset, so a message reads back exactly as it was received.
    private static string FormatTime(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.ParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
}
