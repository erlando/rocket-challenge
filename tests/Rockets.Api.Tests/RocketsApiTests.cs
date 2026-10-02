using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Rockets.Application.Storage;
using Rockets.Domain.Messages;
using static Rockets.Api.Tests.Bodies;

namespace Rockets.Api.Tests;

public sealed class RocketsApiTests : IAsyncLifetime
{
    private const string Apollo = "a-rocket";
    private const string Bravo = "b-rocket";
    private const string Charlie = "c-rocket";

    private readonly ApiFactory _api = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        ApiFactory.DeleteDatabase(_api.DatabasePath);
    }

    private async Task PostAllAsync(params string[] bodies)
    {
        foreach (var body in bodies)
        {
            using var response = await _api.PostAsync(body);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }

    private static string[] Channels(JsonElement list) =>
        list.GetProperty("rockets").EnumerateArray().Select(r => r.GetProperty("channel").GetString()!).ToArray();

    [Fact]
    public async Task A_posted_rocket_is_returned_with_its_state_and_sequence()
    {
        await PostAllAsync(Launched(Apollo, launchSpeed: 500), SpeedIncreased(Apollo, 2, by: 3000), SpeedIncreased(Apollo, 4, by: 10));

        var (status, rocket) = await _api.GetJsonAsync($"/rockets/{Apollo}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Apollo, rocket.GetProperty("channel").GetString());
        Assert.Equal("Falcon-9", rocket.GetProperty("type").GetString());
        Assert.Equal("ARTEMIS", rocket.GetProperty("mission").GetString());
        Assert.Equal(3510, rocket.GetProperty("speed").GetInt64());
        Assert.Equal("launched", rocket.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, rocket.GetProperty("explosionReason").ValueKind);
        Assert.Equal(TimeOf(1), rocket.GetProperty("launchedAt").GetDateTimeOffset());
        Assert.Equal(TimeOf(4), rocket.GetProperty("updatedAt").GetDateTimeOffset());

        var sequence = rocket.GetProperty("sequence");
        Assert.Equal(4, sequence.GetProperty("lastMessageNumber").GetInt64());
        Assert.Equal(2, sequence.GetProperty("checkpointMessageNumber").GetInt64());
        Assert.Equal(1, sequence.GetProperty("pendingMessageCount").GetInt32());
        Assert.Equal(1, sequence.GetProperty("missingMessageCount").GetInt64());
        Assert.False(sequence.GetProperty("isComplete").GetBoolean());
    }

    [Fact]
    public async Task An_exploded_rocket_shows_its_status_and_reason()
    {
        await PostAllAsync(Launched(Apollo), Exploded(Apollo, 2, "ENGINE_FAILURE"));

        var (_, rocket) = await _api.GetJsonAsync($"/rockets/{Apollo}");

        Assert.Equal("exploded", rocket.GetProperty("status").GetString());
        Assert.Equal("ENGINE_FAILURE", rocket.GetProperty("explosionReason").GetString());
        Assert.True(rocket.GetProperty("sequence").GetProperty("isComplete").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_rocket_is_404()
    {
        var (status, problem) = await _api.GetJsonAsync("/rockets/nope");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("nope", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Duplicates_and_invalid_bodies_are_acknowledged_and_counted()
    {
        await PostAllAsync(Launched(Apollo), Launched(Apollo), "{ not json", Envelope(Apollo, 2, "RocketSpeedIncreased", new { by = -5 }));

        var (_, health) = await _api.GetJsonAsync("/health");

        var messages = health.GetProperty("messages");
        Assert.Equal(1, messages.GetProperty("stored").GetInt64());
        Assert.Equal(1, messages.GetProperty("duplicates").GetInt64());
        Assert.Equal(2, messages.GetProperty("rejected").GetInt64());
        Assert.Equal(1, health.GetProperty("rockets").GetInt32());
    }

    [Fact]
    public async Task The_list_is_an_envelope_sorted_by_channel_by_default()
    {
        await PostAllAsync(Launched(Charlie), Launched(Apollo), Launched(Bravo));

        var (status, list) = await _api.GetJsonAsync("/rockets");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, list.GetProperty("count").GetInt32());
        Assert.Equal([Apollo, Bravo, Charlie], Channels(list));
        Assert.Equal(2, list.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("speed", "desc", new[] { Charlie, Apollo, Bravo })]
    [InlineData("speed", "asc", new[] { Bravo, Apollo, Charlie })]
    [InlineData("SPEED", "DESC", new[] { Charlie, Apollo, Bravo })]
    [InlineData("mission", null, new[] { Bravo, Charlie, Apollo })]
    [InlineData("type", "desc", new[] { Apollo, Bravo, Charlie })]
    public async Task The_list_can_be_sorted_by_a_field(string sortBy, string? order, string[] expected)
    {
        await PostAllAsync(
            Launched(Apollo, type: "Saturn-V", launchSpeed: 500, mission: "ZOND"),
            Launched(Bravo, type: "Falcon-9", launchSpeed: 100, mission: "APOLLO"),
            Launched(Charlie, type: "Falcon-9", launchSpeed: 900, mission: "ARTEMIS"));

        var query = order is null ? $"?sortBy={sortBy}" : $"?sortBy={sortBy}&order={order}";
        var (_, list) = await _api.GetJsonAsync($"/rockets{query}");

        Assert.Equal(expected, Channels(list));
    }

    [Theory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task Rockets_without_a_value_sort_last_in_either_order(string order)
    {
        // Charlie has only a speed change so far: its type is unknown until the launch message arrives.
        await PostAllAsync(SpeedIncreased(Charlie, 2, by: 10), Launched(Bravo, type: "Falcon-9"), Launched(Apollo, type: "Saturn-V"));

        var (_, list) = await _api.GetJsonAsync($"/rockets?sortBy=type&order={order}");

        Assert.Equal(Charlie, Channels(list)[^1]);
    }

    [Theory]
    [InlineData("?sortBy=altitude", "sortBy")]
    [InlineData("?order=sideways", "order")]
    public async Task An_unknown_sort_is_400_naming_the_valid_values(string query, string parameter)
    {
        var (status, problem) = await _api.GetJsonAsync($"/rockets{query}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains(parameter, detail);
        Assert.Contains(parameter == "sortBy" ? "launchedAt" : "desc", detail);
    }

    [Fact]
    public async Task State_survives_a_restart()
    {
        await PostAllAsync(Launched(Apollo, launchSpeed: 500), SpeedIncreased(Apollo, 2, by: 100), SpeedIncreased(Apollo, 4, by: 10));
        var (_, before) = await _api.GetJsonAsync($"/rockets/{Apollo}");
        await _api.DisposeAsync();

        await using var restarted = new ApiFactory(_api.DatabasePath);
        var (status, after) = await restarted.GetJsonAsync($"/rockets/{Apollo}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(before.GetRawText(), after.GetRawText());
        Assert.Equal(610, after.GetProperty("speed").GetInt64());
    }

    [Fact]
    public async Task Reset_on_start_begins_with_an_empty_log()
    {
        await PostAllAsync(Launched(Apollo, launchSpeed: 500), Launched(Bravo), "not json");
        await _api.DisposeAsync();

        await using (var reset = new ApiFactory(_api.DatabasePath, resetOnStart: true))
        {
            var (_, list) = await reset.GetJsonAsync("/rockets");
            Assert.Equal(0, list.GetProperty("count").GetInt32());

            // The same message is new again, not a duplicate of the deleted one.
            using var response = await reset.PostAsync(Launched(Apollo, launchSpeed: 700));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            var (_, health) = await reset.GetJsonAsync("/health");
            Assert.Equal(1, health.GetProperty("messages").GetProperty("stored").GetInt64());
            Assert.Equal(0, health.GetProperty("messages").GetProperty("duplicates").GetInt64());
        }

        // Without the option, a restart keeps what was stored after the reset.
        await using var restarted = new ApiFactory(_api.DatabasePath);
        var (_, after) = await restarted.GetJsonAsync("/rockets");
        Assert.Equal([Apollo], Channels(after));
        Assert.Equal(700, after.GetProperty("rockets")[0].GetProperty("speed").GetInt64());
    }

    [Fact]
    public async Task A_storage_failure_is_503_so_the_message_is_resent()
    {
        await using var api = new ApiFactory(store: new FailingStore());

        using var response = await api.PostAsync(Launched(Apollo));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class FailingStore : IMessageStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CommitResult> CommitAsync(
            IReadOnlyList<RocketMessage> messages, IReadOnlyList<RejectedMessage> rejections, CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated storage failure.");

        public IAsyncEnumerable<RocketMessage> ReadAllAsync(CancellationToken cancellationToken = default) => Empty<RocketMessage>();

        public IAsyncEnumerable<RocketMessage> ReadChannelAsync(string channel, CancellationToken cancellationToken = default) =>
            Empty<RocketMessage>();

        public IAsyncEnumerable<RejectedMessage> ReadRejectedAsync(CancellationToken cancellationToken = default) => Empty<RejectedMessage>();

        public Task<long> ClearAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);

        private static async IAsyncEnumerable<T> Empty<T>([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
