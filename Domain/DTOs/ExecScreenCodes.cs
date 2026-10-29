namespace Domain.DTOs;

// Why an exec that would have run unasked is being put to the person, as codes rather than prose:
// each channel says it in its own language beside the approval text it already has.
public static class ExecScreenCodes
{
    // The command does not look like part of what the person asked for.
    public const string NotRequested = "not_requested";

    // It would delete, overwrite or irreversibly change what is already there.
    public const string Destructive = "destructive";

    // It would send data to a remote server.
    public const string SendsOut = "sends_out";

    // It would download code or a program and run it.
    public const string RunsDownloaded = "runs_downloaded";

    // Nobody could say: the judge was late, down, unconfigured, or the turn was the local box's.
    public const string Unjudged = "unjudged";

    // Not the exec screen's: a call that browses a site logged in as the person (asMe), asked the
    // first time a conversation does so on that site. It rides the same field so every channel
    // says why in the words it already has for these.
    public const string AsPerson = "as_person";
}