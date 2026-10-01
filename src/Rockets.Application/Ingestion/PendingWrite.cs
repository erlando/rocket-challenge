using Rockets.Domain.Messages;

namespace Rockets.Application.Ingestion;

/// <summary>A request waiting for the writer: the parsed message (or why it couldn't be parsed) and the request's completion.</summary>
internal sealed class PendingWrite(string body, DateTimeOffset receivedAt, RocketMessage? message, string? parseError)
{
    // Continuations run asynchronously so completing a request never runs request code on the writer's thread.
    private readonly TaskCompletionSource<IngestionOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Body { get; } = body;

    public DateTimeOffset ReceivedAt { get; } = receivedAt;

    public RocketMessage? Message { get; } = message;

    public string? ParseError { get; } = parseError;

    public Task<IngestionOutcome> Completion => _completion.Task;

    public void Complete(IngestionOutcome outcome) => _completion.TrySetResult(outcome);

    public void Fail(Exception exception) => _completion.TrySetException(exception);
}
