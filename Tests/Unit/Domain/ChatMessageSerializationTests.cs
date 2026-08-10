using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatMessageSerializationTests
{
    [Fact]
    public void SetAndGetSenderId_StoresAndRetrievesValue()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");

        msg.SetSenderId("Alice");

        msg.AdditionalProperties.ShouldNotBeNull();
        msg.AdditionalProperties["SenderId"].ShouldBe("Alice");
        msg.GetSenderId().ShouldBe("Alice");
    }

    [Fact]
    public void GetSenderId_ReturnsValueAfterJsonRoundtrip()
    {
        // Arrange - simulate what happens when ChatMessage is stored in Redis
        var msg = new ChatMessage(ChatRole.User, "Hello");
        msg.SetSenderId("Alice");

        // Act - serialize and deserialize (simulates Redis storage)
        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<ChatMessage>(json);

        // Assert - GetSenderId should handle JsonElement correctly
        deserialized.ShouldNotBeNull();
        var senderId = deserialized.GetSenderId();
        senderId.ShouldBe("Alice");
    }

    [Fact]
    public void SetAndGetTimestamp_StoresAndRetrievesValue()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(2));

        msg.SetTimestamp(timestamp);

        msg.AdditionalProperties.ShouldNotBeNull();
        msg.AdditionalProperties["Timestamp"].ShouldBe(timestamp);
        msg.GetTimestamp().ShouldBe(timestamp);
    }

    [Fact]
    public void GetTimestamp_ReturnsValueAfterJsonRoundtrip()
    {
        // Arrange - simulate what happens when ChatMessage is stored in Redis
        var msg = new ChatMessage(ChatRole.User, "Hello");
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.FromHours(2));
        msg.SetTimestamp(timestamp);

        // Act - serialize and deserialize (simulates Redis storage)
        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<ChatMessage>(json);

        // Assert - GetTimestamp should handle JsonElement correctly
        deserialized.ShouldNotBeNull();
        var result = deserialized.GetTimestamp();
        result.ShouldBe(timestamp);
    }

    [Fact]
    public void SetSenderIdOrTimestamp_DoesNothingWhenNull()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");

        msg.SetSenderId(null);
        msg.SetTimestamp(null);

        msg.AdditionalProperties.ShouldBeNull();
    }

    [Fact]
    public void SetAndGetLocation_StoresAndRetrievesValue()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");

        msg.SetLocation("the office");

        msg.AdditionalProperties.ShouldNotBeNull();
        msg.AdditionalProperties["Location"].ShouldBe("the office");
        msg.GetLocation().ShouldBe("the office");
    }

    [Fact]
    public void GetLocation_ReturnsValueAfterJsonRoundtrip()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");
        msg.SetLocation("the office");

        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<ChatMessage>(json);

        deserialized.ShouldNotBeNull();
        deserialized.GetLocation().ShouldBe("the office");
    }

    [Fact]
    public void SetAndGetSatelliteId_StoresAndRetrievesValue()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");

        msg.SetSatelliteId("kitchen-01");

        msg.AdditionalProperties.ShouldNotBeNull();
        msg.AdditionalProperties["SatelliteId"].ShouldBe("kitchen-01");
        msg.GetSatelliteId().ShouldBe("kitchen-01");
    }

    [Fact]
    public void GetSatelliteId_ReturnsValueAfterJsonRoundtrip()
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");
        msg.SetSatelliteId("kitchen-01");

        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<ChatMessage>(json);

        deserialized.ShouldNotBeNull();
        deserialized.GetSatelliteId().ShouldBe("kitchen-01");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void SetSatelliteId_DoesNothingWhenNullOrWhitespace(string? satelliteId)
    {
        var msg = new ChatMessage(ChatRole.User, "Hello");

        msg.SetSatelliteId(satelliteId);

        msg.AdditionalProperties.ShouldBeNull();
    }

    [Fact]
    public void GetMemoryContext_ReturnsValueAfterJsonRoundtrip()
    {
        // Arrange - a message carrying memory context, as the recall hook attaches it.
        // After Redis persistence the value reloads as a JsonElement; the rendered prefix
        // must stay byte-stable across turns, so the context (and its fields) must survive.
        var entry = new MemoryEntry
        {
            Id = "m1",
            UserId = "u1",
            Category = MemoryCategory.Fact,
            Content = "prefers tea over coffee",
            Importance = 0.8,
            Confidence = 0.9,
            CreatedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastAccessedAt = new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero)
        };
        var context = new MemoryContext([new MemorySearchResult(entry, 0.95)], null);
        var msg = new ChatMessage(ChatRole.User, "Hello");
        msg.SetMemoryContext(context);

        // Act - serialize and deserialize (simulates Redis storage)
        var json = JsonSerializer.Serialize(msg);
        var deserialized = JsonSerializer.Deserialize<ChatMessage>(json);

        // Assert - GetMemoryContext should handle JsonElement, preserving rendered fields
        deserialized.ShouldNotBeNull();
        var result = deserialized.GetMemoryContext();
        result.ShouldNotBeNull();
        result!.Memories.Count.ShouldBe(1);
        result.Memories[0].Memory.Content.ShouldBe("prefers tea over coffee");
        result.Memories[0].Memory.Category.ShouldBe(MemoryCategory.Fact);
        result.Memories[0].Memory.Importance.ShouldBe(0.8);
    }

    [Fact]
    public void GetDismissedAlert_JsonElementValue_RoundTrips()
    {
        // After a thread reload AdditionalProperties values come back as JsonElement, not string.
        var message = new ChatMessage(ChatRole.User, "five more minutes");
        message.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["DismissedAlert"] = JsonSerializer.SerializeToElement("alarm \"trash\"")
        };

        message.GetDismissedAlert().ShouldBe("alarm \"trash\"");
    }
}