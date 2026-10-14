using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace McpServerSugary.App;

// Where reading a label will be answered. The standalone app called OpenRouter from here; on this
// server every model call goes through the sugary agent, which does not read labels yet (spec ticket
// 07). The app is told so by /api/session and never sends it; an installed build from before that
// asks anyway is answered with a sentence.
public static class UnavailableEndpoints
{
    public static void MapUnavailableEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/labels", () => NotYet("Reading a nutrition label")).DisableAntiforgery();
    }

    private static IResult NotYet(string action) => Results.Problem(
        $"{action} is not available yet. Add foods from your list, from a barcode or by hand.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}