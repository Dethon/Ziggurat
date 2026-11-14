using System.Text.Json;
using System.Text.Json.Serialization;

namespace Domain.DTOs;

// Who pays for a model call: the household it is made for, whose figures it is counted under (null:
// the deployment's own), and the OpenRouter key it is sent with (null: the host's own key, the
// deployment's — what the home household's calls and every call for nobody are sent with). Carried
// as plain data by whatever makes a call for a household — an agent's spec, a worker's spawn, a
// judgment's caller, an extraction, a night's dreaming — never read from an ambient.
public sealed record ModelPayer(string? TenantId, ModelKey? Key)
{
    public static readonly ModelPayer Deployment = new(null, null);
}

// A household's OpenRouter key. A secret, so it prints as nothing and is never written out: a record
// that carries one (a spec, a spawn, a caller) can be logged, asserted on or serialized whole without
// it, and only the client that sends a call reveals it, into that call's header.
[JsonConverter(typeof(NeverWritten))]
public sealed class ModelKey : IEquatable<ModelKey>
{
    private readonly string _value;

    public ModelKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public string Reveal() => _value;

    public override string ToString() => "[a model key]";

    public bool Equals(ModelKey? other) => other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ModelKey);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);

    private sealed class NeverWritten : JsonConverter<ModelKey>
    {
        public override ModelKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new JsonException("A model key is never read from JSON: it is only ever written out masked.");

        public override void Write(Utf8JsonWriter writer, ModelKey value, JsonSerializerOptions options) =>
            writer.WriteStringValue("[a model key]");
    }
}