using McpServerSugary.Glucose;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.App;

public record GlucoseConnectionDto(string PatientId, string Name);

// Never her password; her email in full, since she is the one asking and typed it.
public record GlucoseLinkDto(
    string Email,
    string? Region,
    string? PatientId,
    string? Name,
    IReadOnlyList<GlucoseConnectionDto> Connections,
    GlucoseUnit Unit,
    int TargetLow,
    int TargetHigh,
    DateTimeOffset LinkedAt,
    DateTimeOffset? LastSuccessAt,
    string? LastFailure,
    string? FailureDetail,
    DateTimeOffset? FailingSince);

public record LinkGlucoseRequest(string? Email, string? Password, string? Region);

public record ChooseConnectionRequest(string? PatientId);

// Unit is text and the range nullable, so a missing or unknown value is refused with a code rather
// than a bare 400 from a failed bind. Loaded is what the sheet opened with (StaleEdits).
public record GlucoseSettingsRequest(
    string? Unit, int? TargetLow, int? TargetHigh, GlucoseSettingsRequest? Loaded = null);

// The LibreLinkUp account her glucose comes from. Every refusal carries a `code` the app words in her
// language: linking-off, missing-credentials, unknown-region, credentials-refused, step-required (with
// `component`), no-connections, rate-limited (with `retryAfterSeconds` where LibreLinkUp said),
// unavailable, not-linked, unknown-connection, unknown-unit, bad-range.
public static class GlucoseEndpoints
{
    public static void MapGlucoseEndpoints(this IEndpointRouteBuilder api)
    {
        var glucose = api.MapGroup("/glucose");
        glucose.MapGet("/link", ReadAsync);
        // Logging in may take LibreLinkUp's whole timeout; her turn is taken around the write alone.
        glucose.MapPut("/link", LinkAsync).WithMetadata(new TakesItsOwnTurn());
        glucose.MapPut("/link/connection", ChooseAsync);
        glucose.MapPut("/settings", SaveSettingsAsync);
        glucose.MapDelete("/link", UnlinkAsync);
    }

    private static async Task<IResult> ReadAsync(SugaryDbContext db, GlucoseCredentials credentials, CancellationToken ct) =>
        await LinkOf(db, ct) is { } link ? Results.Ok(ToDto(link))
        : credentials.CanSeal ? NotLinked()
        : LinkingOff();

    // Validated by logging in and listing who the account follows, before anything is kept: a refused
    // link leaves the one she had as it was.
    private static async Task<IResult> LinkAsync(
        LinkGlucoseRequest request, SugaryDbContext db, Person person, GlucoseCredentials credentials,
        LibreLinkUpClient libreLinkUp, OneLogWriteAtATime writes, TimeProvider clock, HttpContext http,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (!credentials.CanSeal)
        {
            return LinkingOff();
        }

        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(request.Password))
        {
            return Refuse(StatusCodes.Status400BadRequest, "missing-credentials", "Type the account's email and password.");
        }

        var region = LibreLinkUpRegions.Normalize(request.Region);
        if (region is null && !string.IsNullOrWhiteSpace(request.Region))
        {
            return Refuse(StatusCodes.Status400BadRequest, "unknown-region", "A region is a code such as eu or us.");
        }

        var answer = await libreLinkUp.ConnectionsAsync(new LibreLinkUpAccount(email, request.Password, region), ct);
        if (answer is LibreLinkUpAnswer<IReadOnlyList<LibreLinkUpConnection>>.Failed failed)
        {
            return Refused(failed.Failure, http, loggers.CreateLogger(typeof(GlucoseEndpoints)));
        }

        var found = (LibreLinkUpAnswer<IReadOnlyList<LibreLinkUpConnection>>.Ok)answer;
        if (found.Value.Count == 0)
        {
            return Refuse(StatusCodes.Status400BadRequest, "no-connections",
                "This account follows nobody. Follow her sensor from the LibreLinkUp app first.");
        }

        var offered = found.Value
            .Select(c => new GlucoseConnectionOffered { PatientId = c.PatientId, Name = NameOf(c) })
            .ToList();
        var now = clock.GetUtcNow();
        using (await writes.TurnAsync(person.Account, ct))
        {
            var link = await LinkOf(db, ct);
            if (link is null)
            {
                link = new GlucoseLink();
                db.GlucoseLinks.Add(link);
            }

            link.Email = email;
            link.SealedPassword = credentials.Seal(person.Account, request.Password);
            link.Region = LibreLinkUpRegions.Normalize(found.Region);
            link.PatientId = offered.Count == 1 ? offered[0].PatientId
                : offered.Any(c => c.PatientId == link.PatientId) ? link.PatientId
                : null;
            link.Connections.Clear();
            link.Connections.AddRange(offered);
            link.LinkedAt = now;
            link.LastSuccessAt = now;
            link.LastFailure = null;
            link.FailureDetail = null;
            link.FailingSince = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(link));
        }
    }

    private static async Task<IResult> ChooseAsync(ChooseConnectionRequest request, SugaryDbContext db, CancellationToken ct)
    {
        if (await LinkOf(db, ct) is not { } link)
        {
            return NotLinked();
        }

        if (link.Connections.All(c => c.PatientId != request.PatientId))
        {
            return Refuse(StatusCodes.Status400BadRequest, "unknown-connection",
                "The account follows nobody by that id. Link it again to see who it follows now.");
        }

        link.PatientId = request.PatientId;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(link));
    }

    private static async Task<IResult> SaveSettingsAsync(
        GlucoseSettingsRequest request, SugaryDbContext db, HttpContext http, CancellationToken ct)
    {
        if (await LinkOf(db, ct) is not { } link)
        {
            return NotLinked();
        }

        if (request.Loaded is { } loaded)
        {
            var merged = StaleEdits.Merge(http, Compared(loaded), Compared(request),
                new GlucoseSettingsRequest(UnitWord(link.Unit), link.TargetLow, link.TargetHigh));
            if (merged.Value is not { } value)
            {
                return StaleEdits.Conflict(merged.Conflicts);
            }

            request = value;
        }

        if (UnitOf(request.Unit) is not { } unit)
        {
            return Refuse(StatusCodes.Status400BadRequest, "unknown-unit", "Glucose is read in mg/dL or mmol/L.");
        }

        if (!GlucoseRange.IsValid(request.TargetLow, request.TargetHigh))
        {
            return Refuse(StatusCodes.Status400BadRequest, "bad-range",
                $"A target range is from {GlucoseRange.Lowest} to {GlucoseRange.Highest} mg/dL, its low below its high.");
        }

        link.Unit = unit;
        link.TargetLow = request.TargetLow!.Value;
        link.TargetHigh = request.TargetHigh!.Value;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(link));
    }

    // Forgets the account and its password. Her readings are her log's, and stay.
    private static async Task<IResult> UnlinkAsync(SugaryDbContext db, CancellationToken ct)
    {
        if (await LinkOf(db, ct) is not { } link)
        {
            return NotLinked();
        }

        db.GlucoseLinks.Remove(link);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static Task<GlucoseLink?> LinkOf(SugaryDbContext db, CancellationToken ct) =>
        db.GlucoseLinks.Include(l => l.Connections).SingleOrDefaultAsync(ct);

    // The account's password is never logged: LibreLinkUpAccount is a record and would print it.
    private static IResult Refused(LibreLinkUpFailure failure, HttpContext http, ILogger logger)
    {
        switch (failure)
        {
            case LibreLinkUpFailure.CredentialsRefused:
                return Refuse(StatusCodes.Status400BadRequest, "credentials-refused",
                    "LibreLinkUp did not accept that email and password.");
            case LibreLinkUpFailure.StepRequired step:
                return Refuse(StatusCodes.Status400BadRequest, "step-required",
                    "Open the LibreLinkUp app and accept what it asks first.", ("component", step.Component));
            case LibreLinkUpFailure.RateLimited limited:
                var seconds = limited.RetryAfter is { } wait ? (int?)Math.Ceiling(wait.TotalSeconds) : null;
                if (seconds is not null)
                {
                    http.Response.Headers.RetryAfter = seconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return Refuse(StatusCodes.Status429TooManyRequests, "rate-limited",
                    "LibreLinkUp was asked too often. Try again later.", ("retryAfterSeconds", seconds));
            case LibreLinkUpFailure.VersionTooOld old:
                logger.LogWarning("LibreLinkUp no longer serves app version {Version}; it asks for {Minimum}",
                    LibreLinkUpClient.AppVersion, old.MinimumVersion);
                return Unavailable();
            case LibreLinkUpFailure.Unreachable unreachable:
                logger.LogWarning("LibreLinkUp gave no answer to a link: {Reason}", unreachable.Reason);
                return Unavailable();
            default:
                return Unavailable();
        }
    }

    private static IResult Unavailable() =>
        Refuse(StatusCodes.Status502BadGateway, "unavailable", "LibreLinkUp could not be reached. Try again later.");

    private static IResult NotLinked() =>
        Refuse(StatusCodes.Status404NotFound, "not-linked", "No LibreLinkUp account is linked.");

    private static IResult LinkingOff() =>
        Refuse(StatusCodes.Status503ServiceUnavailable, "linking-off",
            "Glucose can't be linked on this server: it has no sealing key.");

    private static IResult Refuse(int status, string code, string detail, params (string Name, object? Value)[] more) =>
        Results.Problem(detail, statusCode: status, extensions: more
            .Prepend(("code", code))
            .ToDictionary(field => field.Name, field => field.Value));

    private static string NameOf(LibreLinkUpConnection connection) =>
        $"{connection.FirstName} {connection.LastName}".Trim() is { Length: > 0 } name ? name : connection.PatientId;

    private static GlucoseUnit? UnitOf(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "mgdl" => GlucoseUnit.Mgdl,
        "mmol" => GlucoseUnit.Mmol,
        _ => null
    };

    private static string UnitWord(GlucoseUnit unit) => unit == GlucoseUnit.Mmol ? "mmol" : "mgdl";

    private static GlucoseSettingsRequest Compared(GlucoseSettingsRequest request) =>
        request with { Unit = request.Unit?.Trim().ToLowerInvariant(), Loaded = null };

    public static string? FailureCode(GlucoseLinkFailure? failure) => failure switch
    {
        GlucoseLinkFailure.CredentialsRefused => "credentials-refused",
        GlucoseLinkFailure.StepRequired => "step-required",
        GlucoseLinkFailure.VersionTooOld => "unavailable",
        GlucoseLinkFailure.RateLimited => "rate-limited",
        GlucoseLinkFailure.Unreachable => "unavailable",
        _ => null
    };

    private static GlucoseLinkDto ToDto(GlucoseLink link) => new(
        link.Email, link.Region, link.PatientId, link.Name,
        [.. link.Connections.Select(c => new GlucoseConnectionDto(c.PatientId, c.Name))],
        link.Unit, link.TargetLow, link.TargetHigh, link.LinkedAt, link.LastSuccessAt,
        FailureCode(link.LastFailure), link.FailureDetail, link.FailingSince);
}