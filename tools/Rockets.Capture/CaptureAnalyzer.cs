using System.Text.Json;

namespace Rockets.Capture;

/// <summary>Statistics about a capture, used to confirm or adjust the assumptions in the implementation plan.</summary>
public sealed record CaptureReport
{
    // Delivery behaviour
    public int Deliveries { get; init; }
    public int MalformedDeliveries { get; init; }
    public int UniqueMessages { get; init; }
    public int NeverAcknowledged { get; init; }
    public int RedeliveriesAfterSuccess { get; init; }
    public int RetriesAfterFailure { get; init; }
    public int PayloadMismatches { get; init; }
    public int MaxAttempts { get; init; }
    public TimeSpan? MedianRetryDelay { get; init; }
    public TimeSpan? MaxRetryDelay { get; init; }

    // Ordering, as the service would see acknowledged messages arrive
    public int OutOfOrderArrivals { get; init; }
    public long MaxReorderDistance { get; init; }
    public int MaxPending { get; init; }

    // Content of the acknowledged messages, per rocket in messageNumber order
    public int Rockets { get; init; }
    public long MaxMessageNumber { get; init; }
    public IReadOnlyDictionary<string, int> MessageTypes { get; init; } = new Dictionary<string, int>();
    public int RocketsNotStartingAtOne { get; init; }
    public int RocketsWithGaps { get; init; }
    public long MissingMessages { get; init; }
    public int RocketsWhereFirstIsNotLaunched { get; init; }
    public int RocketsWithMultipleLaunches { get; init; }
    public int RocketsExploded { get; init; }
    public int RocketsWithMessagesAfterExplosion { get; init; }
    public int MessagesAfterExplosion { get; init; }
    public int RocketsWithNegativeSpeed { get; init; }
    public long MinSpeed { get; init; }
    public long MaxSpeed { get; init; }
    public int RocketsWithNonMonotonicMessageTime { get; init; }
}

public static class CaptureAnalyzer
{
    public static int Run(string path)
    {
        var report = Analyze(CaptureRecord.ReadFile(path));
        Print(report);
        return 0;
    }

    public static CaptureReport Analyze(IEnumerable<CaptureRecord> records)
    {
        // Order by receive time: with a delay probe, lines are appended when the response is sent, not on arrival.
        var ordered = records.OrderBy(r => r.ReceivedAt).ToList();
        var wellFormed = ordered.Where(r => r.IsWellFormed).ToList();

        var deliveries = AnalyzeDeliveries(wellFormed);
        var ordering = AnalyzeOrdering(wellFormed);
        var acknowledged = deliveries.FirstAcknowledged.Values.ToList();
        var rockets = acknowledged.GroupBy(r => r.Channel!).Select(g => AnalyzeRocket(g.OrderBy(r => r.MessageNumber))).ToList();

        return new CaptureReport
        {
            Deliveries = ordered.Count,
            MalformedDeliveries = ordered.Count - wellFormed.Count,
            UniqueMessages = deliveries.UniqueMessages,
            NeverAcknowledged = deliveries.UniqueMessages - acknowledged.Count,
            RedeliveriesAfterSuccess = deliveries.RedeliveriesAfterSuccess,
            RetriesAfterFailure = deliveries.RetryDelays.Count,
            PayloadMismatches = deliveries.PayloadMismatches,
            MaxAttempts = wellFormed.Count == 0 ? 0 : wellFormed.Max(r => r.Attempt),
            MedianRetryDelay = Median(deliveries.RetryDelays),
            MaxRetryDelay = deliveries.RetryDelays.Count == 0 ? null : deliveries.RetryDelays.Max(),

            OutOfOrderArrivals = ordering.OutOfOrderArrivals,
            MaxReorderDistance = ordering.MaxReorderDistance,
            MaxPending = ordering.MaxPending,

            Rockets = rockets.Count,
            MaxMessageNumber = acknowledged.Count == 0 ? 0 : acknowledged.Max(r => r.MessageNumber!.Value),
            MessageTypes = acknowledged.GroupBy(r => r.MessageType!).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            RocketsNotStartingAtOne = rockets.Count(r => r.FirstNumber != 1),
            RocketsWithGaps = rockets.Count(r => r.Missing > 0),
            MissingMessages = rockets.Sum(r => r.Missing),
            RocketsWhereFirstIsNotLaunched = rockets.Count(r => r.FirstType != "RocketLaunched"),
            RocketsWithMultipleLaunches = rockets.Count(r => r.Launches > 1),
            RocketsExploded = rockets.Count(r => r.Exploded),
            RocketsWithMessagesAfterExplosion = rockets.Count(r => r.AfterExplosion > 0),
            MessagesAfterExplosion = rockets.Sum(r => r.AfterExplosion),
            RocketsWithNegativeSpeed = rockets.Count(r => r.MinSpeed < 0),
            MinSpeed = rockets.Count == 0 ? 0 : rockets.Min(r => r.MinSpeed),
            MaxSpeed = rockets.Count == 0 ? 0 : rockets.Max(r => r.MaxSpeed),
            RocketsWithNonMonotonicMessageTime = rockets.Count(r => !r.MessageTimeMonotonic),
        };
    }

    private sealed record DeliveryStats(
        int UniqueMessages,
        int RedeliveriesAfterSuccess,
        int PayloadMismatches,
        List<TimeSpan> RetryDelays,
        Dictionary<string, CaptureRecord> FirstAcknowledged);

    private static DeliveryStats AnalyzeDeliveries(List<CaptureRecord> wellFormed)
    {
        var first = new Dictionary<string, CaptureRecord>();
        var lastAttemptAt = new Dictionary<string, DateTimeOffset>();
        var firstAcknowledged = new Dictionary<string, CaptureRecord>();
        var redeliveriesAfterSuccess = 0;
        var payloadMismatches = 0;
        var retryDelays = new List<TimeSpan>();

        foreach (var record in wellFormed)
        {
            var key = record.Key!;
            if (first.TryGetValue(key, out var original))
            {
                if (firstAcknowledged.ContainsKey(key))
                {
                    redeliveriesAfterSuccess++;
                }
                else
                {
                    retryDelays.Add(record.ReceivedAt - lastAttemptAt[key]);
                }

                if (original.MessageType != record.MessageType || original.Message != record.Message)
                {
                    payloadMismatches++;
                }
            }
            else
            {
                first[key] = record;
            }

            lastAttemptAt[key] = record.ReceivedAt;
            if (record.WasAcknowledged)
            {
                firstAcknowledged.TryAdd(key, record);
            }
        }

        return new DeliveryStats(first.Count, redeliveriesAfterSuccess, payloadMismatches, retryDelays, firstAcknowledged);
    }

    private sealed record OrderingStats(int OutOfOrderArrivals, long MaxReorderDistance, int MaxPending);

    /// <summary>
    /// Replays acknowledged messages in arrival order the way the service's ledger would see them:
    /// how far behind the highest number seen a message arrives, and how many messages wait above the gap-free prefix.
    /// </summary>
    private static OrderingStats AnalyzeOrdering(List<CaptureRecord> wellFormed)
    {
        var seen = new HashSet<string>();
        var rockets = new Dictionary<string, (long Highest, long Checkpoint, SortedSet<long> Pending)>();
        var outOfOrder = 0;
        var maxDistance = 0L;
        var maxPending = 0;

        foreach (var record in wellFormed.Where(r => r.WasAcknowledged && seen.Add(r.Key!)))
        {
            var number = record.MessageNumber!.Value;
            var (highest, checkpoint, pending) = rockets.TryGetValue(record.Channel!, out var state)
                ? state
                : (0L, 0L, new SortedSet<long>());

            if (number < highest)
            {
                outOfOrder++;
                maxDistance = Math.Max(maxDistance, highest - number);
            }

            pending.Add(number);
            while (pending.Count > 0 && pending.Min == checkpoint + 1)
            {
                checkpoint++;
                pending.Remove(checkpoint);
            }

            maxPending = Math.Max(maxPending, pending.Count);
            rockets[record.Channel!] = (Math.Max(highest, number), checkpoint, pending);
        }

        return new OrderingStats(outOfOrder, maxDistance, maxPending);
    }

    private sealed record RocketStats(
        long FirstNumber,
        long Missing,
        string FirstType,
        int Launches,
        bool Exploded,
        int AfterExplosion,
        long MinSpeed,
        long MaxSpeed,
        bool MessageTimeMonotonic);

    /// <summary>Folds one rocket's acknowledged messages in messageNumber order.</summary>
    private static RocketStats AnalyzeRocket(IEnumerable<CaptureRecord> messages)
    {
        var list = messages.ToList();
        long speed = 0, minSpeed = long.MaxValue, maxSpeed = long.MinValue;
        long? explodedAt = null;
        var launches = 0;
        var afterExplosion = 0;
        var monotonic = true;
        DateTimeOffset? previousTime = null;

        foreach (var record in list)
        {
            if (explodedAt is not null && record.MessageNumber > explodedAt)
            {
                afterExplosion++;
            }

            if (previousTime is not null && record.MessageTime < previousTime)
            {
                monotonic = false;
            }
            previousTime = record.MessageTime ?? previousTime;

            using var payload = JsonDocument.Parse(record.Message ?? "{}");
            var body = payload.RootElement;
            switch (record.MessageType)
            {
                case "RocketLaunched":
                    launches++;
                    speed += body.GetProperty("launchSpeed").GetInt64();
                    break;
                case "RocketSpeedIncreased":
                    speed += body.GetProperty("by").GetInt64();
                    break;
                case "RocketSpeedDecreased":
                    speed -= body.GetProperty("by").GetInt64();
                    break;
                case "RocketExploded":
                    explodedAt ??= record.MessageNumber;
                    break;
            }

            minSpeed = Math.Min(minSpeed, speed);
            maxSpeed = Math.Max(maxSpeed, speed);
        }

        // Numbers are expected to run from 1 to the last one seen.
        var last = list[^1].MessageNumber!.Value;
        return new RocketStats(
            list[0].MessageNumber!.Value,
            Missing: last - list.Count,
            list[0].MessageType!,
            launches,
            explodedAt is not null,
            afterExplosion,
            minSpeed,
            maxSpeed,
            monotonic);
    }

    private static TimeSpan? Median(List<TimeSpan> values)
    {
        if (values.Count == 0)
        {
            return null;
        }
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    public static void Print(CaptureReport report)
    {
        Console.WriteLine("Delivery");
        Line("deliveries (all attempts)", report.Deliveries);
        Line("malformed deliveries", report.MalformedDeliveries);
        Line("unique messages", report.UniqueMessages);
        Line("never acknowledged", report.NeverAcknowledged);
        Line("redeliveries after success", report.RedeliveriesAfterSuccess);
        Line("retries after failure", report.RetriesAfterFailure);
        Line("payload mismatches on redelivery", report.PayloadMismatches);
        Line("max attempts for one message", report.MaxAttempts);
        Line("median retry delay", report.MedianRetryDelay?.TotalMilliseconds.ToString("0 ms") ?? "-");
        Line("max retry delay", report.MaxRetryDelay?.TotalMilliseconds.ToString("0 ms") ?? "-");

        Console.WriteLine("Ordering (acknowledged messages, arrival order)");
        Line("out-of-order arrivals", report.OutOfOrderArrivals);
        Line("max reorder distance", report.MaxReorderDistance);
        Line("max pending above checkpoint", report.MaxPending);

        Console.WriteLine("Content (acknowledged messages, messageNumber order)");
        Line("rockets", report.Rockets);
        Line("max message number", report.MaxMessageNumber);
        foreach (var (type, count) in report.MessageTypes)
        {
            Line($"  {type}", count);
        }
        Line("rockets not starting at 1", report.RocketsNotStartingAtOne);
        Line("rockets with gaps", report.RocketsWithGaps);
        Line("missing messages", report.MissingMessages);
        Line("rockets where #1 is not RocketLaunched", report.RocketsWhereFirstIsNotLaunched);
        Line("rockets launched more than once", report.RocketsWithMultipleLaunches);
        Line("rockets exploded", report.RocketsExploded);
        Line("rockets with messages after explosion", report.RocketsWithMessagesAfterExplosion);
        Line("messages after explosion", report.MessagesAfterExplosion);
        Line("rockets whose speed goes negative", report.RocketsWithNegativeSpeed);
        Line("min / max speed", $"{report.MinSpeed} / {report.MaxSpeed}");
        Line("rockets with messageTime not rising", report.RocketsWithNonMonotonicMessageTime);

        static void Line(string label, object value) => Console.WriteLine($"  {label,-42} {value}");
    }
}
