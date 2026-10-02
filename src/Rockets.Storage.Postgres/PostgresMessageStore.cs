using System.Globalization;
using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using Rockets.Application.Storage;
using Rockets.Domain.Messages;

namespace Rockets.Storage.Postgres;

public sealed record PostgresStoreOptions(string ConnectionString);

/// <summary>
/// The message log in Postgres, behind the same <see cref="IMessageStore"/> contract as SQLite.
/// Connections come from a pooled <see cref="NpgsqlDataSource"/>. Unlike SQLite, Postgres allows concurrent
/// writers, so several service instances could share it if each owned its own channels (see README, Scaling).
/// </summary>
public sealed class PostgresMessageStore : IMessageStore, IAsyncDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS messages (
            channel        TEXT        NOT NULL,
            message_number BIGINT      NOT NULL,
            message_type   TEXT        NOT NULL,
            -- Text in round-trip format, as in SQLite: timestamptz would drop the offset and keep only microseconds,
            -- and a message must read back exactly as it was received.
            message_time   TEXT        NOT NULL,
            -- Text, not jsonb: jsonb normalises the JSON, which would change the content behind payload_hash.
            payload_json   TEXT        NOT NULL,
            payload_hash   TEXT        NOT NULL,
            received_at    TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (channel, message_number)
        );

        CREATE TABLE IF NOT EXISTS rejected_messages (
            id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            received_at TIMESTAMPTZ NOT NULL,
            reason      TEXT        NOT NULL,
            body        TEXT        NOT NULL
        );
        """;

    private const string SelectMessages =
        "SELECT channel, message_number, message_type, message_time, payload_json FROM messages";

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;

    public PostgresMessageStore(PostgresStoreOptions options, TimeProvider? time = null)
    {
        _dataSource = NpgsqlDataSource.Create(options.ConnectionString);
        _time = time ?? TimeProvider.System;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(Schema);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        // Disposing the transaction without committing rolls it back, so an exception anywhere leaves nothing behind.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var duplicates = await InsertMessagesAsync(connection, transaction, messages, cancellationToken);
        await InsertRejectionsAsync(connection, transaction, rejections, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return duplicates.Count == 0 ? CommitResult.Empty : new CommitResult(duplicates);
    }

    // COLLATE "C" orders channels by byte value, the same as SQLite, whatever the database's locale.
    public IAsyncEnumerable<RocketMessage> ReadAllAsync(CancellationToken cancellationToken = default) =>
        ReadMessagesAsync($"{SelectMessages} ORDER BY channel COLLATE \"C\", message_number", channel: null, cancellationToken);

    public IAsyncEnumerable<RocketMessage> ReadChannelAsync(string channel, CancellationToken cancellationToken = default) =>
        ReadMessagesAsync($"{SelectMessages} WHERE channel = $1 ORDER BY message_number", channel, cancellationToken);

    public async IAsyncEnumerable<RejectedMessage> ReadRejectedAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("SELECT received_at, reason, body FROM rejected_messages ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            yield return new RejectedMessage(
                reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetString(2));
        }
    }

    public async Task<long> ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var rejections = new NpgsqlCommand("DELETE FROM rejected_messages", connection, transaction))
        {
            await rejections.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var messages = new NpgsqlCommand("DELETE FROM messages", connection, transaction);
        long deleted = await messages.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    private async Task<List<StoredDuplicate>> InsertMessagesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<RocketMessage> messages, CancellationToken cancellationToken)
    {
        var duplicates = new List<StoredDuplicate>();
        if (messages.Count == 0)
        {
            return duplicates;
        }

        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO messages (channel, message_number, message_type, message_time, payload_json, payload_hash, received_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (channel, message_number) DO NOTHING
            """,
            connection,
            transaction);
        var channel = insert.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text });
        var number = insert.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint });
        var type = insert.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text });
        var time = insert.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text });
        var payload = insert.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text });
        var hash = insert.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset>
        {
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
            TypedValue = _time.GetUtcNow(),
        });

        await using var storedHash = new NpgsqlCommand(
            "SELECT payload_hash FROM messages WHERE channel = $1 AND message_number = $2", connection, transaction);
        var storedChannel = storedHash.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text });
        var storedNumber = storedHash.Parameters.Add(new NpgsqlParameter<long> { NpgsqlDbType = NpgsqlDbType.Bigint });

        foreach (var message in messages)
        {
            channel.Value = message.Channel;
            number.Value = message.MessageNumber;
            type.Value = message.MessageType;
            time.Value = message.MessageTime.ToString("O", CultureInfo.InvariantCulture);
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
        NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<RejectedMessage> rejections, CancellationToken cancellationToken)
    {
        if (rejections.Count == 0)
        {
            return;
        }

        await using var insert = new NpgsqlCommand(
            "INSERT INTO rejected_messages (received_at, reason, body) VALUES ($1, $2, $3)", connection, transaction);
        var receivedAt = insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { NpgsqlDbType = NpgsqlDbType.TimestampTz });
        var reason = insert.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text });
        var body = insert.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text });

        foreach (var rejection in rejections)
        {
            // timestamptz stores an instant; Npgsql only accepts it as UTC.
            receivedAt.Value = rejection.ReceivedAt.ToUniversalTime();
            reason.Value = rejection.Reason;
            body.Value = rejection.Body;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async IAsyncEnumerable<RocketMessage> ReadMessagesAsync(
        string sql, string? channel, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(sql);
        if (channel is not null)
        {
            command.Parameters.Add(new NpgsqlParameter<string> { NpgsqlDbType = NpgsqlDbType.Text, TypedValue = channel });
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var storedChannel = reader.GetString(0);
            var messageNumber = reader.GetInt64(1);
            var messageTime = DateTimeOffset.ParseExact(reader.GetString(3), "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
            var result = MessageParser.FromStored(storedChannel, messageNumber, messageTime, reader.GetString(2), reader.GetString(4));

            yield return result.Message
                ?? throw new InvalidDataException($"Stored message {storedChannel}#{messageNumber} is invalid: {result.Error}");
        }
    }
}
