using Rockets.Application.Rockets;

namespace Rockets.Api.Rockets;

/// <summary>
/// Sorting for the rocket list. Field and order names are case-insensitive. Rockets without a value for the field
/// (e.g. no type before the launch message arrives) sort last in either order, and ties are broken by channel.
/// </summary>
public static class RocketSorting
{
    private delegate IOrderedEnumerable<RocketSnapshot> Sorter(IEnumerable<RocketSnapshot> rockets, bool descending);

    private static readonly Dictionary<string, Sorter> Sorters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["channel"] = (rockets, descending) => By(rockets, r => r.Channel, descending, StringComparer.Ordinal),
        ["type"] = (rockets, descending) => By(rockets, r => r.State.Type, descending, StringComparer.Ordinal),
        ["mission"] = (rockets, descending) => By(rockets, r => r.State.Mission, descending, StringComparer.Ordinal),
        ["speed"] = (rockets, descending) => By(rockets, r => r.State.Speed, descending),
        // Status sorts in lifecycle order: awaitingLaunch, launched, exploded.
        ["status"] = (rockets, descending) => By(rockets, r => r.State.Status, descending),
        ["launchedAt"] = (rockets, descending) => By(rockets, r => r.State.LaunchedAt, descending),
        ["updatedAt"] = (rockets, descending) => By(rockets, r => r.State.UpdatedAt, descending),
    };

    public static string ValidFields => string.Join(", ", Sorters.Keys);

    /// <summary>Validates the query parameters. Defaults: sortBy=channel, order=asc.</summary>
    public static bool TryCreate(
        string? sortBy,
        string? order,
        out Func<IEnumerable<RocketSnapshot>, IEnumerable<RocketSnapshot>> sort,
        out string? error)
    {
        sort = rockets => rockets;
        if (!Sorters.TryGetValue(sortBy ?? "channel", out var sorter))
        {
            error = $"sortBy must be one of: {ValidFields}.";
            return false;
        }

        bool descending;
        switch (order?.ToLowerInvariant())
        {
            case null or "asc":
                descending = false;
                break;
            case "desc":
                descending = true;
                break;
            default:
                error = "order must be asc or desc.";
                return false;
        }

        sort = rockets => sorter(rockets, descending);
        error = null;
        return true;
    }

    private static IOrderedEnumerable<RocketSnapshot> By<TKey>(
        IEnumerable<RocketSnapshot> rockets, Func<RocketSnapshot, TKey> key, bool descending, IComparer<TKey>? comparer = null)
    {
        var valuesFirst = rockets.OrderBy(r => key(r) is null);
        var sorted = descending ? valuesFirst.ThenByDescending(key, comparer) : valuesFirst.ThenBy(key, comparer);
        return sorted.ThenBy(r => r.Channel, StringComparer.Ordinal);
    }
}
