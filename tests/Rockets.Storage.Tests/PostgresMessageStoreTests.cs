using Npgsql;
using Rockets.Application.Storage;
using Rockets.Storage.Postgres;
using Testcontainers.PostgreSql;

namespace Rockets.Storage.Tests;

/// <summary>
/// One Postgres container for the whole test class. Each test gets its own empty database in it.
/// Without Docker, the tests are skipped with the reason instead of failing.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? UnavailableReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:18-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception e)
        {
            UnavailableReason = $"Postgres contract tests need Docker, which is not available: {e.GetType().Name}: {e.Message}";
        }
    }

    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"rockets_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(_container!.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText = $"CREATE DATABASE {name}";
            await command.ExecuteNonQueryAsync();
        }
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>Runs the store contract against Postgres: the same tests as SQLite, so the stores are interchangeable.</summary>
public sealed class PostgresMessageStoreTests(PostgresFixture postgres) : MessageStoreContractTests, IClassFixture<PostgresFixture>
{
    private PostgresMessageStore? _store;

    protected override async Task<IMessageStore> CreateStoreAsync()
    {
        if (postgres.UnavailableReason is { } reason)
        {
            Assert.Skip(reason);
        }

        _store = new PostgresMessageStore(new PostgresStoreOptions(await postgres.CreateDatabaseAsync()));
        await _store.InitializeAsync();
        return _store;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
    }
}
