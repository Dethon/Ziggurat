using System.Net;
using System.Text.Json.Nodes;
using Domain.Tools.HomeAssistant.Vfs;

namespace Tests.Eval.Fixtures;

// Home Assistant, faked at its REST API — the outermost external, where ADR-0030 puts a fake. The
// server above it is the real one, so the catalog, the action files, the argument parsing and the
// service call a scenario exercises are the deployment's own.
//
// A home small enough to read in a dump: one calendar, one light, one air conditioner, one switch.
// The entities exist to be told apart, not to be a plausible house — every extra entity is another
// hundred tokens of index prompt for a model to be distracted by.
public sealed class FakeHomeAssistant : HttpMessageHandler
{
    public const string Token = "eval-token";

    public const string AlarmsEntityId = "calendar.assistant_alarms";
    public const string KitchenLightEntityId = "light.kitchen";
    public const string AirConditionerEntityId = "climate.salon";
    public const string WashingMachineEntityId = "switch.lavadora";

    // Two players in the kitchen, because "play it here" is only a decision when the room has more
    // than one thing that could play it: one is a Music Assistant player and the other is a
    // television that lists the same actions and does nothing with them.
    public const string KitchenSpeakerEntityId = "media_player.altavoz_cocina";
    public const string KitchenTvEntityId = "media_player.tv_cocina";

    // What the user calls their favourites and what the playlist is called are different strings.
    // That gap is the whole subject of the browse-before-you-play rule.
    public const string FavouritesPlaylist = "Liked Songs dethonv";

    // Already playing when a scenario arrives, so "start it over" has something to start over.
    public const string PlayingEpisodeTitle = "280. Palantir: el control tecnológico de la defensa";
    public const string PlayingEpisodeUri = "spotify--w2nq2jMe://podcast_episode/4Fk1sWv0xKvJ6teiCpTAJN";

    // The directory a scenario writes into its expectations, composed the way the mount composes
    // it rather than typed out: a friendly name edited here would otherwise rename the directory
    // and leave every scenario asserting a path nothing serves.
    public static readonly string AlarmsDirectory = Directory(AlarmsEntityId, "Assistant Alarms");
    public static readonly string KitchenLightDirectory = Directory(KitchenLightEntityId, "Luz Cocina");
    public static readonly string AirConditionerDirectory = Directory(AirConditionerEntityId, "Aire Salón");
    public static readonly string KitchenSpeakerDirectory = Directory(KitchenSpeakerEntityId, "Altavoz Cocina");

    // The same player reached either way. The mount serves entities under /ha/entities and again
    // under the area they belong to, and both are the deployment's own paths — a scenario that
    // pinned one view would report the other as a behavioural failure.
    public static readonly string KitchenSpeakerAnyView =
        $@"(^|/)({HaCatalog.ClassOf(KitchenSpeakerEntityId)}\.)?{HaCatalog.ObjectOf(KitchenSpeakerEntityId)}_\(";

    public static readonly string KitchenTvDirectory = Directory(KitchenTvEntityId, "TV Cocina");

    private readonly Lock _gate = new();
    private readonly List<HaCall> _calls = [];
    private readonly Dictionary<string, HaEntity> _entities;

    public FakeHomeAssistant() => _entities = Seed().ToDictionary(e => e.EntityId);

    public IReadOnlyList<HaCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public string? StateOf(string entityId)
    {
        lock (_gate)
        {
            return _entities.TryGetValue(entityId, out var entity) ? entity.State : null;
        }
    }

    // Every entity's state in one dictionary, which is what a scenario compares across a turn: the
    // question "did anything else move" is only answerable against the whole home, never against
    // the entities the scenario happened to think of. Attributes are in it too, keyed
    // `<entity>#<attribute>`, because a thermostat set to another temperature has changed without
    // its state moving.
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_gate)
        {
            return _entities.Values
                .SelectMany(entity => entity.Snapshot())
                .ToDictionary(entry => entry.Key, entry => entry.Value);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";

        if (request.Method == HttpMethod.Get && path.EndsWith("/api/states"))
        {
            return Json(new JsonArray([.. Entities().Select(e => e.ToJson())]));
        }

        if (request.Method == HttpMethod.Get && path.Contains("/api/states/"))
        {
            var id = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
            return Entities().FirstOrDefault(e => e.EntityId == id) is { } entity
                ? Json(entity.ToJson())
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (request.Method == HttpMethod.Get && path.EndsWith("/api/services"))
        {
            return Json(HaServices.Catalog());
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("/api/template"))
        {
            return Text(Areas());
        }

        if (request.Method == HttpMethod.Post && path.Contains("/api/services/"))
        {
            return await CallServiceAsync(request, path, cancellationToken);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private async Task<HttpResponseMessage> CallServiceAsync(
        HttpRequestMessage request, string path, CancellationToken cancellationToken)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var (domain, service) = (segments[^2], segments[^1]);

        // Home Assistant answers `return_response=true` against a service whose handler declares no
        // response with exactly this 400, and the real client retries without the query on it. The
        // fake says it too, so the eval exercises the retry rather than a path production never
        // takes.
        if (request.RequestUri?.Query.Contains("return_response=true") == true
            && !HaServices.Responds($"{domain}.{service}"))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    $"Service {domain}.{service} does not support responses")
            };
        }

        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))
                   ?? new JsonObject();
        var data = body.AsObject();
        var entityId = data["entity_id"]?.GetValue<string>();
        data.Remove("entity_id");

        lock (_gate)
        {
            _calls.Add(new HaCall(domain, service, entityId, data));
            Apply(domain, service, entityId, data);
        }

        return Media(domain, service, data) ?? Json(new JsonArray());
    }

    // The two media answers that are not "ok, nothing changed": a browse that returns a listing,
    // and a name that resolves to nothing. Both are what the contract's rules are about — the
    // listing is the only place a real playlist title exists, and the 500 is what an invented one
    // gets, reported by Home Assistant as a bare failure that says nothing about what went wrong.
    private HttpResponseMessage? Media(string domain, string service, JsonObject data) =>
        (domain, service) switch
        {
            ("media_player", "browse_media") => Browse(data),
            ("music_assistant", "play_media") when !Resolves(data) => Unresolvable(),
            ("media_player", "play_media") => Unresolvable(),
            _ => null
        };

    // Only the library's own listing. Home Assistant raises a BrowseError for anything else, which
    // reaches the caller as a 500 with no explanation — including for a podcast, which is why the
    // episode action exists at all.
    private static HttpResponseMessage Browse(JsonObject data) =>
        data["media_content_id"]?.GetValue<string>() == "playlists"
            ? Json(new JsonObject
            {
                ["changed_states"] = new JsonArray(),
                ["service_response"] = new JsonObject
                {
                    ["title"] = "Playlists",
                    ["media_content_type"] = "playlists",
                    ["children"] = new JsonArray([.. Playlists.Select(Playlist)])
                }
            })
            : Unresolvable();

    private static readonly string[] _playlists =
        [FavouritesPlaylist, "Domingo por la mañana", "Gimnasio 2026"];

    public static IReadOnlyList<string> Playlists => _playlists;

    private static JsonNode Playlist(string title) => new JsonObject
    {
        ["title"] = title,
        ["media_content_type"] = "playlist",
        ["media_content_id"] = $"library://playlist/{Array.IndexOf(_playlists, title) + 1}",
        ["can_play"] = true,
        ["can_expand"] = false
    };

    // A name resolves if the library has it, and a uri resolves if it is one the fake serves.
    // Everything else is a guess, and a guess is what the browse-first rule exists to prevent.
    private static bool Resolves(JsonObject data) =>
        data["media_id"]?.GetValue<string>() is { } id
        && (_playlists.Contains(id, StringComparer.OrdinalIgnoreCase)
            || id == PlayingEpisodeUri
            || KnownNames.Contains(id, StringComparer.OrdinalIgnoreCase));

    // Free-text names that resolve through the streaming providers, which is what the contract says
    // a track, artist or show may be played by.
    public static IReadOnlyList<string> KnownNames { get; } =
        ["Miles Davis", "No es el fin del mundo", "Radio 3"];

    private static HttpResponseMessage Unresolvable() =>
        new(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("500 Internal Server Error: Server got itself in trouble")
        };

    // What the call did to the home, as far as a scenario can observe it. Only the effects a
    // scenario asserts on are modelled: a fake that guessed at every service's real behaviour would
    // be a second Home Assistant nobody verified.
    private void Apply(string domain, string service, string? entityId, JsonObject data)
    {
        if (entityId is null || !_entities.TryGetValue(entityId, out var entity))
        {
            return;
        }

        _entities[entityId] = service switch
        {
            "turn_on" => entity with { State = "on" },
            "turn_off" => entity with { State = "off" },
            "toggle" => entity with { State = entity.State == "on" ? "off" : "on" },
            "set_temperature" when data["temperature"] is { } temperature =>
                entity.WithAttribute("temperature", temperature.DeepClone()),
            "set_hvac_mode" when data["hvac_mode"] is { } mode =>
                entity with { State = mode.GetValue<string>() },
            _ => entity
        };
    }

    private IReadOnlyList<HaEntity> Entities()
    {
        lock (_gate)
        {
            return [.. _entities.Values];
        }
    }

    // The one template the catalog provider renders, answered with the areas this home has. It is
    // the only path into HA's area registry, so an empty answer would leave `/ha/areas` empty and
    // a room-named request with nowhere to land.
    private string Areas() =>
        new JsonObject
        {
            ["areas"] = new JsonArray(
                Area("kitchen", "Cocina", KitchenLightEntityId, WashingMachineEntityId,
                    KitchenSpeakerEntityId, KitchenTvEntityId),
                Area("salon", "Salón", AirConditionerEntityId))
        }.ToJsonString();

    private static JsonNode Area(string id, string name, params string[] entities) =>
        new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["entities"] = new JsonArray([.. entities.Select(e => (JsonNode)e!)])
        };

    private static IReadOnlyList<HaEntity> Seed() =>
    [
        new(AlarmsEntityId, "off", "Assistant Alarms"),
        new(KitchenLightEntityId, "on", "Luz Cocina"),
        new(AirConditionerEntityId, "cool", "Aire Salón",
            new JsonObject { ["temperature"] = 24, ["hvac_modes"] = new JsonArray("off", "cool", "heat") }),
        new(WashingMachineEntityId, "off", "Lavadora"),
        // The two attributes the contract says to read a player by. The episode already loaded is
        // what makes a seek possible: media_seek needs something on the player, and a scenario
        // about restarting one has to arrive with it playing.
        new(KitchenSpeakerEntityId, "playing", "Altavoz Cocina", new JsonObject
        {
            ["app_id"] = "music_assistant",
            ["mass_player_type"] = "player",
            ["media_title"] = PlayingEpisodeTitle,
            ["media_content_id"] = PlayingEpisodeUri,
            ["media_position"] = 3600
        }),
        new(KitchenTvEntityId, "off", "TV Cocina", new JsonObject
        {
            ["app_id"] = "tv",
            ["device_class"] = "tv"
        })
    ];

    private static string Directory(string entityId, string friendlyName) =>
        $"/ha/entities/{HaCatalog.ClassOf(entityId)}/"
        + HaSlug.Compose(HaCatalog.ObjectOf(entityId), friendlyName);

    private static HttpResponseMessage Json(JsonNode body) => Text(body.ToJsonString());

    private static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
}

public sealed record HaCall(string Domain, string Service, string? EntityId, JsonObject Data);

internal sealed record HaEntity(
    string EntityId, string State, string FriendlyName, JsonObject? Attributes = null)
{
    public HaEntity WithAttribute(string name, JsonNode value)
    {
        var attributes = Attributes?.DeepClone().AsObject() ?? [];
        attributes[name] = value;
        return this with { Attributes = attributes };
    }

    public IEnumerable<KeyValuePair<string, string>> Snapshot() =>
    [
        new(EntityId, State),
        .. (Attributes ?? []).Select(attribute =>
            new KeyValuePair<string, string>(
                $"{EntityId}#{attribute.Key}", attribute.Value?.ToJsonString().Trim('"') ?? ""))
    ];

    public JsonNode ToJson()
    {
        var attributes = Attributes?.DeepClone().AsObject() ?? [];
        attributes["friendly_name"] = FriendlyName;

        return new JsonObject
        {
            ["entity_id"] = EntityId,
            ["state"] = State,
            ["attributes"] = attributes,
            ["last_changed"] = "2026-08-17T18:00:00+00:00",
            ["last_updated"] = "2026-08-17T18:00:00+00:00"
        };
    }
}