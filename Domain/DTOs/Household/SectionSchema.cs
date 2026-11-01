using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Domain.DTOs.Household;

// One section of a household's configuration as the accounts service keeps it: a JSON document
// stamped with the version of its shape, carried forward to the current shape on every read and
// every save by the steps beside its record, so a document written under any earlier shape — an old
// row, an operator's file kept from before — is always read as today's. A step is a pure function of
// the plain document (secrets in clear: the service opens them before it migrates and seals them
// again after), so a step may move a secret like any other field.
//
// Version 0 is a document that names no version, which the first step reads as the first shape.
public abstract class SectionSchema(string name, IReadOnlyList<Func<JsonObject, JsonObject>> steps)
{
    public const string VersionProperty = "schemaVersion";

    // Strict, for what is stored and what an operator saves: a misspelt field is refused rather than
    // dropped in silence, and a null where the shape has none is refused rather than read as empty.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        WriteIndented = true
    };

    public string Name { get; } = name;

    public int CurrentVersion => steps.Count;

    // The fields of the current shape that are secrets, as paths of property names joined by `/`,
    // `*` standing for any one name (a bot keyed by its agent): sealed on save, never printed, and
    // unsealed only into the view of the server they belong to.
    public virtual IReadOnlyList<string> SecretPaths => [];

    public static int VersionOf(JsonObject document) =>
        document[VersionProperty] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 0;

    // The document carried to the current shape and stamped with its version. One from a newer shape
    // than this code knows is refused: reading it as today's would drop what the newer one added.
    public JsonObject Migrate(JsonObject document)
    {
        var version = VersionOf(document);
        if (version > CurrentVersion || version < 0)
        {
            throw new SectionShapeException(
                $"the {Name} section is at schema version {version}, which this deployment does not know (it knows up to {CurrentVersion})");
        }

        var migrated = steps.Skip(version).Aggregate(Unstamped(document), (current, step) => step(current));
        migrated[VersionProperty] = CurrentVersion;
        return migrated;
    }

    // What is wrong with a document at the current shape; none means it may be saved.
    public abstract IReadOnlyList<string> Problems(JsonObject current);

    // A document at the current shape written out whole, every field its record defaults filled in:
    // what is stored and what a reader is answered, so neither depends on what a writer left out.
    public abstract JsonObject Normalize(JsonObject current);

    // The section as a household that saved none has it.
    public JsonObject Default => Normalize(Migrate(new JsonObject()));

    protected static JsonObject Unstamped(JsonObject document)
    {
        var copy = document.DeepClone().AsObject();
        copy.Remove(VersionProperty);
        return copy;
    }
}

// A section whose current shape is the record T: read into it, written from it, validated by it.
public sealed class SectionSchema<T>(
    string name,
    IReadOnlyList<Func<JsonObject, JsonObject>> steps,
    Func<T, IEnumerable<string>> validate,
    IReadOnlyList<string>? secretPaths = null) : SectionSchema(name, steps) where T : class
{
    public override IReadOnlyList<string> SecretPaths { get; } = secretPaths ?? [];

    public T Read(JsonObject current) =>
        Unstamped(current).Deserialize<T>(Json)
        ?? throw new SectionShapeException($"the {Name} section is empty");

    public JsonObject Write(T section)
    {
        var document = JsonSerializer.SerializeToNode(section, Json)!.AsObject();
        document.Insert(0, VersionProperty, CurrentVersion);
        return document;
    }

    public override JsonObject Normalize(JsonObject current) => Write(Read(current));

    public override IReadOnlyList<string> Problems(JsonObject current)
    {
        try
        {
            return [.. validate(Read(current))];
        }
        catch (JsonException ex)
        {
            return [$"the {Name} section does not read as its shape: {ex.Message}"];
        }
    }
}

public sealed class SectionShapeException(string message) : Exception(message);

// The first step of every section: a document that names no version is the first shape as it is.
internal static class SectionSteps
{
    public static readonly Func<JsonObject, JsonObject> Unversioned = document => document;
}