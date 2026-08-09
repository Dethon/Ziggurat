using Domain.Agents;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Agents;

public class TurnDecorationTests
{
    private static string FirstText(ChatMessage message, TimeZoneInfo? localTimeZone = null) =>
        TurnDecoration.Apply(message, localTimeZone ?? TimeZoneInfo.Utc)
            .Contents.OfType<TextContent>().First().Text;

    [Fact]
    public void Apply_WithSenderAndLocation_PrefixesRoom()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        msg.SetLocation("the office");

        FirstText(msg).ShouldStartWith("Message from household (in the office):");
    }

    [Fact]
    public void Apply_WithSenderLocationAndSatellite_RendersViaSatellite()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        msg.SetLocation("the office");
        msg.SetSatelliteId("kitchen-01");

        FirstText(msg).ShouldStartWith("Message from household (in the office via kitchen-01):");
    }

    [Fact]
    public void Apply_WithSenderAndSatelliteNoLocation_RendersViaSatellite()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        msg.SetSatelliteId("kitchen-01");

        FirstText(msg).ShouldStartWith("Message from household (via kitchen-01):");
    }

    [Fact]
    public void Apply_WithSatelliteButNoSender_IgnoresSatellite()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSatelliteId("kitchen-01");
        msg.SetTimestamp(new DateTimeOffset(2026, 6, 4, 18, 22, 1, TimeSpan.Zero));

        FirstText(msg).ShouldStartWith("[Current time: ");
        FirstText(msg).ShouldNotContain("Message from");
        FirstText(msg).ShouldNotContain("via");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Apply_WithSenderAndBlankOrNoLocation_OmitsRoom(string? location)
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        if (location is not null)
        {
            msg.AdditionalProperties!["Location"] = location;
        }

        FirstText(msg).ShouldStartWith("Message from household:");
        FirstText(msg).ShouldNotContain("(in");
    }

    [Fact]
    public void Apply_WithLocationButNoSender_IgnoresLocation()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetLocation("the office");
        msg.SetTimestamp(new DateTimeOffset(2026, 6, 4, 18, 22, 1, TimeSpan.Zero));

        FirstText(msg).ShouldStartWith("[Current time: ");
        FirstText(msg).ShouldNotContain("Message from");
        FirstText(msg).ShouldNotContain("(in");
    }

    [Fact]
    public void Apply_WithTimestampOnly_RendersTimeWithoutSender()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetTimestamp(new DateTimeOffset(2026, 6, 4, 18, 22, 1, TimeSpan.Zero));

        FirstText(msg).ShouldStartWith("[Current time: ");
        FirstText(msg).ShouldContain("]:\n");
        FirstText(msg).ShouldNotContain("Message from");
    }

    [Fact]
    public void Apply_RendersTimestampInLocalZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-plus2", TimeSpan.FromHours(2), "p2", "p2");
        var msg = new ChatMessage(ChatRole.User, "hi");
        msg.SetSenderId("u");
        msg.SetTimestamp(new DateTimeOffset(2026, 6, 4, 18, 22, 1, TimeSpan.Zero)); // 18:22:01 UTC

        FirstText(msg, zone).ShouldStartWith("[Current time: 2026-06-04 20:22:01 +02:00]");
    }

    [Fact]
    public void Apply_WithDismissedAlert_PrefixesDismissalContext()
    {
        var msg = new ChatMessage(ChatRole.User, "five more minutes");
        msg.SetSenderId("household");
        msg.SetDismissedAlert("alarm \"Take out the trash\"");

        FirstText(msg).ShouldStartWith(
            "[The user just dismissed the alarm \"Take out the trash\"]\nMessage from household:");
    }

    [Fact]
    public void Apply_WithMemoryContextAndSenderPrefix_PutsTheRecallBlockFirst()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        msg.SetMemoryContext(new MemoryContext([], new PersonalityProfile
        {
            UserId = "household",
            Summary = "Brief communicator",
            LastUpdated = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)
        }));

        var texts = TurnDecoration.Apply(msg, TimeZoneInfo.Utc)
            .Contents.OfType<TextContent>().Select(c => c.Text).ToList();

        texts[0].ShouldStartWith("[Memory context]");
        texts[1].ShouldStartWith("Message from household:");
        texts[2].ShouldBe("lights on");
    }

    [Fact]
    public void Apply_ToAnAssistantMessage_DecoratesNothing()
    {
        var msg = new ChatMessage(ChatRole.Assistant, "the lights are on");
        msg.SetSenderId("household");
        msg.SetTimestamp(new DateTimeOffset(2026, 6, 4, 18, 22, 1, TimeSpan.Zero));
        msg.SetMemoryContext(new MemoryContext([], null));

        FirstText(msg).ShouldBe("the lights are on");
    }
}