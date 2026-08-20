using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.Exceptions;

namespace Domain.Tools;

// Standard error envelope for tool responses: { ok:false, errorCode, message, retryable, hint? }.
//
// Three things every failure has to answer, and the envelope answers each in one place rather than
// at the call site: what failed (the code and the message), whether trying again is worth anything
// (the code decides, never the caller), and what to do instead (the hint, which the code guarantees
// for every failure where a recovery action exists).
//
// When to call ToolError.Create explicitly:
//   - The tool has a specific code worth surfacing (e.g. captcha_required, element_not_found).
//   - The tool knows a concrete recovery action worth naming in `hint`.
//
// When to just throw and let the boundary wrap it:
//   - Generic argument/not-found failures with no extra context.
//   - Infrastructure.Utils.ToolResponse.Create(Exception) auto-wraps thrown exceptions
//     into this envelope at the MCP boundary using the same taxonomy.
//
// Two paths exist on purpose: explicit envelopes for tools that know more than the generic mapping
// can express, fall-through throws for everything else.
public static class ToolError
{
    public static class Codes
    {
        // The caller's request cannot be acted on as written.
        public const string InvalidArgument = "invalid_argument";

        // The thing named does not exist.
        public const string NotFound = "not_found";

        // It exists already, and this call would have replaced it.
        public const string AlreadyExists = "already_exists";

        // The target is real and the operation is not one it does.
        public const string UnsupportedOperation = "unsupported_operation";

        // The target is real, the operation exists, and this caller may not have it.
        public const string PermissionDenied = "permission_denied";

        // A credential is missing, expired or rejected. Configuration, not a passing condition.
        public const string Authentication = "authentication";

        // The far side is asking to be called less often.
        public const string RateLimited = "rate_limited";

        // The work did not finish inside the time it was given.
        public const string Timeout = "timeout";

        // Something this call depends on is down or unreachable right now.
        public const string TransientDependency = "transient_dependency";

        // Part of the work landed and part did not, and the caller has to know which.
        public const string PartialSuccess = "partial_success";

        // A browsing session the caller named is gone.
        public const string SessionNotFound = "session_not_found";

        // The page loaded and the element named is not on it.
        public const string ElementNotFound = "element_not_found";

        // The page is asking a human to prove they are one.
        public const string CaptchaRequired = "captcha_required";

        // Nothing above fitted. A bug or an unmapped failure, worth retrying once.
        public const string InternalError = "internal_error";
    }

    // Whether retrying is valid, and what to do instead, decided once per code.
    //
    // Retryability used to be a boolean each call site passed, and the same code went out retryable
    // from one tool and not from another — an agent then learned that a dependency being down was
    // sometimes worth waiting for and sometimes not. It is a property of what went wrong, so it
    // lives with the code.
    //
    // `Recovery` is the fallback hint for codes where a failure without a recovery action is not
    // worth reporting. A call site with something specific to say still says it; this is what the
    // envelope carries when nobody did.
    private sealed record Meaning(bool Retryable, string? Recovery = null);

    private static readonly Dictionary<string, Meaning> _taxonomy = new(StringComparer.Ordinal)
    {
        [Codes.InvalidArgument] = new(Retryable: false),
        [Codes.NotFound] = new(Retryable: false),
        [Codes.AlreadyExists] = new(Retryable: false),
        [Codes.UnsupportedOperation] = new(
            Retryable: false,
            Recovery: "Use an operation this target supports, or a target that supports this one."),
        [Codes.PermissionDenied] = new(
            Retryable: false,
            Recovery: "This caller may not do that. Ask the user to grant it, or use a target it may reach."),
        [Codes.Authentication] = new(
            Retryable: false,
            Recovery: "The credential is missing or rejected. It has to be fixed in configuration; "
                      + "repeating the call will not fix it."),
        [Codes.RateLimited] = new(
            Retryable: true,
            Recovery: "Wait before trying again, and make fewer calls."),
        [Codes.Timeout] = new(Retryable: true),
        [Codes.TransientDependency] = new(
            Retryable: true,
            Recovery: "Whatever this depends on is not answering. The same call may work shortly."),
        [Codes.PartialSuccess] = new(
            Retryable: false,
            Recovery: "Some of the work landed. Read what is reported and repeat only the rest."),
        [Codes.SessionNotFound] = new(Retryable: false),
        [Codes.ElementNotFound] = new(Retryable: false),
        [Codes.CaptchaRequired] = new(
            Retryable: false,
            Recovery: "A person has to solve the challenge; nothing here can."),
        [Codes.InternalError] = new(Retryable: true)
    };

    public static IReadOnlyCollection<string> All => _taxonomy.Keys;

    public static bool IsKnown(string errorCode) => _taxonomy.ContainsKey(errorCode);

    // An unknown code is not retryable. A code nobody declared came from outside this taxonomy —
    // a third-party MCP server, a future version — and inviting a retry loop on a failure nothing
    // here understands is the worse of the two guesses.
    public static bool IsRetryable(string errorCode) =>
        _taxonomy.TryGetValue(errorCode, out var meaning) && meaning.Retryable;

    // What the envelope says to do when the call site said nothing.
    public static string? Recovery(string errorCode) =>
        _taxonomy.TryGetValue(errorCode, out var meaning) ? meaning.Recovery : null;

    public static JsonObject Create(string errorCode, string message, string? hint = null) =>
        Result(errorCode, message, hint).ToNode();

    public static ToolErrorResult Result(string errorCode, string message, string? hint = null) =>
        new() { ErrorCode = errorCode, Message = message, Hint = hint };

    // What a thrown failure is, in the taxonomy's terms. It lives here rather than at one boundary
    // because there are two: a tool server hands its filter this mapping through ToolResponse, and
    // a channel server passes no error result at all and used to answer a bare exception message —
    // no code, no retryability, no recovery. One mapping, so a timeout reads the same from either.
    //
    // The distinctions are the ones a caller acts on differently: a rejected credential is not a
    // malformed request, a refusal is not a rate limit, and a dependency that is down is the only
    // one of them worth trying again.
    public static string CodeFor(Exception exception) => exception switch
    {
        ArgumentException => Codes.InvalidArgument,
        NotSupportedException => Codes.UnsupportedOperation,
        UnauthorizedAccessException => Codes.PermissionDenied,
        HomeAssistantNotFoundException => Codes.NotFound,
        HomeAssistantUnauthorizedException => Codes.Authentication,
        HomeAssistantException { StatusCode: 403 } => Codes.PermissionDenied,
        HomeAssistantException { StatusCode: 429 } => Codes.RateLimited,
        HomeAssistantException { StatusCode: >= 400 and < 500 } => Codes.InvalidArgument,
        // A 5xx, and no status at all: the call either fell over on the far side or never arrived.
        HomeAssistantException => Codes.TransientDependency,
        TimeoutException => Codes.Timeout,
        // An HttpClient timeout arrives as a cancellation with nobody behind it. A caller's real
        // hang-up never reaches a mapping — the call-tool filter rethrows it as the abort it is.
        OperationCanceledException => Codes.Timeout,
        HttpRequestException http => FromStatus((int?)http.StatusCode),
        SocketException => Codes.TransientDependency,
        IOException => Codes.TransientDependency,
        _ => Codes.InternalError
    };

    private static string FromStatus(int? status) => status switch
    {
        401 => Codes.Authentication,
        403 => Codes.PermissionDenied,
        404 => Codes.NotFound,
        408 => Codes.Timeout,
        409 => Codes.AlreadyExists,
        429 => Codes.RateLimited,
        >= 400 and < 500 => Codes.InvalidArgument,
        // 5xx, and no status at all: the request never got an answer worth reading.
        _ => Codes.TransientDependency
    };

    // The codes whose whole value is the recovery action, each behind a factory that will not let a
    // caller omit it. Nothing stops `Create` from producing the same codes — these exist so the
    // ordinary way to raise one is the way that says what to do about it.
    public static ToolErrorResult PermissionDenied(string message, string hint) =>
        Result(Codes.PermissionDenied, message, hint);

    public static ToolErrorResult Authentication(string message, string hint) =>
        Result(Codes.Authentication, message, hint);

    public static ToolErrorResult RateLimited(string message, string hint) =>
        Result(Codes.RateLimited, message, hint);

    public static ToolErrorResult TransientDependency(string message, string hint) =>
        Result(Codes.TransientDependency, message, hint);

    public static ToolErrorResult PartialSuccess(string message, string hint) =>
        Result(Codes.PartialSuccess, message, hint);
}