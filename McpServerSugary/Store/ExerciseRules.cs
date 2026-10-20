using System.Globalization;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;

namespace McpServerSugary.Store;

// What makes a session of exercise one the log keeps, however it arrives: started with a tap,
// stopped with another, or added afterwards. Each answer is the sentence the refusal says, or null.
// The rules refuse and never move a time she gave: the times kept are always the ones she chose.
public static class ExerciseRules
{
    // Long enough for a hike, short enough to catch a session she forgot to stop.
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(12);

    // Her phone's clock can be a little ahead of the server's.
    public static readonly TimeSpan FutureSlack = TimeSpan.FromMinutes(5);

    private const int MaxNameLength = 40;
    private const int MaxNoteLength = 200;

    public const string NoStart = "A session needs the time it started.";

    public static string? Problem(
        ExerciseKind? kind, string? name, DateTimeOffset? start, DateTimeOffset? end, string? note, DateTimeOffset now)
    {
        if (start is not { } first)
        {
            return NoStart;
        }

        var latest = now + FutureSlack;
        return kind switch
        {
            null => "A session is a walk, the gym or another kind.",
            ExerciseKind.Other when Name(kind, name) is null => "Another kind of exercise needs a name.",
            _ when Name(kind, name)?.Length > MaxNameLength => $"A name can be at most {MaxNameLength} characters.",
            _ when Note(note)?.Length > MaxNoteLength => $"A note can be at most {MaxNoteLength} characters.",
            _ when end <= first => "A session has to end after it starts.",
            _ when first > latest || end > latest => "A session can't start or end in the future.",
            _ when end - first > MaxLength => $"A session can be at most {MaxLength.TotalHours:0} hours.",
            _ => null
        };
    }

    // One session runs at a time: a second one running is a forgotten stop or a second phone, and
    // stopping the first for her would save an end she never chose.
    public static string Running(DateTimeOffset startedAtHome) =>
        $"Another session has been running since {startedAtHome.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}. Stop it first.";

    // The kind as the API spells it ("walk", "gym", "other"); anything else is no kind at all, so a
    // typo is refused with a sentence rather than failing to bind.
    public static ExerciseKind? Kind(string? word) =>
        Enum.GetValues<ExerciseKind>().Cast<ExerciseKind?>()
            .FirstOrDefault(kind => SugaryTables.Word(kind!.Value) == word);

    // Only another kind carries a name: a walk is a walk.
    public static string? Name(ExerciseKind? kind, string? name) =>
        kind is ExerciseKind.Other && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;

    public static string? Note(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}