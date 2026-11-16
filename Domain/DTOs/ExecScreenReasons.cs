namespace Domain.DTOs;

// The screen's codes as the one English line the text channels show beside an approval (Telegram
// above its keyboard, WebChat on its card). Voice says them in Spanish on its own, because what it
// speaks is a sentence read before a measured question, not a caption.
public static class ExecScreenReasons
{
    private static readonly IReadOnlyDictionary<string, string> _english = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ExecScreenCodes.NotRequested] = "This doesn't look like part of what you asked for.",
        [ExecScreenCodes.Destructive] = "It would delete or change something that's already there.",
        [ExecScreenCodes.SendsOut] = "It would send data to a remote server.",
        [ExecScreenCodes.RunsDownloaded] = "It would download a program and run it.",
        [ExecScreenCodes.ReachesLan] = "It would reach devices on your home network.",
        [ExecScreenCodes.Unjudged] = "It runs on your computer and couldn't be checked first.",
        [ExecScreenCodes.AsPerson] = "It would browse this site logged in as you."
    };

    // The same reasons told to someone who sees the card but did not ask: a member reading a shared
    // topic, who would otherwise read "logged in as you" as being about them.
    private static readonly IReadOnlyDictionary<string, string> _englishForOthers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ExecScreenCodes.NotRequested] = "This doesn't look like part of what was asked for.",
        [ExecScreenCodes.Destructive] = "It would delete or change something that's already there.",
        [ExecScreenCodes.SendsOut] = "It would send data to a remote server.",
        [ExecScreenCodes.RunsDownloaded] = "It would download a program and run it.",
        [ExecScreenCodes.ReachesLan] = "It would reach devices on the household's network.",
        [ExecScreenCodes.Unjudged] = "It runs on the asker's computer and couldn't be checked first.",
        [ExecScreenCodes.AsPerson] = "It would browse this site logged in as the person who asked."
    };

    public static string? EnglishForOthers(IReadOnlyList<string>? codes) => Line(codes, _englishForOthers);

    // Null for a request the screen had no part in, so it renders exactly as before. A code this
    // channel does not know — one from a newer agent — is skipped rather than shown raw.
    public static string? English(IReadOnlyList<string>? codes) => Line(codes, _english);

    private static string? Line(IReadOnlyList<string>? codes, IReadOnlyDictionary<string, string> table)
    {
        var sentences = (codes ?? []).Select(c => table.GetValueOrDefault(c)).OfType<string>().ToList();
        return sentences.Count > 0 ? string.Join(" ", sentences) : null;
    }
}