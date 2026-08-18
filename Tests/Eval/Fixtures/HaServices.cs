using System.Text.Json.Nodes;

namespace Tests.Eval.Fixtures;

// The service catalog `GET /api/services` answers, in Home Assistant's own shape. What matters is
// the field names and their selectors: the mount renders the action file's help from them and the
// argument parser accepts or rejects a flag on them, so a scenario asserting on a written argument
// is asserting against the same schema a real home publishes.
internal static class HaServices
{
    // The services whose handlers declare a response. Everything else answers `return_response=true`
    // with a 400, exactly as Home Assistant does.
    private static readonly HashSet<string> _responding = ["calendar.get_events"];

    public static bool Responds(string qualified) => _responding.Contains(qualified);

    public static JsonArray Catalog() =>
    [
        Domain("calendar",
            Service("create_event", "Adds a new calendar event.",
                Text("summary", required: true), Text("start_date_time"), Text("end_date_time"),
                Text("description"), Text("rrule"), Text("location")),
            Service("get_events", "Lists events on a calendar within a time range.",
                Text("start_date_time"), Text("end_date_time"), Text("duration")),
            Service("delete_event", "Deletes an event on a calendar.",
                Text("uid", required: true), Text("recurrence_id"), Text("recurrence_range")),
            Service("update_event", "Updates an event on a calendar.",
                Text("uid", required: true), Text("summary"), Text("start_date_time"),
                Text("end_date_time"), Text("description"), Text("rrule"))),
        Domain("light",
            Service("turn_on", "Turns on one or more lights.",
                Number("brightness_pct", 0, 100), Number("transition", 0, 300), Text("color_name")),
            Service("turn_off", "Turns off one or more lights.", Number("transition", 0, 300)),
            Service("toggle", "Toggles one or more lights.")),
        Domain("climate",
            Service("set_temperature", "Sets the target temperature.",
                Number("temperature", 7, 35)),
            Service("set_hvac_mode", "Sets the operation mode.", Text("hvac_mode")),
            Service("turn_on", "Turns the climate device on."),
            Service("turn_off", "Turns the climate device off.")),
        Domain("switch",
            Service("turn_on", "Turns a switch on."),
            Service("turn_off", "Turns a switch off."),
            Service("toggle", "Toggles a switch."))
    ];

    private static JsonNode Domain(string domain, params (string Name, JsonNode Definition)[] services)
    {
        var table = new JsonObject();
        foreach (var (name, definition) in services)
        {
            definition["target"] = new JsonObject
            {
                ["entity"] = new JsonArray(new JsonObject { ["domain"] = new JsonArray(domain) })
            };
            table[name] = definition;
        }

        return new JsonObject { ["domain"] = domain, ["services"] = table };
    }

    private static (string, JsonNode) Service(
        string name, string description, params (string Name, JsonNode Field)[] fields)
    {
        var table = new JsonObject();
        foreach (var (field, definition) in fields)
        {
            table[field] = definition;
        }

        return (name, new JsonObject { ["description"] = description, ["fields"] = table });
    }

    private static (string, JsonNode) Text(string name, bool required = false) =>
        (name, new JsonObject
        {
            ["required"] = required,
            ["selector"] = new JsonObject { ["text"] = new JsonObject() }
        });

    private static (string, JsonNode) Number(string name, double min, double max) =>
        (name, new JsonObject
        {
            ["required"] = false,
            ["selector"] = new JsonObject
            {
                ["number"] = new JsonObject { ["min"] = min, ["max"] = max }
            }
        });
}