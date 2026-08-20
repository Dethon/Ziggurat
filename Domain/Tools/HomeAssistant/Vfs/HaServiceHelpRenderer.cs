using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaServiceHelpRenderer
{
    public static string Render(string entityId, HaServiceDefinition svc)
    {
        var sb = new StringBuilder();
        sb.Append(svc.Service).Append(".sh — call ")
          .Append(svc.Domain).Append('.').Append(svc.Service)
          .Append(" on ").Append(entityId).Append('\n');

        if (!string.IsNullOrWhiteSpace(svc.Description))
        {
            sb.Append(svc.Description!.Trim()).Append('\n');
        }

        if (svc.Fields.Count == 0)
        {
            sb.Append("  (no arguments)");
            return sb.ToString();
        }

        foreach (var (name, field) in svc.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            sb.Append("  --").Append(name).Append("  ").Append(TypeOf(field.Selector));
            if (field.Required)
            {
                sb.Append("  (required)");
            }
            if (!string.IsNullOrWhiteSpace(field.Description))
            {
                sb.Append("  ").Append(field.Description!.Trim());
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    public static string TypeOf(JsonNode? selector)
    {
        if (selector is null)
        {
            return "TEXT";
        }
        if (selector["number"] is JsonNode number)
        {
            var kind = number["step"]?.GetValue<double>() is { } step && step % 1 != 0 ? "FLOAT" : "INT";
            var min = number["min"];
            var max = number["max"];
            return min is not null && max is not null ? $"{kind} {min}-{max}" : kind;
        }
        if (selector["boolean"] is not null)
        {
            return "BOOL";
        }
        if (selector["select"]?["options"] is JsonArray options)
        {
            var opts = options.Select(o => o is JsonObject obj ? obj["value"]?.ToString() : o?.ToString());
            return $"ONE OF [{string.Join(",", opts)}]";
        }
        if (selector["object"] is not null)
        {
            return "TEXT or JSON";
        }
        // HA's `area` selector means the field wants an area_id — the lowercase registry slug
        // (e.g. `salon`), shown in the setup-index parens and as the /ha/areas/<room> segment,
        // NOT the display name. The help line spells out where to read it because this is the
        // one place the model reliably looks: the prompt's own version of the rule was skipped
        // one armed run in three, a slugified synonym of the user's words passed instead.
        if (selector["area"] is not null)
        {
            return "AREA_ID (slug: read it verbatim from the setup index heading, never derived from the room's name)";
        }
        return "TEXT";
    }
}