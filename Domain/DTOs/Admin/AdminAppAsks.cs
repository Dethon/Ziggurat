using Domain.DTOs.Household;
using JetBrains.Annotations;

namespace Domain.DTOs.Admin;

// The household's daily caps on its built apps' asks as its owners edit them (.claude/rules/admin-owner-pages.md,
// "Apps"), beside the household's published apps a cap of its own may be set for — none when the household
// has no apps feature, null when the apps server could not be asked. A save sends the app asks section whole.
[PublicAPI]
public sealed record AdminAppAsks(AppAsksSection Caps, IReadOnlyList<AdminCappedApp>? Apps = null);

// One of the household's published apps, as the apps page shows it: its author by their name now.
[PublicAPI]
public sealed record AdminCappedApp(string Id, string Name, string AccentColor, string AuthorName, string Url);