namespace Domain.DTOs.Tables;

// How a table's times and days are written wherever they leave as text — the CSV export, a query's
// answer — so the two never disagree about what a stamp looks like.
public static class TableFormats
{
    // An instant with the offset of the clock it is held on.
    public const string Instant = "yyyy-MM-dd'T'HH:mm:sszzz";

    // A wall time on a clock, with no offset.
    public const string WallTime = "yyyy-MM-dd'T'HH:mm:ss";

    public const string Date = "yyyy-MM-dd";
}