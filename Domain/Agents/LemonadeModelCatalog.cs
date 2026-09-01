using Domain.DTOs.Channel;

namespace Domain.Agents;

// How the host's models become patchable models: namespaced ids, a display name a person can
// read, and the attachment kinds the host reported. Trimming is display only — the id is never
// altered — and two ids that would trim to one name are both shown in full, so nobody picks the
// wrong quantization of the model they meant.
public static class LemonadeModelCatalog
{
    private const string TrimMarker = "-GGUF";

    public static IReadOnlyList<PatchableModel> ToPatchable(IReadOnlyList<LemonadeModel> models)
    {
        var trimmed = models.ToDictionary(m => m.Id, m => Trim(m.Id));
        var collided = trimmed.Values
            .GroupBy(name => name)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        return models
            .Select(m => new PatchableModel(
                LemonadeModelId.Namespaced(m.Id),
                collided.Contains(trimmed[m.Id]) ? m.Id : trimmed[m.Id],
                m.AcceptedAttachmentKinds))
            .ToList();
    }

    private static string Trim(string id)
    {
        var at = id.IndexOf(TrimMarker, StringComparison.OrdinalIgnoreCase);
        return at > 0 ? id[..at] : id;
    }
}