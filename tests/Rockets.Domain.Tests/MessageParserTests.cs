using Rockets.Domain.Messages;
using static Rockets.Domain.Tests.TestMessages;

namespace Rockets.Domain.Tests;

public class MessageParserTests
{
    [Fact]
    public void Parses_the_example_from_the_brief()
    {
        const string json = """
            {
                "metadata": {
                    "channel": "193270a9-c9cf-404a-8f83-838e71d9ae67",
                    "messageNumber": 1,
                    "messageTime": "2022-02-02T19:39:05.86337+01:00",
                    "messageType": "RocketLaunched"
                },
                "message": {
                    "type": "Falcon-9",
                    "launchSpeed": 500,
                    "mission": "ARTEMIS"
                }
            }
            """;

        var message = Parse(json);

        Assert.Equal("193270a9-c9cf-404a-8f83-838e71d9ae67", message.Channel);
        Assert.Equal(1, message.MessageNumber);
        Assert.Equal(DateTimeOffset.Parse("2022-02-02T19:39:05.86337+01:00"), message.MessageTime);
        Assert.Equal("RocketLaunched", message.MessageType);
        Assert.Equal(new RocketLaunched("Falcon-9", 500, "ARTEMIS"), message.Payload);
        Assert.Equal("""{"type":"Falcon-9","launchSpeed":500,"mission":"ARTEMIS"}""", message.PayloadJson);
    }

    [Fact]
    public void Parses_every_known_message_type()
    {
        Assert.Equal(new RocketSpeedIncreased(3000), SpeedIncreased(2, 3000).Payload);
        Assert.Equal(new RocketSpeedDecreased(2500), SpeedDecreased(3, 2500).Payload);
        Assert.Equal(new RocketMissionChanged("SHUTTLE_MIR"), MissionChanged(4, "SHUTTLE_MIR").Payload);
        Assert.Equal(new RocketExploded("PRESSURE_VESSEL_FAILURE"), Exploded(5).Payload);
    }

    [Fact]
    public void An_unknown_message_type_is_accepted_but_carries_no_state_change()
    {
        var message = Unknown(7);

        Assert.Equal("RocketRefuelled", message.MessageType);
        Assert.IsType<UnknownMessage>(message.Payload);
        Assert.Equal("""{"litres":1000}""", message.PayloadJson);
    }

    [Fact]
    public void The_payload_hash_ignores_formatting_and_message_time_but_not_content()
    {
        var compact = Parse("""{"metadata":{"channel":"c","messageNumber":2,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedIncreased"},"message":{"by":3000}}""");
        var spaced = Parse("""{ "metadata": { "channel": "c", "messageNumber": 2, "messageTime": "2023-01-01T00:00:00Z", "messageType": "RocketSpeedIncreased" }, "message": { "by": 3000 } }""");
        var otherAmount = Parse("""{"metadata":{"channel":"c","messageNumber":2,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedIncreased"},"message":{"by":3001}}""");
        var otherType = Parse("""{"metadata":{"channel":"c","messageNumber":2,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedDecreased"},"message":{"by":3000}}""");

        Assert.Equal(compact.PayloadHash, spaced.PayloadHash);
        Assert.NotEqual(compact.PayloadHash, otherAmount.PayloadHash);
        Assert.NotEqual(compact.PayloadHash, otherType.PayloadHash);
    }

    public static TheoryData<string, string> InvalidMessages => new()
    {
        { "not json", "invalid JSON" },
        { "[]", "metadata" },
        { """{"message":{"by":1}}""", "metadata" },
        { """{"metadata":{"messageNumber":1,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedIncreased"},"message":{"by":1}}""", "channel" },
        { """{"metadata":{"channel":"","messageNumber":1,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedIncreased"},"message":{"by":1}}""", "channel" },
        { """{"metadata":{"channel":"c","messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedIncreased"},"message":{"by":1}}""", "messageNumber" },
        { """{"metadata":{"channel":"c","messageNumber":0,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedIncreased"},"message":{"by":1}}""", "messageNumber" },
        { """{"metadata":{"channel":"c","messageNumber":1.5,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedIncreased"},"message":{"by":1}}""", "messageNumber" },
        { """{"metadata":{"channel":"c","messageNumber":1,"messageTime":"yesterday","messageType":"RocketSpeedIncreased"},"message":{"by":1}}""", "messageTime" },
        { """{"metadata":{"channel":"c","messageNumber":1,"messageTime":"2022-02-02T19:39:05Z"},"message":{"by":1}}""", "messageType" },
        { """{"metadata":{"channel":"c","messageNumber":1,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedIncreased"}}""", "message" },
        { """{"metadata":{"channel":"c","messageNumber":1,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedIncreased"},"message":{}}""", "by" },
        { """{"metadata":{"channel":"c","messageNumber":1,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketSpeedDecreased"},"message":{"by":-5}}""", "by" },
        { """{"metadata":{"channel":"c","messageNumber":1,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketLaunched"},"message":{"type":"Falcon-9","mission":"ARTEMIS"}}""", "launchSpeed" },
        { """{"metadata":{"channel":"c","messageNumber":1,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketLaunched"},"message":{"launchSpeed":500,"mission":"ARTEMIS"}}""", "type" },
        { """{"metadata":{"channel":"c","messageNumber":1,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketLaunched"},"message":{"type":"Falcon-9","launchSpeed":500}}""", "mission" },
        { """{"metadata":{"channel":"c","messageNumber":1,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketMissionChanged"},"message":{"newMission":""}}""", "newMission" },
        { """{"metadata":{"channel":"c","messageNumber":1,"messageTime":"2022-02-02T19:39:05Z","messageType":"RocketExploded"},"message":{}}""", "reason" },
    };

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public void Invalid_messages_are_rejected_with_a_reason_naming_the_problem(string json, string expectedInReason)
    {
        var result = MessageParser.Parse(json);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Message);
        Assert.Contains(expectedInReason, result.Error);
    }
}
