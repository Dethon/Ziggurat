namespace Domain.DTOs;

// One block, so changing what a conversation's life looks like is one edit rather than four
// constants in three processes. Every horizon here is subtracted from the current time when a
// range is built or an expiry is set, so a change takes effect on the next read with no
// migration and no backfill.
public record RetentionSettings
{
    // The one file this block is configured in, shipped beside this type and copied into the
    // output of every host that references Domain. Each host's own configuration entry point reads
    // it, so an operator changing a horizon edits here and nowhere else. It is added after a host's
    // appsettings.json and before environment variables: a host cannot quietly disagree with the
    // policy, and one container can still be overridden for a test run.
    public const string FileName = "retention.json";

    // Six months. Past this a topic stops appearing in the ordinary list; it is not deleted and
    // nothing is written on it, because archived is where it sits in the index and never a
    // state it carries. See ADR 0024.
    public TimeSpan ArchiveHorizon { get; init; } = TimeSpan.FromDays(182);

    // Twelve months. Past this a topic ceases to exist, taking its history, what it is searched
    // by and the files sent to it.
    public TimeSpan PurgeHorizon { get; init; } = TimeSpan.FromDays(365);

    // How many rows one fetch of the sidebar costs, whatever the space has accumulated.
    public int PageSize { get; init; } = 30;

    // How much of the last message a row shows. Stored on the topic, so a row needs no history.
    public int SnippetLength { get; init; } = 120;

    // Attachments die with the conversation they were sent to rather than eleven months before
    // it, so an old conversation is not a list of missing files.
    public TimeSpan AttachmentRetention { get; init; } = TimeSpan.FromDays(365);
}