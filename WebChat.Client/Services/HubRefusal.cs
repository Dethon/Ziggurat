using System.Net;

namespace WebChat.Client.Services;

// The proxy's session check in front of the hub said no to the connection itself: signed out (401),
// or a household that is no longer let in (403, suspended). The page and the hub sit behind the same
// check, so a reload of the page is what shows the person why.
internal static class HubRefusal
{
    public static bool Is(Exception? exception) =>
        exception switch
        {
            null => false,
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => true,
            _ => Is(exception.InnerException)
        };
}