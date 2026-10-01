using Rockets.Domain.Messages;
using static Rockets.Domain.Tests.TestMessages;

namespace Rockets.Domain.Tests;

/// <summary>
/// Property-style tests of the ordering model. Each seed generates a rocket's message sequence, shuffles it,
/// and injects redeliveries. The ledger is checked after every step against a naive reference: the in-order
/// fold of all distinct messages received so far. These tests cover the ledger's ordering logic;
/// what each message does to the state is covered by <see cref="RocketStateTests"/>.
/// </summary>
public class RocketLedgerPropertyTests
{
    public static TheoryData<int> Seeds => new(Enumerable.Range(0, 200));

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Shuffled_and_redelivered_messages_give_the_same_state_as_applying_them_in_order(int seed)
    {
        var random = new Random(seed);
        var sequence = GenerateSequence(random, count: random.Next(1, 300));
        var deliveries = Shuffle(random, WithRedeliveries(random, sequence, redeliveryRate: 0.2));

        var ledger = RocketLedger.Empty(Channel);
        var received = new Dictionary<long, RocketMessage>();

        foreach (var message in deliveries)
        {
            var result = ledger.Apply(message);
            var isRedelivery = !received.TryAdd(message.MessageNumber, message);

            Assert.Equal(isRedelivery, result.Outcome == LedgerOutcome.Duplicate);
            Assert.False(result.PayloadMismatch);
            ledger = result.Ledger;
            AssertInvariants(ledger, received);
        }

        Assert.Equal(sequence.Count, ledger.CheckpointNumber);
        Assert.Empty(ledger.Pending);
        Assert.Equal(0, ledger.MissingMessageCount);
        Assert.Equal(FoldInOrder(sequence), ledger.Current);
        Assert.Equal(ledger.Checkpoint, ledger.Current);
    }

    [Fact]
    public void Messages_arriving_in_reverse_order_stay_pending_until_the_first_arrives()
    {
        var sequence = GenerateSequence(new Random(42), count: 500);

        var ledger = ApplyAll(Enumerable.Reverse(sequence).Take(sequence.Count - 1));

        Assert.Equal(0, ledger.CheckpointNumber);
        Assert.Equal(sequence.Count - 1, ledger.Pending.Count);
        Assert.Equal(1, ledger.MissingMessageCount);

        ledger = ledger.Apply(sequence[0]).Ledger;

        Assert.Equal(sequence.Count, ledger.CheckpointNumber);
        Assert.Equal(FoldInOrder(sequence), ledger.Current);
    }

    private static void AssertInvariants(RocketLedger ledger, Dictionary<long, RocketMessage> received)
    {
        // The checkpoint is the end of the gap-free prefix 1..N.
        var expectedCheckpoint = 0L;
        while (received.ContainsKey(expectedCheckpoint + 1))
        {
            expectedCheckpoint++;
        }
        Assert.Equal(expectedCheckpoint, ledger.CheckpointNumber);

        // The checkpoint state is exact: the in-order fold of messages 1..N.
        Assert.Equal(FoldInOrder(received.Values.Where(m => m.MessageNumber <= expectedCheckpoint)), ledger.Checkpoint);

        // Pending holds exactly the received messages above N.
        Assert.Equal(received.Keys.Where(n => n > expectedCheckpoint).Order(), ledger.Pending.Keys);

        // The current state is everything received, applied in order, skipping gaps.
        Assert.Equal(FoldInOrder(received.Values), ledger.Current);

        Assert.Equal(received.Keys.Max(), ledger.LastMessageNumber);
        Assert.Equal(received.Keys.Max() - received.Count, ledger.MissingMessageCount);
    }

    /// <summary>A plausible rocket: launched first, then mostly speed changes, some mission changes,
    /// an occasional unknown message type, and possibly an explosion.</summary>
    private static List<RocketMessage> GenerateSequence(Random random, int count)
    {
        var messages = new List<RocketMessage> { Launched(1, launchSpeed: random.Next(0, 1000)) };
        var explodesAt = random.NextDouble() < 0.3 ? random.Next(2, count + 2) : -1;

        for (var number = 2L; number <= count; number++)
        {
            var roll = random.NextDouble();
            messages.Add(number == explodesAt ? Exploded(number)
                : roll < 0.45 ? SpeedIncreased(number, random.Next(0, 5000))
                : roll < 0.90 ? SpeedDecreased(number, random.Next(0, 5000))
                : roll < 0.98 ? MissionChanged(number, $"MISSION_{random.Next(10)}")
                : Unknown(number));
        }

        return messages;
    }

    private static List<RocketMessage> WithRedeliveries(Random random, List<RocketMessage> sequence, double redeliveryRate) =>
        sequence.Concat(sequence.Where(_ => random.NextDouble() < redeliveryRate)).ToList();

    private static List<RocketMessage> Shuffle(Random random, List<RocketMessage> messages)
    {
        var shuffled = messages.ToArray();
        random.Shuffle(shuffled);
        return shuffled.ToList();
    }
}
