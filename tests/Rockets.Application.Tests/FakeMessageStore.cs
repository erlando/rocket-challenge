using System.Runtime.CompilerServices;
using Rockets.Application.Storage;
using Rockets.Domain.Messages;

namespace Rockets.Application.Tests;

/// <summary>
/// An in-memory store with controls for the writer tests: hold commits to build up a batch,
/// fail them, or commit and then throw (a commit whose outcome looks like a failure).
/// </summary>
public sealed class FakeMessageStore : IMessageStore
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<(string Channel, long Number), RocketMessage> _messages = new();
    private readonly List<RejectedMessage> _rejected = [];
    private readonly List<IReadOnlyList<RocketMessage>> _batches = [];
    private readonly SemaphoreSlim _commitsStarted = new(0);
    private TaskCompletionSource _hold = Completed();

    /// <summary>Every commit throws before writing anything.</summary>
    public bool FailCommits { get; set; }

    /// <summary>Reading a channel throws, so a reload after a failure can't succeed.</summary>
    public bool FailReads { get; set; }

    /// <summary>The next commit writes everything and then throws, as if the outcome were lost.</summary>
    public bool ThrowAfterNextCommit { get; set; }

    public IReadOnlyList<RocketMessage> Messages { get { lock (_gate) { return _messages.Values.ToList(); } } }

    public IReadOnlyList<RejectedMessage> Rejected { get { lock (_gate) { return _rejected.ToList(); } } }

    /// <summary>The messages of each attempted commit, in order.</summary>
    public IReadOnlyList<IReadOnlyList<RocketMessage>> Batches { get { lock (_gate) { return _batches.ToList(); } } }

    /// <summary>Makes commits wait until <see cref="Release"/>.</summary>
    public void Hold() => _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _hold.TrySetResult();

    /// <summary>Waits until a commit has started (and is possibly being held).</summary>
    public async Task WaitForCommitAsync()
    {
        if (!await _commitsStarted.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("No commit started.");
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<CommitResult> CommitAsync(
        IReadOnlyList<RocketMessage> messages, IReadOnlyList<RejectedMessage> rejections, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _batches.Add(messages.ToList());
        }
        _commitsStarted.Release();
        await _hold.Task;

        if (FailCommits)
        {
            throw new IOException("Simulated storage failure.");
        }

        var duplicates = new List<StoredDuplicate>();
        lock (_gate)
        {
            foreach (var message in messages)
            {
                if (!_messages.TryAdd((message.Channel, message.MessageNumber), message))
                {
                    var stored = _messages[(message.Channel, message.MessageNumber)];
                    duplicates.Add(new StoredDuplicate(message.Channel, message.MessageNumber, stored.PayloadHash != message.PayloadHash));
                }
            }
            _rejected.AddRange(rejections);
        }

        if (ThrowAfterNextCommit)
        {
            ThrowAfterNextCommit = false;
            throw new IOException("Simulated failure after the commit succeeded.");
        }

        return new CommitResult(duplicates);
    }

    public async IAsyncEnumerable<RocketMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var message in Messages)
        {
            yield return message;
        }
    }

    public async IAsyncEnumerable<RocketMessage> ReadChannelAsync(string channel, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        if (FailReads)
        {
            throw new IOException("Simulated read failure.");
        }
        foreach (var message in Messages.Where(m => m.Channel == channel))
        {
            yield return message;
        }
    }

    public async IAsyncEnumerable<RejectedMessage> ReadRejectedAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        foreach (var rejection in Rejected)
        {
            yield return rejection;
        }
    }

    public Task<long> ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            long deleted = _messages.Count;
            _messages.Clear();
            _rejected.Clear();
            return Task.FromResult(deleted);
        }
    }

    private static TaskCompletionSource Completed()
    {
        var completed = new TaskCompletionSource();
        completed.SetResult();
        return completed;
    }
}
