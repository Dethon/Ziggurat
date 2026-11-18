namespace McpServerSugary.Store;

// The LibreLinkUp follower account her glucose comes from, one per person: where it lives, how to log
// in (the password sealed for her, GlucoseCredentials), whose sensor it reads, how she reads glucose,
// and how the last polls went.
public class GlucoseLink : IPersonal
{
    public string UserId { get; set; } = "";
    public string TenantId { get; set; } = "";

    public string Email { get; set; } = "";
    public byte[] SealedPassword { get; set; } = [];

    // The region LibreLinkUp found the account at, as the client keeps it (lowercase); null for the
    // global address. Read off the answer, never off what she typed.
    public string? Region { get; set; }

    // The person the account follows whose readings are hers; null until she chooses among several.
    public string? PatientId { get; set; }

    // Everyone the account followed when it was linked, so a choice is checked without logging in again
    // and she can change it later.
    public List<GlucoseConnectionOffered> Connections { get; set; } = [];

    public GlucoseUnit Unit { get; set; } = GlucoseUnit.Mgdl;

    // Her target range in mg/dL whatever unit she reads in.
    public int TargetLow { get; set; } = GlucoseRange.DefaultLow;
    public int TargetHigh { get; set; } = GlucoseRange.DefaultHigh;

    public DateTimeOffset LinkedAt { get; set; }

    // The poller's: when LibreLinkUp last answered, and since when and why it has not.
    public DateTimeOffset? LastSuccessAt { get; set; }
    public GlucoseLinkFailure? LastFailure { get; set; }

    // A step's component, a minimum version: what the failure named, for the app to say.
    public string? FailureDetail { get; set; }
    public DateTimeOffset? FailingSince { get; set; }

    public string? Name => Connections.FirstOrDefault(c => c.PatientId == PatientId)?.Name;
}

public class GlucoseConnectionOffered
{
    public string PatientId { get; set; } = "";
    public string Name { get; set; } = "";
}

public enum GlucoseUnit
{
    Mgdl,
    Mmol
}

// Why the last poll got no answer, as LibreLinkUpFailure names it.
public enum GlucoseLinkFailure
{
    CredentialsRefused,
    StepRequired,
    VersionTooOld,
    RateLimited,
    Unreachable
}

public static class GlucoseRange
{
    // The international consensus target range, which time-in-range is reported against.
    public const int DefaultLow = 70;
    public const int DefaultHigh = 180;

    // What a sensor reads: a range outside it could never be left or entered.
    public const int Lowest = 40;
    public const int Highest = 400;

    public static bool IsValid(int? low, int? high) =>
        low >= Lowest && high <= Highest && low < high;
}