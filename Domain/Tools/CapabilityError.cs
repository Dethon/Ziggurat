namespace Domain.Tools;

// Why something the caller asked for is not there. "Not available" is four different answers, and
// they are not interchangeable: one of them is worth waiting for, one is worth asking a person
// about, one means the request was addressed to nothing, and one means it was addressed to the
// wrong thing. A model told only "unavailable" retries the permanent ones and gives up on the
// passing one.
public enum CapabilityState
{
    // Nothing of that name exists here. A typo, or a thing that was never set up.
    Absent,

    // It exists and is not answering right now. The only one of the four worth trying again.
    Unavailable,

    // It exists and is answering, and this caller was not given it. Somebody has to grant it;
    // repeating the call cannot.
    Unassigned,

    // It exists, this caller may use it, and what was asked of it is not something it does.
    Unsupported
}

public static class CapabilityError
{
    // Each state answers as the taxonomy code a caller would act on the same way, so a missing
    // machine and a down dependency read alike to whatever decides whether to retry.
    public static string CodeFor(CapabilityState state) => state switch
    {
        CapabilityState.Absent => ToolError.Codes.NotFound,
        CapabilityState.Unavailable => ToolError.Codes.TransientDependency,
        CapabilityState.Unassigned => ToolError.Codes.PermissionDenied,
        CapabilityState.Unsupported => ToolError.Codes.UnsupportedOperation,
        _ => ToolError.Codes.InternalError
    };

    // The hint is required rather than optional: a capability answer whose whole content is "no"
    // tells a model to try the same thing again in a different wording, which is the loop these
    // four states exist to prevent.
    public static ToolErrorResult For(CapabilityState state, string what, string hint) =>
        ToolError.Result(CodeFor(state), what, hint);
}