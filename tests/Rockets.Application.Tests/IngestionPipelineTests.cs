using Microsoft.Extensions.Logging.Abstractions;
using Rockets.Application.Ingestion;
using Rockets.Application.Rockets;
using Rockets.Domain;
using static Rockets.Application.Tests.Bodies;

namespace Rockets.Application.Tests;

public sealed class IngestionPipelineTests : IAsyncLifetime
{
    private readonly FakeMessageStore _store = new();
    private readonly RocketRegistry _registry = new();
    private readonly IngestionStats _stats = new();
    private IngestionPipeline _pipeline = null!;

    public ValueTask InitializeAsync() => StartAsync(new IngestionOptions());

    public async ValueTask DisposeAsync()
    {
        _store.Release();
        await _pipeline.StopAsync();
    }

    private async ValueTask StartAsync(IngestionOptions options)
    {
        _pipeline = new IngestionPipeline(_store, _registry, _stats, options, NullLogger<IngestionPipeline>.Instance, TimeProvider.System);
        await _pipeline.StartAsync();
    }

    private async Task RestartAsync(IngestionOptions options)
    {
        await _pipeline.StopAsync();
        await StartAsync(options);
    }

    private RocketSnapshot Rocket(string channel = Channel) =>
        _registry.Find(channel) ?? throw new InvalidOperationException($"Rocket {channel} is not in the registry.");

    [Fact]
    public async Task A_stored_message_is_committed_before_the_request_completes_and_then_visible()
    {
        var outcome = await _pipeline.SubmitAsync(Launched(1, launchSpeed: 500));

        Assert.Equal(IngestionOutcome.Stored, outcome);
        Assert.Single(_store.Messages);
        Assert.Equal(500, Rocket().State.Speed);
        Assert.Equal(1, Rocket().CheckpointMessageNumber);
        Assert.Equal(1, _stats.Stored);
    }

    [Fact]
    public async Task A_redelivered_message_is_a_duplicate_and_changes_nothing()
    {
        await _pipeline.SubmitAsync(Launched(1));
        await _pipeline.SubmitAsync(SpeedIncreased(2, by: 100));

        var outcome = await _pipeline.SubmitAsync(SpeedIncreased(2, by: 100));

        Assert.Equal(IngestionOutcome.Duplicate, outcome);
        Assert.Equal(600, Rocket().State.Speed);
        Assert.Equal(2, _store.Messages.Count);
        Assert.Equal(1, _stats.Duplicates);
        Assert.Equal(0, _stats.PayloadMismatches);
    }

    [Fact]
    public async Task Concurrent_posts_to_one_rocket_end_in_the_in_order_state()
    {
        var bodies = new List<string> { Launched(1) };
        var random = new Random(7);
        for (var number = 2; number <= 300; number++)
        {
            bodies.Add(random.Next(2) == 0 ? SpeedIncreased(number, random.Next(1000)) : SpeedDecreased(number, random.Next(1000)));
        }
        var shuffled = bodies.ToArray();
        random.Shuffle(shuffled);

        var outcomes = await Task.WhenAll(shuffled.Select(body => Task.Run(() => _pipeline.SubmitAsync(body))));

        Assert.All(outcomes, outcome => Assert.Equal(IngestionOutcome.Stored, outcome));
        Assert.Equal(FoldInOrder(bodies), Rocket().State);
        Assert.True(Rocket().IsComplete);
        Assert.Equal(300, _store.Messages.Count);
    }

    [Fact]
    public async Task Requests_queued_during_a_commit_are_committed_together_and_a_duplicate_among_them_is_detected()
    {
        _store.Hold();
        var first = _pipeline.SubmitAsync(Launched(1));
        await _store.WaitForCommitAsync();

        var second = _pipeline.SubmitAsync(SpeedIncreased(2));
        var redelivered = _pipeline.SubmitAsync(SpeedIncreased(2));
        _store.Release();

        Assert.Equal(IngestionOutcome.Stored, await first);
        Assert.Equal(IngestionOutcome.Stored, await second);
        Assert.Equal(IngestionOutcome.Duplicate, await redelivered);
        Assert.Equal(2, _store.Batches.Count);
        Assert.Equal(2, _store.Messages.Count);
        Assert.Equal(600, Rocket().State.Speed);
    }

    [Fact]
    public async Task A_bad_message_is_rejected_on_its_own_and_the_rest_of_its_batch_is_stored()
    {
        _store.Hold();
        var launched = _pipeline.SubmitAsync(Launched(1, launchSpeed: 500));
        await _store.WaitForCommitAsync();

        var invalid = _pipeline.SubmitAsync("{ not json");
        var good = _pipeline.SubmitAsync(SpeedIncreased(2, by: 100));
        var overflow = _pipeline.SubmitAsync(SpeedIncreased(3, by: long.MaxValue));
        var goodAfterGap = _pipeline.SubmitAsync(SpeedIncreased(4, by: 10));
        _store.Release();

        Assert.Equal(IngestionOutcome.Stored, await launched);
        Assert.Equal(IngestionOutcome.Rejected, await invalid);
        Assert.Equal(IngestionOutcome.Stored, await good);
        Assert.Equal(IngestionOutcome.Rejected, await overflow);
        Assert.Equal(IngestionOutcome.Stored, await goodAfterGap);

        Assert.Equal([1L, 2L, 4L], _store.Messages.Select(m => m.MessageNumber));
        Assert.Equal(2, _store.Rejected.Count);
        Assert.Contains("invalid JSON", _store.Rejected[0].Reason);
        Assert.Equal("{ not json", _store.Rejected[0].Body);
        Assert.Contains("overflow", _store.Rejected[1].Reason, StringComparison.OrdinalIgnoreCase);
        // The rejected #3 leaves a gap: the checkpoint stops at 2, and #4 shows in the current state.
        Assert.Equal(2, Rocket().CheckpointMessageNumber);
        Assert.Equal(610, Rocket().State.Speed);
        Assert.Equal(1, Rocket().MissingMessageCount);
        Assert.Equal(2, _stats.Rejected);
    }

    [Fact]
    public async Task A_storage_failure_fails_the_requests_and_leaves_the_snapshots_unchanged()
    {
        await _pipeline.SubmitAsync(Launched(1, launchSpeed: 500));
        _store.FailCommits = true;

        await Assert.ThrowsAsync<IngestionUnavailableException>(() => _pipeline.SubmitAsync(SpeedIncreased(2, by: 100)));

        Assert.Equal(500, Rocket().State.Speed);
        Assert.Equal(1, _stats.StoreFailures);

        _store.FailCommits = false;
        Assert.Equal(IngestionOutcome.Stored, await _pipeline.SubmitAsync(SpeedIncreased(2, by: 100)));
        Assert.Equal(600, Rocket().State.Speed);
    }

    [Fact]
    public async Task After_a_commit_that_succeeded_but_reported_failure_memory_is_reloaded_from_the_store()
    {
        await _pipeline.SubmitAsync(Launched(1, launchSpeed: 500));
        _store.ThrowAfterNextCommit = true;

        await Assert.ThrowsAsync<IngestionUnavailableException>(() => _pipeline.SubmitAsync(SpeedIncreased(2, by: 100)));

        // The message did reach the store, so memory must show it too.
        Assert.Equal(600, Rocket().State.Speed);
        Assert.Equal(2, Rocket().CheckpointMessageNumber);
        Assert.Equal(IngestionOutcome.Duplicate, await _pipeline.SubmitAsync(SpeedIncreased(2, by: 100)));
    }

    [Fact]
    public async Task A_rocket_that_could_not_be_reloaded_is_reloaded_before_its_next_message()
    {
        await _pipeline.SubmitAsync(Launched(1, launchSpeed: 500));
        // The commit succeeds and then throws, and the reload fails too: memory is now behind the store.
        _store.ThrowAfterNextCommit = true;
        _store.FailReads = true;
        await Assert.ThrowsAsync<IngestionUnavailableException>(() => _pipeline.SubmitAsync(SpeedIncreased(2, by: 100)));
        _store.FailReads = false;

        // Without a retried reload, #3 would wait above a gap at #2 that will never be resent.
        Assert.Equal(IngestionOutcome.Stored, await _pipeline.SubmitAsync(SpeedIncreased(3, by: 10)));
        Assert.Equal(610, Rocket().State.Speed);
        Assert.Equal(3, Rocket().CheckpointMessageNumber);
    }

    [Fact]
    public async Task While_a_rocket_cannot_be_reloaded_its_messages_are_refused_and_other_rockets_carry_on()
    {
        await _pipeline.SubmitAsync(Launched(1, launchSpeed: 500));
        _store.ThrowAfterNextCommit = true;
        _store.FailReads = true;
        await Assert.ThrowsAsync<IngestionUnavailableException>(() => _pipeline.SubmitAsync(SpeedIncreased(2, by: 100)));

        // Applying #3 to the out-of-date state would be wrong, so it is refused (and will be resent).
        await Assert.ThrowsAsync<IngestionUnavailableException>(() => _pipeline.SubmitAsync(SpeedIncreased(3, by: 10)));
        Assert.Equal(IngestionOutcome.Stored, await _pipeline.SubmitAsync(Launched(1, channel: "other")));
        Assert.Equal(500, Rocket().State.Speed);
    }

    [Fact]
    public async Task Payload_mismatches_are_counted_whether_the_original_is_applied_or_pending()
    {
        await _pipeline.SubmitAsync(Launched(1));
        await _pipeline.SubmitAsync(SpeedIncreased(3, by: 100));

        Assert.Equal(IngestionOutcome.Duplicate, await _pipeline.SubmitAsync(Launched(1, launchSpeed: 999)));
        Assert.Equal(IngestionOutcome.Duplicate, await _pipeline.SubmitAsync(SpeedIncreased(3, by: 999)));

        Assert.Equal(2, _stats.PayloadMismatches);
        Assert.Equal(600, Rocket().State.Speed);
    }

    [Fact]
    public async Task A_client_that_disconnects_mid_write_does_not_stop_the_message_being_stored()
    {
        using var requestAborted = new CancellationTokenSource();
        _store.Hold();

        var submit = _pipeline.SubmitAsync(Launched(1), requestAborted.Token);
        await _store.WaitForCommitAsync();
        await requestAborted.CancelAsync();
        _store.Release();

        Assert.Equal(IngestionOutcome.Stored, await submit);
        Assert.Single(_store.Messages);
    }

    [Fact]
    public async Task When_the_queue_stays_full_a_request_gives_up_after_the_timeout()
    {
        await RestartAsync(new IngestionOptions { QueueCapacity = 1, EnqueueTimeout = TimeSpan.FromMilliseconds(200) });
        _store.Hold();
        var inCommit = _pipeline.SubmitAsync(Launched(1));
        await _store.WaitForCommitAsync();
        var queued = _pipeline.SubmitAsync(SpeedIncreased(2));

        var exception = await Assert.ThrowsAsync<IngestionUnavailableException>(() => _pipeline.SubmitAsync(SpeedIncreased(3)));

        Assert.Contains("queue", exception.Message);
        _store.Release();
        Assert.Equal(IngestionOutcome.Stored, await inCommit);
        Assert.Equal(IngestionOutcome.Stored, await queued);
    }

    [Fact]
    public async Task Stopping_drains_the_queue_and_refuses_new_messages()
    {
        _store.Hold();
        var inCommit = _pipeline.SubmitAsync(Launched(1));
        await _store.WaitForCommitAsync();
        var queued = new[] { _pipeline.SubmitAsync(SpeedIncreased(2)), _pipeline.SubmitAsync(SpeedIncreased(3)) };

        var stopping = _pipeline.StopAsync();
        await Assert.ThrowsAsync<IngestionUnavailableException>(() => _pipeline.SubmitAsync(SpeedIncreased(4)));
        _store.Release();
        await stopping;

        Assert.Equal(IngestionOutcome.Stored, await inCommit);
        Assert.All(await Task.WhenAll(queued), outcome => Assert.Equal(IngestionOutcome.Stored, outcome));
        Assert.Equal(3, _store.Messages.Count);
    }

    [Fact]
    public async Task Starting_replays_the_stored_log_into_the_registry()
    {
        string[] bodies = [Launched(1, launchSpeed: 500), SpeedIncreased(2, by: 100), SpeedIncreased(4, by: 10), Launched(1, channel: "other")];
        foreach (var body in bodies)
        {
            await _pipeline.SubmitAsync(body);
        }
        var registry = new RocketRegistry();

        var restarted = new IngestionPipeline(_store, registry, new IngestionStats(), new IngestionOptions(),
            NullLogger<IngestionPipeline>.Instance, TimeProvider.System);
        await restarted.StartAsync();
        await restarted.StopAsync();

        Assert.Equal(2, registry.All().Count);
        Assert.Equal(_registry.Find(Channel), registry.Find(Channel));
        Assert.Equal(610, registry.Find(Channel)!.State.Speed);
        Assert.Equal(2, registry.Find(Channel)!.CheckpointMessageNumber);
        Assert.Equal(RocketStatus.Launched, registry.Find("other")!.State.Status);
    }
}
