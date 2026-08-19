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
    private static readonly HashSet<string> _responding =
        ["calendar.get_events", "media_player.browse_media", "media_player.search_media"];

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
        // Everything a player advertises. The music rules are a set of discriminations between
        // these — a playlist against a track, an episode against its show, a seek against another
        // play — so a catalog holding only the right one would make each of them unfalsifiable.
        Domain("media_player",
            Service("browse_media", "Browses the media available on a player.",
                Text("media_content_type"), Text("media_content_id")),
            Service("search_media", "Searches the providers' whole catalog, not the user's library.",
                Text("search_query", required: true), Text("media_content_type"),
                Text("media_filter_classes")),
            Service("play_media", "Plays a concrete media id or url on a player.",
                Text("media_content_id", required: true), Text("media_content_type", required: true)),
            Service("media_seek", "Seeks to a position in the item currently playing.",
                Number("seek_position", 0, 86400)),
            Service("media_play", "Resumes playback."),
            Service("media_pause", "Pauses playback."),
            Service("media_stop", "Stops playback."),
            Service("media_next_track", "Skips to the next item."),
            Service("volume_set", "Sets the volume.", Number("volume_level", 0, 1)),
            Service("turn_off", "Turns the player off.")),
        // Targeted at media_player rather than at its own domain, the way Music Assistant's
        // integration declares them: that cross-domain target is what puts `music_assistant.*.sh`
        // in every player's directory, and a fake that got it wrong would serve no action at all.
        Domain("music_assistant", targets: "media_player",
            Service("play_media", "Resolves a name in the user's library and plays it.",
                Text("media_id", required: true), Text("media_type"), Text("artist"), Text("album"),
                Text("enqueue"), Text("radio_mode")),
            Service("transfer_queue", "Moves the queue to another player.", Text("source_player"))),
        Domain("switch",
            Service("turn_on", "Turns a switch on."),
            Service("turn_off", "Turns a switch off."),
            Service("toggle", "Toggles a switch.")),
        // The whole-house start sits beside the one-area clean on purpose: an area argument is
        // only a decision when starting everywhere is also on the table.
        Domain("vacuum",
            Service("start", "Starts or resumes cleaning the whole home."),
            Service("return_to_base", "Sends the vacuum back to its dock."),
            Service("clean_zone", "Cleans one area of the home and returns to the dock.",
                AreaId("cleaning_area_id", required: true)),
            // The options carry a casing the user's words never do: "modo turbo" is not "Turbo",
            // so an accepted value was read from the help or from the rejection that lists them.
            Service("set_fan_speed", "Sets the suction level.",
                Select("fan_speed", required: true, "Silencioso", "Normal", "Turbo")))
    ];

    private static JsonNode Domain(
        string domain, params (string Name, JsonNode Definition)[] services) =>
        Domain(domain, domain, services);

    private static JsonNode Domain(
        string domain, string targets, params (string Name, JsonNode Definition)[] services)
    {
        var table = new JsonObject();
        foreach (var (name, definition) in services)
        {
            definition["target"] = new JsonObject
            {
                ["entity"] = new JsonArray(new JsonObject { ["domain"] = new JsonArray(targets) })
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

    // HA's `select` selector: the field takes one of a fixed option list, the help renderer
    // prints the options, and the parser rejects anything else byte-for-byte.
    private static (string, JsonNode) Select(string name, bool required, params string[] options) =>
        (name, new JsonObject
        {
            ["required"] = required,
            ["selector"] = new JsonObject
            {
                ["select"] = new JsonObject
                {
                    ["options"] = new JsonArray([.. options.Select(o => (JsonNode)o)])
                }
            }
        });

    // HA's `area` selector: the field wants the registry's area id — the frozen slug — and the
    // help renderer flags it as exactly that.
    private static (string, JsonNode) AreaId(string name, bool required = false) =>
        (name, new JsonObject
        {
            ["required"] = required,
            ["selector"] = new JsonObject { ["area"] = new JsonObject() }
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