using Microsoft.Data.Sqlite;
using Rockets.Application.Storage;
using Rockets.Storage.Sqlite;
using static Rockets.Storage.Tests.Messages;

namespace Rockets.Storage.Tests;

/// <summary>Runs the store contract against SQLite on a temporary file, plus SQLite-specific checks.</summary>
public sealed class SqliteMessageStoreTests : MessageStoreContractTests
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rockets-test-{Guid.NewGuid():N}.db");

    protected override async Task<IMessageStore> CreateStoreAsync()
    {
        var store = new SqliteMessageStore(new SqliteStoreOptions(_path));
        await store.InitializeAsync();
        return store;
    }

    public override ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Data_survives_opening_the_database_again()
    {
        await Store.CommitAsync([Launched("a"), SpeedIncreased("a", 2)], [new RejectedMessage(Start, "reason", "body")]);

        var reopened = new SqliteMessageStore(new SqliteStoreOptions(_path, SqliteSynchronous.Normal));
        await reopened.InitializeAsync();

        Assert.Equal(await Store.ReadAllAsync().ToListAsync(), await reopened.ReadAllAsync().ToListAsync());
        Assert.Single(await reopened.ReadRejectedAsync().ToListAsync());
    }

    [Theory]
    [InlineData(SqliteSynchronous.Full, 2)]
    [InlineData(SqliteSynchronous.Normal, 1)]
    public async Task Connections_use_WAL_and_the_configured_synchronous_level(SqliteSynchronous synchronous, long expectedPragmaValue)
    {
        var store = new SqliteMessageStore(new SqliteStoreOptions(_path, synchronous));
        await store.InitializeAsync();

        await using var connection = await store.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode; PRAGMA synchronous;";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.Equal("wal", reader.GetString(0));
        await reader.NextResultAsync();
        await reader.ReadAsync();
        Assert.Equal(expectedPragmaValue, reader.GetInt64(0));
    }
}
