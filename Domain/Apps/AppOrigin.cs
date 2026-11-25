using Domain.DTOs;

namespace Domain.Apps;

// Which built app a request is for, read from its Host alone: the one label under the apps domain is
// `<slug>-<id>` for the published app and `<slug>-<id>-preview` for its working tree. Only the id
// decides — the slug is checked for shape and otherwise ignored — so a renamed app's old links still
// open it. Anything else under or beside the domain is no app.
public sealed record AppOrigin(string Id, bool Preview)
{
    public const string PreviewSuffix = "-preview";

    public static AppOrigin? Parse(string host, string appsDomain)
    {
        var name = WithoutPort(host).TrimEnd('.').ToLowerInvariant();
        var suffix = "." + appsDomain.TrimEnd('.').ToLowerInvariant();
        if (!name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        var label = name[..^suffix.Length];
        var preview = label.EndsWith(PreviewSuffix, StringComparison.Ordinal);
        var app = preview ? label[..^PreviewSuffix.Length] : label;
        var dash = app.LastIndexOf('-');
        return dash > 0 && Slugs.IsValid(app[..dash]) && BuiltAppId.IsValid(app[(dash + 1)..])
            ? new AppOrigin(app[(dash + 1)..], preview)
            : null;
    }

    private static string WithoutPort(string host)
    {
        var colon = host.LastIndexOf(':');
        return colon >= 0 && host[(colon + 1)..].All(char.IsAsciiDigit) ? host[..colon] : host;
    }
}