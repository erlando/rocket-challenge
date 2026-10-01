using Rockets.Application.Storage;
using Rockets.Domain.Messages;
using static Rockets.Storage.Tests.Messages;

namespace Rockets.Storage.Tests;

/// <summary>
/// The behaviour every <see cref="IMessageStore"/> must have. Each implementation (SQLite now, Postgres later)
/// subclasses this and supplies a fresh, initialized store per test.
/// </summary>
public abstract class MessageStoreContractTests : IAsyncLifetime
{
    private IMessageStore? _store;

    protected IMessageStore Store => _store ?? throw new InvalidOperationException("The store is not initialized.");

    /// <summary>Creates an empty store with its schema initialized.</summary>
    protected abstract Task<IMessageStore> CreateStoreAsync();

    public async ValueTask InitializeAsync() => _store = await CreateStoreAsync();

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static RejectedMessage Rejection(string reason = "message.by must be a non-negative integer") =>
        new(Start, reason, """{"metadata":{},"message":{"by":-1}}""");

    [Fact]
    public async Task Initializing_twice_keeps_the_data()
    {
        await Store.CommitAsync([Launched("a")], []);

        await Store.InitializeAsync();

        Assert.Single(await Store.ReadAllAsync().ToListAsync());
    }

    [Fact]
    public async Task Committed_messages_read_back_equal_to_what_was_written()
    {
        RocketMessage[] messages =
        [
            Launched("a", 1, mission: "MÅNE-1 \"quoted\""),
            SpeedIncreased("a", 2, by: 3000),
            Create("a", 3, "RocketExploded", new { reason = "PRESSURE_VESSEL_FAILURE" }),
            Create("a", 4, "RocketRefuelled", new { litres = 1000 }),
        ];

        var result = await Store.CommitAsync(messages, []);

        Assert.Empty(result.Duplicates);
        Assert.Equal(messages, await Store.ReadAllAsync().ToListAsync());
    }

    [Fact]
    public async Task Read_all_groups_by_channel_in_message_number_order()
    {
        await Store.CommitAsync([SpeedIncreased("b", 3), Launched("a"), SpeedIncreased("b", 2)], []);
        await Store.CommitAsync([Launched("b"), SpeedIncreased("a", 2)], []);

        var read = await Store.ReadAllAsync().ToListAsync();

        Assert.Equal(
            ["a#1", "a#2", "b#1", "b#2", "b#3"],
            read.Select(m => $"{m.Channel}#{m.MessageNumber}"));
    }

    [Fact]
    public async Task Read_channel_returns_only_that_rocket_in_order()
    {
        await Store.CommitAsync([SpeedIncreased("a", 3), Launched("b"), Launched("a"), SpeedIncreased("a", 2)], []);

        var read = await Store.ReadChannelAsync("a").ToListAsync();

        Assert.Equal([1L, 2L, 3L], read.Select(m => m.MessageNumber));
        Assert.All(read, m => Assert.Equal("a", m.Channel));
        Assert.Empty(await Store.ReadChannelAsync("unknown").ToListAsync());
    }

    [Fact]
    public async Task A_duplicate_is_not_stored_again_and_is_reported()
    {
        await Store.CommitAsync([Launched("a"), SpeedIncreased("a", 2, by: 100)], []);

        var result = await Store.CommitAsync([SpeedIncreased("a", 2, by: 100), SpeedIncreased("a", 3)], []);

        Assert.Equal([new StoredDuplicate("a", 2, PayloadMismatch: false)], result.Duplicates);
        Assert.Equal(3, (await Store.ReadAllAsync().ToListAsync()).Count);
    }

    [Fact]
    public async Task A_duplicate_with_different_content_is_flagged_and_the_first_write_wins()
    {
        var original = SpeedIncreased("a", 2, by: 100);
        await Store.CommitAsync([original], []);

        var result = await Store.CommitAsync([SpeedIncreased("a", 2, by: 999)], []);

        Assert.Equal([new StoredDuplicate("a", 2, PayloadMismatch: true)], result.Duplicates);
        Assert.Equal([original], await Store.ReadAllAsync().ToListAsync());
    }

    [Fact]
    public async Task A_duplicate_within_one_batch_is_stored_once_and_reported()
    {
        var result = await Store.CommitAsync([Launched("a"), Launched("a")], []);

        Assert.Equal([new StoredDuplicate("a", 1, PayloadMismatch: false)], result.Duplicates);
        Assert.Single(await Store.ReadAllAsync().ToListAsync());
    }

    [Fact]
    public async Task Rejected_messages_are_stored_in_order()
    {
        await Store.CommitAsync([Launched("a")], [Rejection("first")]);
        await Store.CommitAsync([], [Rejection("second"), Rejection("third")]);

        var rejected = await Store.ReadRejectedAsync().ToListAsync();

        Assert.Equal(["first", "second", "third"], rejected.Select(r => r.Reason));
        Assert.Equal(Rejection("first"), rejected[0]);
    }

    [Fact]
    public async Task A_failed_commit_leaves_nothing_behind()
    {
        await Store.CommitAsync([Launched("a")], []);
        // A message the store can't write (a required column is null) makes the transaction fail partway through.
        var unwritable = SpeedIncreased("a", 3) with { PayloadJson = null! };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Store.CommitAsync([SpeedIncreased("a", 2), unwritable, SpeedIncreased("a", 4)], [Rejection()]));

        Assert.Equal([1L], (await Store.ReadAllAsync().ToListAsync()).Select(m => m.MessageNumber));
        Assert.Empty(await Store.ReadRejectedAsync().ToListAsync());
    }

    [Fact]
    public async Task An_empty_commit_is_allowed()
    {
        var result = await Store.CommitAsync([], []);

        Assert.Empty(result.Duplicates);
    }
}
