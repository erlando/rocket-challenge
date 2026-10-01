using static Rockets.Domain.Tests.TestMessages;

namespace Rockets.Domain.Tests;

public class RocketStateTests
{
    [Fact]
    public void A_new_rocket_is_awaiting_launch()
    {
        Assert.Equal(RocketStatus.AwaitingLaunch, RocketState.Initial.Status);
        Assert.Equal(0, RocketState.Initial.Speed);
        Assert.Null(RocketState.Initial.Type);
    }

    [Fact]
    public void Launch_sets_type_speed_mission_and_launch_time()
    {
        var state = RocketState.Initial.Apply(Launched(1, "Falcon-9", 500, "ARTEMIS"));

        Assert.Equal(new RocketState("Falcon-9", "ARTEMIS", 500, RocketStatus.Launched, null, TimeOf(1), TimeOf(1)), state);
    }

    [Fact]
    public void Speed_changes_add_and_subtract()
    {
        var state = FoldInOrder([Launched(1, launchSpeed: 500), SpeedIncreased(2, 3000), SpeedDecreased(3, 2500)]);

        Assert.Equal(1000, state.Speed);
        Assert.Equal(TimeOf(3), state.UpdatedAt);
    }

    [Fact]
    public void Mission_change_replaces_the_mission()
    {
        var state = FoldInOrder([Launched(1, mission: "ARTEMIS"), MissionChanged(2, "SHUTTLE_MIR")]);

        Assert.Equal("SHUTTLE_MIR", state.Mission);
    }

    [Fact]
    public void Speed_changes_before_launch_data_accumulate_while_awaiting_launch()
    {
        var state = FoldInOrder([SpeedIncreased(2, 300), SpeedDecreased(3, 100)]);

        Assert.Equal(RocketStatus.AwaitingLaunch, state.Status);
        Assert.Equal(200, state.Speed);
        Assert.Null(state.LaunchedAt);
    }

    [Fact]
    public void Explosion_records_the_reason_and_the_status_is_permanent()
    {
        var state = FoldInOrder([
            Launched(1, launchSpeed: 500),
            Exploded(2, "ENGINE_FAILURE"),
            MissionChanged(3, "SHUTTLE_MIR"),
            SpeedDecreased(4, 100),
        ]);

        Assert.Equal(RocketStatus.Exploded, state.Status);
        Assert.Equal("ENGINE_FAILURE", state.ExplosionReason);
        // Other fields keep updating after an explosion (an assumption; see implementation-plan.md §2.4).
        Assert.Equal("SHUTTLE_MIR", state.Mission);
        Assert.Equal(400, state.Speed);
        Assert.Equal(TimeOf(4), state.UpdatedAt);
    }

    [Fact]
    public void A_later_launch_message_does_not_revive_an_exploded_rocket()
    {
        var state = FoldInOrder([Launched(1), Exploded(2), Launched(3)]);

        Assert.Equal(RocketStatus.Exploded, state.Status);
    }

    [Fact]
    public void An_unknown_message_changes_nothing()
    {
        var launched = RocketState.Initial.Apply(Launched(1));

        Assert.Equal(launched, launched.Apply(Unknown(2)));
    }

    [Fact]
    public void Speed_overflow_is_an_error_not_a_wrong_number()
    {
        var launched = RocketState.Initial.Apply(Launched(1, launchSpeed: 500));

        Assert.Throws<OverflowException>(() => launched.Apply(SpeedIncreased(2, long.MaxValue)));
    }
}
