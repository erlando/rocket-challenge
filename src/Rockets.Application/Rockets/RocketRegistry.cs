using System.Collections.Concurrent;

namespace Rockets.Application.Rockets;

/// <summary>
/// The read side: the latest snapshot of every rocket. The single writer publishes; any number of readers
/// read without locks. Each snapshot is immutable, so a reader always sees a consistent rocket.
/// </summary>
public sealed class RocketRegistry
{
    private readonly ConcurrentDictionary<string, RocketSnapshot> _rockets = new(StringComparer.Ordinal);

    public RocketSnapshot? Find(string channel) => _rockets.GetValueOrDefault(channel);

    /// <summary>A point-in-time copy of all rockets, in no particular order.</summary>
    public IReadOnlyCollection<RocketSnapshot> All() => _rockets.Values.ToArray();

    internal void Publish(RocketSnapshot snapshot) => _rockets[snapshot.Channel] = snapshot;
}
