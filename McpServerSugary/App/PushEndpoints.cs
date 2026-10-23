using Domain.DTOs;
using McpServerSugary.Reminders;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.App;

public record PushDto(string? PublicKey);

public record SavePushSubscriptionRequest(string? Endpoint, string? P256dh, string? Auth, string? Language);

// Her phone subscribing to the pen reminders. The public key is what its browser subscribes with,
// and null when the deployment has no keys: the app then offers no reminders.
public static class PushEndpoints
{
    public const string BadSubscription = "That isn't a subscription a browser's push service makes.";

    public static void MapPushEndpoints(this IEndpointRouteBuilder api)
    {
        var push = api.MapGroup("/push");
        push.MapGet("/", (SugarySettings settings) => new PushDto(Keys(settings)?.PublicKey));
        push.MapPut("/subscription", SaveAsync);
        push.MapDelete("/subscription", DeleteAsync);
    }

    // Saved again each time the app opens and when she changes its language, so one per endpoint.
    private static async Task<IResult> SaveAsync(
        SavePushSubscriptionRequest request, SugarySettings settings, SugaryDbContext db, CancellationToken ct)
    {
        if (Keys(settings) is null)
        {
            return Results.NotFound();
        }

        if (!IsSubscription(request))
        {
            return Results.Problem(BadSubscription, statusCode: StatusCodes.Status400BadRequest);
        }

        var subscription = await db.PushSubscriptions.FindAsync([request.Endpoint], ct);
        if (subscription is null)
        {
            subscription = new PushSubscription { Endpoint = request.Endpoint! };
            db.PushSubscriptions.Add(subscription);
        }

        subscription.P256dh = request.P256dh!;
        subscription.Auth = request.Auth!;
        subscription.Language = request.Language!;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(string endpoint, SugaryDbContext db, CancellationToken ct)
    {
        await db.PushSubscriptions.Where(s => s.Endpoint == endpoint).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    private static WebPushConfig? Keys(SugarySettings settings) => settings.WebPush.IsConfigured ? settings.WebPush : null;

    private static bool IsSubscription(SavePushSubscriptionRequest request) =>
        Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint)
        && endpoint.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrWhiteSpace(request.P256dh)
        && !string.IsNullOrWhiteSpace(request.Auth)
        && PenReminderText.Languages.Contains(request.Language);
}