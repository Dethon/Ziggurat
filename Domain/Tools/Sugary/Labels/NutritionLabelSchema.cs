using System.Text.Json;
using Domain.DTOs.Channel;

namespace Domain.Tools.Sugary.Labels;

// The shape a label's turn asks the answer in, as the standalone app sent it
// (Sugary/Packaged/LabelReader.cs). It goes out on the channel message exactly as written, so it
// already satisfies strict mode: every property required, nothing else allowed.
public static class NutritionLabelSchema
{
    public const string Name = "nutrition_label";

    private const string Json =
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["readable", "carbsPer100g", "unit", "servingGrams", "name", "note"],
          "properties": {
            "readable": { "type": "boolean" },
            "carbsPer100g": { "anyOf": [{ "type": "number" }, { "type": "null" }] },
            "unit": { "type": "string", "enum": ["g", "ml"] },
            "servingGrams": { "anyOf": [{ "type": "number" }, { "type": "null" }] },
            "name": { "anyOf": [{ "type": "string" }, { "type": "null" }] },
            "note": { "anyOf": [{ "type": "string" }, { "type": "null" }] }
          }
        }
        """;

    public static ResponseFormat Format { get; } = new()
    {
        Name = Name,
        Schema = JsonSerializer.Deserialize<JsonElement>(Json)
    };
}