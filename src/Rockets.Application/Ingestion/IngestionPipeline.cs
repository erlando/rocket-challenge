using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Rockets.Application.Rockets;
using Rockets.Application.Storage;
using Rockets.Domain;
using Rockets.Domain.Messages;

namespace Rockets.Application.Ingestion;

/// <summary>
/// Accepts messages from any number of requests and hands them to a single writer, which applies them to the
/// rockets' ledgers, commits them in batches and publishes the new snapshots. A request completes only after
/// its message is committed.
/// </summary>
/// <remarks>
/// Only the writer loop touches <see cref="_ledgers"/> and <see cref="_staleChannels"/>, so they need no locks.
/// Ledgers are immutable: a batch is applied to working copies, which replace the real ones only after the commit.
/// </remarks>
public sealed class IngestionPipeline
{
    private readonly IMessageStore _store;
    private readonly RocketRegistry _registry;
    private readonly IngestionStats _stats;
    private readonly IngestionOptions _options;
    private readonly ILogger<IngestionPipeline> _logger;
    private readonly TimeProvider _time;
    private readonly Channel<PendingWrite> _queue;
    private readonly Dictionary<string, RocketLedger> _ledgers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _staleChannels = new(StringComparer.Ordinal);
    private Task? _writer;

    public IngestionPipeline(
        IMessageStore store,
        RocketRegistry registry,
        IngestionStats stats,
        IngestionOptions options,
        ILogger<IngestionPipeline> logger,
        TimeProvider time)
    {
        _store = store;
        _registry = registry;
        _stats = stats;
        _options = options;
        _logger = logger;
        _time = time;
        _queue = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>Initializes the store, replays the log into the registry, then starts the writer.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_writer is not null)
        {
            throw new InvalidOperationException("The pipeline has already been started.");
        }

        await _store.InitializeAsync(cancellationToken);
        var replayed = 0;
        await foreach (var message in _store.ReadAllAsync(cancellationToken))
        {
            _ledgers[message.Channel] = LedgerFor(message.Channel).Apply(message).Ledger;
            replayed++;
        }
        foreach (var ledger in _ledgers.Values)
        {
            _registry.Publish(RocketSnapshot.From(ledger));
        }
        _logger.LogInformation("Replayed {MessageCount} stored messages into {RocketCount} rockets", replayed, _ledgers.Count);

        _writer = Task.Run(RunWriterAsync, CancellationToken.None);
    }

    /// <summary>Stops accepting messages and waits until everything already queued is committed.</summary>
    public async Task StopAsync()
    {
        _queue.Writer.TryComplete();
        if (_writer is not null)
        {
            await _writer;
        }
    }

    /// <summary>
    /// Submits a request body. Returns once the outcome is durable.
    /// <paramref name="requestAborted"/> only cancels waiting for room in the queue, never a write in progress.
    /// </summary>
    /// <exception cref="IngestionUnavailableException">Storage failed, the queue stayed full, or the pipeline is stopping.</exception>
    public async Task<IngestionOutcome> SubmitAsync(string body, CancellationToken requestAborted = default)
    {
        var receivedAt = _time.GetUtcNow();
        var parsed = MessageParser.Parse(body);
        var write = new PendingWrite(body, receivedAt, parsed.Message, parsed.Error);

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(requestAborted))
        {
            timeout.CancelAfter(_options.EnqueueTimeout);
            try
            {
                await _queue.Writer.WriteAsync(write, timeout.Token);
            }
            catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested)
            {
                throw new IngestionUnavailableException(
                    $"The write queue stayed full for {_options.EnqueueTimeout.TotalSeconds:0.#} s.");
            }
            catch (ChannelClosedException e)
            {
                throw new IngestionUnavailableException("The service is shutting down.", e);
            }
        }

        return await write.Completion;
    }

    private async Task RunWriterAsync()
    {
        var batch = new List<PendingWrite>(_options.MaxBatchSize);
        // No cancellation token: the loop ends only when the queue is completed and drained, so stopping never drops a write.
        while (await _queue.Reader.WaitToReadAsync())
        {
            batch.Clear();
            // Take whatever is queued, up to the batch limit. Never wait for a batch to fill up.
            while (batch.Count < _options.MaxBatchSize && _queue.Reader.TryRead(out var write))
            {
                batch.Add(write);
            }

            try
            {
                await ProcessBatchAsync(batch);
            }
            catch (Exception e)
            {
                // A bug must not stop the writer, or every later request would wait forever.
                _logger.LogError(e, "Unexpected error while processing a batch of {Count} requests", batch.Count);
                var unavailable = new IngestionUnavailableException("The message could not be processed.", e);
                batch.ForEach(write => write.Fail(unavailable));
            }
        }
    }

    private async Task ProcessBatchAsync(List<PendingWrite> batch)
    {
        await ReloadStaleChannelsAsync();

        var working = new Dictionary<string, RocketLedger>(StringComparer.Ordinal);
        var toStore = new List<RocketMessage>();
        var rejections = new List<RejectedMessage>();
        var outcomes = new IngestionOutcome?[batch.Count];

        for (var i = 0; i < batch.Count; i++)
        {
            var write = batch[i];
            if (write.Message is not { } message)
            {
                rejections.Add(new RejectedMessage(write.ReceivedAt, write.ParseError!, write.Body));
                outcomes[i] = IngestionOutcome.Rejected;
                continue;
            }

            if (_staleChannels.Contains(message.Channel))
            {
                // Memory may be behind the store for this rocket; leave the outcome open so the request fails and is resent.
                continue;
            }

            var ledger = working.GetValueOrDefault(message.Channel) ?? LedgerFor(message.Channel);
            LedgerResult result;
            try
            {
                result = ledger.Apply(message);
            }
            catch (Exception e)
            {
                // A message that can't be applied (e.g. speed overflow) is rejected on its own; the batch goes on.
                rejections.Add(new RejectedMessage(write.ReceivedAt, $"could not be applied: {e.Message}", write.Body));
                outcomes[i] = IngestionOutcome.Rejected;
                continue;
            }

            if (result.Outcome == LedgerOutcome.Duplicate)
            {
                outcomes[i] = IngestionOutcome.Duplicate;
                if (result.PayloadMismatch)
                {
                    RecordPayloadMismatch(message.Channel, message.MessageNumber);
                }
                else if (message.MessageNumber <= ledger.CheckpointNumber)
                {
                    // Applied messages aren't kept in memory, so the store compares their content (it won't store them twice).
                    toStore.Add(message);
                }
                continue;
            }

            working[message.Channel] = result.Ledger;
            toStore.Add(message);
            outcomes[i] = IngestionOutcome.Stored;
        }

        CommitResult committed;
        try
        {
            committed = await _store.CommitAsync(toStore, rejections, CancellationToken.None);
        }
        catch (Exception e)
        {
            _stats.AddStoreFailure();
            _logger.LogError(e, "Committing a batch of {Count} requests failed", batch.Count);
            // The commit may have succeeded before the error surfaced, so memory is reloaded from the store
            // before anyone is answered. Rockets that can't be reloaded now are retried before the next batch.
            foreach (var message in toStore)
            {
                _staleChannels.Add(message.Channel);
            }
            await ReloadStaleChannelsAsync();
            var unavailable = new IngestionUnavailableException("The message could not be stored.", e);
            batch.ForEach(write => write.Fail(unavailable));
            return;
        }

        _stats.AddCommit();
        foreach (var (channel, ledger) in working)
        {
            _ledgers[channel] = ledger;
            _registry.Publish(RocketSnapshot.From(ledger));
        }
        foreach (var duplicate in committed.Duplicates.Where(d => d.PayloadMismatch))
        {
            RecordPayloadMismatch(duplicate.Channel, duplicate.MessageNumber);
        }

        var stale = new IngestionUnavailableException("The rocket's state is being reloaded after a storage error.");
        for (var i = 0; i < batch.Count; i++)
        {
            if (outcomes[i] is { } outcome)
            {
                _stats.Add(outcome);
                batch[i].Complete(outcome);
            }
            else
            {
                batch[i].Fail(stale);
            }
        }
    }

    private async Task ReloadStaleChannelsAsync()
    {
        foreach (var channel in _staleChannels.ToList())
        {
            try
            {
                var ledger = RocketLedger.Empty(channel);
                await foreach (var message in _store.ReadChannelAsync(channel))
                {
                    ledger = ledger.Apply(message).Ledger;
                }

                _staleChannels.Remove(channel);
                if (ledger.LastMessageNumber > 0)
                {
                    _ledgers[channel] = ledger;
                    _registry.Publish(RocketSnapshot.From(ledger));
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Reloading rocket {Channel} failed; it will be retried before the next batch", channel);
            }
        }
    }

    private void RecordPayloadMismatch(string channel, long messageNumber)
    {
        _stats.AddPayloadMismatch();
        _logger.LogWarning(
            "Message {Channel}#{MessageNumber} was redelivered with different content; the first version is kept",
            channel, messageNumber);
    }

    private RocketLedger LedgerFor(string channel) => _ledgers.GetValueOrDefault(channel) ?? RocketLedger.Empty(channel);
}
