namespace Domain.DTOs.Voice;

public record AnnouncementOutcome
{
    // A satellite that took the announcement, and one with no live connection to take it.
    public const string Started = "started";
    public const string Offline = "offline";

    public required string Id { get; init; }
    public required string Status { get; init; }
}