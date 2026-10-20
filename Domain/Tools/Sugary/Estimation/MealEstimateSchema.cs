using System.Text.Json;
using Domain.DTOs.Channel;

namespace Domain.Tools.Sugary.Estimation;

// The shape a meal estimate's turn asks the answer in, as the standalone app sent it
// (Sugary/Estimation/MealEstimator.cs). It goes out on the channel message exactly as written, so it
// already satisfies strict mode: every property required, nothing else allowed, on every object.
public static class MealEstimateSchema
{
    public const string Name = "meal_estimate";

    private const string Json =
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["items", "questions", "notes"],
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "portion", "gramsLow", "grams", "gramsHigh", "carbsPer100g", "confidence", "note"],
                "properties": {
                  "name": { "type": "string" },
                  "portion": { "type": "string" },
                  "gramsLow": { "type": "number" },
                  "grams": { "type": "number" },
                  "gramsHigh": { "type": "number" },
                  "carbsPer100g": { "type": "number" },
                  "confidence": { "type": "string", "enum": ["low", "medium", "high"] },
                  "note": { "anyOf": [{ "type": "string" }, { "type": "null" }] }
                }
              }
            },
            "questions": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["text", "options"],
                "properties": {
                  "text": { "type": "string" },
                  "options": { "type": "array", "items": { "type": "string" } }
                }
              }
            },
            "notes": { "type": "array", "items": { "type": "string" } }
          }
        }
        """;

    public static ResponseFormat Format { get; } = new()
    {
        Name = Name,
        Schema = JsonSerializer.Deserialize<JsonElement>(Json)
    };
}