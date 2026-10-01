namespace Rockets.Application.Ingestion;

/// <summary>How an accepted request ended. Every outcome is answered with 2xx; the message never needs to be resent.</summary>
public enum IngestionOutcome
{
    /// <summary>The message was stored and applied.</summary>
    Stored,

    /// <summary>The message had already been received; nothing changed.</summary>
    Duplicate,

    /// <summary>The body was invalid or could not be applied. It was recorded in the rejected messages.</summary>
    Rejected,
}

/// <summary>The message could not be accepted right now (storage failure, full queue, shutdown). Answered with 503, so it is resent.</summary>
public sealed class IngestionUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
