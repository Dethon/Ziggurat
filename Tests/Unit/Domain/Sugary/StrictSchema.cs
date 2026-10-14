using System.Text.Json;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// What strict mode asks of a schema a turn sends: every property required and nothing else allowed,
// on every object, or the provider refuses it.
internal static class StrictSchema
{
    public static void Assert(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            schema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
            var required = schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()).ToList();
            properties.EnumerateObject().Select(p => p.Name).ShouldBe(required, ignoreOrder: true);
        }

        foreach (var child in schema.EnumerateObject())
        {
            if (child.Value.ValueKind == JsonValueKind.Object)
            {
                Assert(child.Value);
            }
            else if (child.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in child.Value.EnumerateArray())
                {
                    Assert(element);
                }
            }
        }
    }
}