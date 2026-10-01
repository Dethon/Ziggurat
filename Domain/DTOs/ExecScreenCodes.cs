namespace Domain.DTOs;

// Why an exec that would have run unasked is being put to the person, as codes rather than prose:
// each channel says it in its own language beside the approval text it already has.
public static class ExecScreenCodes
{
    // The command does not look like part of what the person asked for.
    public const string NotRequested = "not_requested";

    // It would delete, overwrite or irreversibly change what is already on the person's machine.
    public const string Destructive = "destructive";

    // It would send data from the person's machine to a remote server.
    public const string SendsOut = "sends_out";

    // Nobody could say: the judge was late, down, unconfigured, or the turn was the local box's.
    public const string Unjudged = "unjudged";
}