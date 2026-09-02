using JetBrains.Annotations;

namespace Domain.Agents;

// A Lemonade model is named as the host's everywhere inside the system — in the catalogue, in a
// config patch, on a turn's options and on every metric event — and the prefix is taken off only
// where the request body is written, so the box receives the id exactly as it advertised it. One
// string, recognised by shape, the way a leading tilde already marks a routing alias.
[PublicAPI]
public static class LemonadeModelId
{
    public const string Prefix = "lemonade/";

    public static bool IsLemonade(string? modelId) =>
        modelId is not null && modelId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string Namespaced(string bareId) => Prefix + bareId;

    public static string Bare(string modelId) =>
        IsLemonade(modelId) ? modelId[Prefix.Length..] : modelId;
}