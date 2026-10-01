using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rockets.Domain;
using Rockets.Domain.Messages;
using Rockets.Storage.Sqlite;

// Measures the SQLite store the way the single writer will use it: sequential commits of small batches,
// each on a pooled connection. Answers two questions from the implementation plan (Phase 2):
//   1. synchronous=FULL or NORMAL, given commit throughput at batch sizes 1, 3 (default load) and 20 (stress run)?
//   2. how long does replaying 100k messages at startup take?
// Run in Release: dotnet run -c Release --project tools/Rockets.StoreBenchmark [-- <work directory>]
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
var workDirectory = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/benchmark");
Directory.CreateDirectory(workDirectory);
const int CommitsPerRun = 1000;
const int Rockets = 20;

Console.WriteLine($"Commit throughput ({CommitsPerRun} sequential commits per row; database in {workDirectory})");
Console.WriteLine();
Console.WriteLine("| synchronous | batch size | commits/s | messages/s | mean commit | p99 commit |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|");
var messagesPerSecond = new Dictionary<(SqliteSynchronous, int), double>();
foreach (var synchronous in new[] { SqliteSynchronous.Full, SqliteSynchronous.Normal })
{
    foreach (var batchSize in new[] { 1, 3, 20 })
    {
        var store = await CreateStoreAsync($"commit-{synchronous}-{batchSize}.db", synchronous);
        var messages = Generate(CommitsPerRun * batchSize + 50 * batchSize).Chunk(batchSize).ToList();

        foreach (var warmup in messages.Take(50))
        {
            await store.CommitAsync(warmup, []);
        }

        var latencies = new List<double>(CommitsPerRun);
        var total = Stopwatch.StartNew();
        foreach (var batch in messages.Skip(50))
        {
            var commit = Stopwatch.StartNew();
            await store.CommitAsync(batch, []);
            latencies.Add(commit.Elapsed.TotalMilliseconds);
        }
        total.Stop();

        latencies.Sort();
        var commitsPerSecond = CommitsPerRun / total.Elapsed.TotalSeconds;
        messagesPerSecond[(synchronous, batchSize)] = commitsPerSecond * batchSize;
        Console.WriteLine(
            $"| {synchronous} | {batchSize} | {commitsPerSecond:N0} | {commitsPerSecond * batchSize:N0} | " +
            $"{latencies.Average():0.00} ms | {latencies[(int)(latencies.Count * 0.99)]:0.00} ms |");
    }
}

Console.WriteLine();
Console.WriteLine("Estimated default grading run (100,000 messages, concurrency 3, so batches of 1 to 3):");
foreach (var synchronous in new[] { SqliteSynchronous.Full, SqliteSynchronous.Normal })
{
    var best = 100_000 / messagesPerSecond[(synchronous, 3)];
    var worst = 100_000 / messagesPerSecond[(synchronous, 1)];
    Console.WriteLine($"  {synchronous}: {best:N0} s (batches of 3) to {worst:N0} s (batches of 1)");
}

Console.WriteLine();
Console.WriteLine("Startup replay of 100,000 messages (20 rockets x 5,000):");
var replayStore = await CreateStoreAsync("replay.db", SqliteSynchronous.Normal);
foreach (var batch in Generate(100_000).Chunk(1000))
{
    await replayStore.CommitAsync(batch, []);
}

var read = Stopwatch.StartNew();
var count = 0;
await foreach (var _ in replayStore.ReadAllAsync())
{
    count++;
}
read.Stop();

var replay = Stopwatch.StartNew();
var ledgers = new Dictionary<string, RocketLedger>();
await foreach (var message in replayStore.ReadAllAsync())
{
    var ledger = ledgers.TryGetValue(message.Channel, out var existing) ? existing : RocketLedger.Empty(message.Channel);
    ledgers[message.Channel] = ledger.Apply(message).Ledger;
}
replay.Stop();

Console.WriteLine($"  read only:           {count:N0} messages in {read.Elapsed.TotalMilliseconds:N0} ms");
Console.WriteLine($"  read + ledger apply: {ledgers.Count} rockets in {replay.Elapsed.TotalMilliseconds:N0} ms " +
                  $"(checkpoints at {string.Join(", ", ledgers.Values.Select(l => l.CheckpointNumber).Distinct())})");
return 0;

async Task<SqliteMessageStore> CreateStoreAsync(string fileName, SqliteSynchronous synchronous)
{
    var path = Path.Combine(workDirectory, fileName);
    SqliteConnection.ClearAllPools();
    foreach (var file in new[] { path, path + "-wal", path + "-shm" })
    {
        File.Delete(file);
    }
    var store = new SqliteMessageStore(new SqliteStoreOptions(path, synchronous));
    await store.InitializeAsync();
    return store;
}

// Messages shaped like the test program's: each rocket starts with a launch, then mostly speed changes.
static IEnumerable<RocketMessage> Generate(int count)
{
    var random = new Random(444);
    var start = DateTimeOffset.UtcNow;
    for (var i = 0; i < count; i++)
    {
        var channel = $"rocket-{i % Rockets:00}-{new Guid(i % Rockets, 0, 0, new byte[8])}";
        var number = i / Rockets + 1L;
        var (type, message) = number == 1
            ? ("RocketLaunched", (object)new { type = "Falcon-9", launchSpeed = 500, mission = "ARTEMIS" })
            : ("RocketSpeedIncreased", new { by = random.Next(0, 5000) });
        var json = JsonSerializer.Serialize(new
        {
            metadata = new { channel, messageNumber = number, messageTime = start.AddMilliseconds(i), messageType = type },
            message,
        });
        yield return MessageParser.Parse(json).Message!;
    }
}
