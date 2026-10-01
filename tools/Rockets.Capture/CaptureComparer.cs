namespace Rockets.Capture;

/// <summary>
/// Compares the acknowledged messages of two captures, ignoring messageTime, to find out whether
/// the test program sends the same messages on every run with the same seed.
/// </summary>
public static class CaptureComparer
{
    public sealed record Comparison(int OnlyInFirst, int OnlyInSecond, int DifferentContent, int Identical, int SharedChannels)
    {
        public bool AreEqual => OnlyInFirst == 0 && OnlyInSecond == 0 && DifferentContent == 0;
    }

    public static int Run(string firstPath, string secondPath)
    {
        var result = Compare(CaptureRecord.ReadFile(firstPath), CaptureRecord.ReadFile(secondPath));
        Console.WriteLine($"identical messages        {result.Identical}");
        Console.WriteLine($"only in first             {result.OnlyInFirst}");
        Console.WriteLine($"only in second            {result.OnlyInSecond}");
        Console.WriteLine($"same key, other content   {result.DifferentContent}");
        Console.WriteLine($"channels in both          {result.SharedChannels}");
        Console.WriteLine(result.AreEqual ? "EQUAL" : "DIFFERENT");
        return result.AreEqual ? 0 : 1;
    }

    public static Comparison Compare(IEnumerable<CaptureRecord> first, IEnumerable<CaptureRecord> second)
    {
        var a = Acknowledged(first);
        var b = Acknowledged(second);

        var identical = 0;
        var different = 0;
        foreach (var (key, content) in a)
        {
            if (b.TryGetValue(key, out var other))
            {
                if (content == other) identical++; else different++;
            }
        }

        var sharedChannels = a.Values.Select(v => v.Channel).Distinct()
            .Intersect(b.Values.Select(v => v.Channel).Distinct())
            .Count();

        return new Comparison(
            a.Keys.Count(k => !b.ContainsKey(k)),
            b.Keys.Count(k => !a.ContainsKey(k)),
            different,
            identical,
            sharedChannels);
    }

    private static Dictionary<string, (string Channel, string Type, string? Message)> Acknowledged(IEnumerable<CaptureRecord> records)
    {
        var result = new Dictionary<string, (string, string, string?)>();
        foreach (var record in records.Where(r => r.IsWellFormed && r.WasAcknowledged))
        {
            result.TryAdd(record.Key!, (record.Channel!, record.MessageType!, record.Message));
        }
        return result;
    }
}
