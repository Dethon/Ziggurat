namespace Domain.DTOs;

// The screen's codes as the one English line the text channels show beside an approval (Telegram
// above its keyboard, WebChat on its card). Voice says them in Spanish on its own, because what it
// speaks is a sentence read before a measured question, not a caption.
public static class ExecScreenReasons
{
    private static readonly IReadOnlyDictionary<string, string> _english = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ExecScreenCodes.NotRequested] = "This doesn't look like part of what you asked for.",
        [ExecScreenCodes.Destructive] = "It would delete or change something already on your computer.",
        [ExecScreenCodes.SendsOut] = "It would send data from your computer to a remote server.",
        [ExecScreenCodes.Unjudged] = "It runs on your computer and couldn't be checked first."
    };

    // Null for a request the screen had no part in, so it renders exactly as before. A code this
    // channel does not know — one from a newer agent — is skipped rather than shown raw.
    public static string? English(IReadOnlyList<string>? codes)
    {
        var sentences = (codes ?? []).Select(c => _english.GetValueOrDefault(c)).OfType<string>().ToList();
        return sentences.Count > 0 ? string.Join(" ", sentences) : null;
    }
}