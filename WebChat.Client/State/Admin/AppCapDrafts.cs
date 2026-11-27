using Domain.DTOs.Admin;
using Domain.DTOs.Household;

namespace WebChat.Client.State.Admin;

// The apps page's caps between loading and saving: the household's default, and a cap for each of its
// published apps in the order listed, then each cap kept for an app no longer listed (deleted since, or the
// apps server not answering), by its id, so an owner can still remove it. None is no cap of the app's own.
public sealed class AppCapDrafts
{
    private AppCapDrafts(int? household, IReadOnlyList<AppCapDraft> apps)
    {
        Household = household;
        Apps = apps;
    }

    public int? Household { get; set; }

    public IReadOnlyList<AppCapDraft> Apps { get; }

    public static AppCapDrafts Of(AdminAppAsks loaded)
    {
        var listed = loaded.Apps ?? [];
        return new AppCapDrafts(loaded.Caps.DailyCap,
        [
            .. listed.Select(app => new AppCapDraft(app.Id, app.Name, app.AuthorName, true,
                loaded.Caps.Apps.TryGetValue(app.Id, out var cap) ? cap : null)),
            .. loaded.Caps.Apps.Where(cap => listed.All(app => app.Id != cap.Key)).OrderBy(cap => cap.Key, StringComparer.Ordinal)
                .Select(cap => new AppCapDraft(cap.Key, cap.Key, null, false, cap.Value))
        ]);
    }

    // Only the caps set: an app with none meets the household's.
    public AppAsksSection ToSection() => new()
    {
        DailyCap = Household,
        Apps = Apps.Where(app => app.Cap is not null).ToDictionary(app => app.Id, app => app.Cap!.Value)
    };
}

// Listed: one of the household's published apps as the apps server answers now; else a cap kept by id alone.
public sealed class AppCapDraft(string id, string name, string? authorName, bool listed, int? cap)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string? AuthorName { get; } = authorName;
    public bool Listed { get; } = listed;
    public int? Cap { get; set; } = cap;
}