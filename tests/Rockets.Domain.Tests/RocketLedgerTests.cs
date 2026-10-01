using static Rockets.Domain.Tests.TestMessages;

namespace Rockets.Domain.Tests;

public class RocketLedgerTests
{
    [Fact]
    public void An_empty_ledger_has_nothing_applied()
    {
        var ledger = RocketLedger.Empty(Channel);

        Assert.Equal(Channel, ledger.Channel);
        Assert.Equal(0, ledger.CheckpointNumber);
        Assert.Equal(0, ledger.LastMessageNumber);
        Assert.Empty(ledger.Pending);
        Assert.Equal(RocketState.Initial, ledger.Current);
    }

    [Fact]
    public void In_order_messages_advance_the_checkpoint_one_by_one()
    {
        var first = RocketLedger.Empty(Channel).Apply(Launched(1));
        var second = first.Ledger.Apply(SpeedIncreased(2, 100));

        Assert.Equal(LedgerOutcome.Advanced, first.Outcome);
        Assert.Equal(LedgerOutcome.Advanced, second.Outcome);
        Assert.Equal(2, second.Ledger.CheckpointNumber);
        Assert.Empty(second.Ledger.Pending);
        Assert.Equal(600, second.Ledger.Checkpoint.Speed);
        Assert.Equal(second.Ledger.Checkpoint, second.Ledger.Current);
    }

    [Fact]
    public void A_message_above_a_gap_is_pending_but_shows_in_the_current_state()
    {
        var ledger = ApplyAll([Launched(1, launchSpeed: 500), SpeedIncreased(2, 100)]);

        var result = ledger.Apply(SpeedIncreased(4, 50));

        Assert.Equal(LedgerOutcome.Accepted, result.Outcome);
        Assert.Equal(2, result.Ledger.CheckpointNumber);
        Assert.Equal(new[] { 4L }, result.Ledger.Pending.Keys);
        Assert.Equal(600, result.Ledger.Checkpoint.Speed);
        Assert.Equal(650, result.Ledger.Current.Speed);
        Assert.Equal(4, result.Ledger.LastMessageNumber);
        Assert.Equal(1, result.Ledger.MissingMessageCount);
    }

    [Fact]
    public void Filling_the_gap_moves_the_checkpoint_through_all_pending_messages()
    {
        var ledger = ApplyAll([Launched(1), SpeedIncreased(3, 30), SpeedIncreased(4, 40), SpeedIncreased(6, 60)]);

        var result = ledger.Apply(SpeedIncreased(2, 20));

        Assert.Equal(LedgerOutcome.Advanced, result.Outcome);
        Assert.Equal(4, result.Ledger.CheckpointNumber);
        Assert.Equal(new[] { 6L }, result.Ledger.Pending.Keys);
        Assert.Equal(590, result.Ledger.Checkpoint.Speed);
        Assert.Equal(650, result.Ledger.Current.Speed);
        Assert.Equal(1, result.Ledger.MissingMessageCount);
    }

    [Fact]
    public void Messages_before_the_launch_wait_and_are_applied_after_it_once_it_arrives()
    {
        var ledger = ApplyAll([SpeedIncreased(2, 100), MissionChanged(3, "SHUTTLE_MIR")]);

        Assert.Equal(RocketStatus.AwaitingLaunch, ledger.Current.Status);
        Assert.Equal(0, ledger.CheckpointNumber);

        ledger = ledger.Apply(Launched(1, launchSpeed: 500, mission: "ARTEMIS")).Ledger;

        Assert.Equal(3, ledger.CheckpointNumber);
        Assert.Equal(RocketStatus.Launched, ledger.Current.Status);
        Assert.Equal(600, ledger.Current.Speed);
        Assert.Equal("SHUTTLE_MIR", ledger.Current.Mission);
    }

    [Fact]
    public void A_redelivered_message_below_the_checkpoint_is_a_duplicate_and_changes_nothing()
    {
        var ledger = ApplyAll([Launched(1), SpeedIncreased(2, 100)]);

        var result = ledger.Apply(SpeedIncreased(2, 100));

        Assert.Equal(LedgerOutcome.Duplicate, result.Outcome);
        Assert.False(result.PayloadMismatch);
        Assert.Same(ledger, result.Ledger);
    }

    [Fact]
    public void A_redelivered_pending_message_is_a_duplicate_and_a_content_change_is_flagged()
    {
        var ledger = ApplyAll([Launched(1), SpeedIncreased(3, 100)]);

        var same = ledger.Apply(SpeedIncreased(3, 100));
        var changed = ledger.Apply(SpeedIncreased(3, 999));

        Assert.Equal(LedgerOutcome.Duplicate, same.Outcome);
        Assert.False(same.PayloadMismatch);
        Assert.Equal(LedgerOutcome.Duplicate, changed.Outcome);
        Assert.True(changed.PayloadMismatch);
        Assert.Same(ledger, changed.Ledger);
        Assert.Equal(600, changed.Ledger.Current.Speed);
    }

    [Fact]
    public void An_unknown_message_type_fills_its_place_in_the_sequence()
    {
        var ledger = ApplyAll([Launched(1), Unknown(2), SpeedIncreased(3, 100)]);

        Assert.Equal(3, ledger.CheckpointNumber);
        Assert.Equal(600, ledger.Current.Speed);
    }

    [Fact]
    public void A_message_for_another_rocket_is_refused()
    {
        var ledger = RocketLedger.Empty(Channel);

        Assert.Throws<ArgumentException>(() => ledger.Apply(Launched(channel: "another-rocket")));
    }

    [Fact]
    public void A_message_that_fails_to_apply_leaves_the_ledger_unchanged()
    {
        var ledger = ApplyAll([Launched(1)]);

        Assert.Throws<OverflowException>(() => ledger.Apply(SpeedIncreased(2, long.MaxValue)));
        Assert.Equal(1, ledger.CheckpointNumber);
        Assert.Empty(ledger.Pending);
        Assert.Equal(500, ledger.Current.Speed);
    }
}
