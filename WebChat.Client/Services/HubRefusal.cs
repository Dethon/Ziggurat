using System.Net;

namespace WebChat.Client.Services;

// The proxy's session check in front of the hub said no to the connection itself: signed out (401),
// or a household that is no longer let in (403, suspended). No reconnect gets past either.
internal static class HubRefusal
{
    public static bool Is(Exception? exception) => StatusOf(exception) is not null;

    // Whether the refusal is the person being signed out (401), rather than their household not let in.
    public static bool IsSignedOut(Exception? exception) => StatusOf(exception) == HttpStatusCode.Unauthorized;

    private static HttpStatusCode? StatusOf(Exception? exception) =>
        exception switch
        {
            null => null,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } refused =>
                refused.StatusCode,
            _ => StatusOf(exception.InnerException)
        };
}