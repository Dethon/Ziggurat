using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.DTOs.Admin;

namespace Dashboard.Client.Services;

// What the admin API answered, as the household list tells it apart.
public abstract record HouseholdsAnswer<T>
{
    public sealed record Answered(T Value) : HouseholdsAnswer<T>;

    public sealed record Refused(AdminRefusal Refusal) : HouseholdsAnswer<T>;

    public sealed record SignedOut : HouseholdsAnswer<T>;

    public sealed record NotAllowed(string Message) : HouseholdsAnswer<T>;

    public sealed record Unreachable : HouseholdsAnswer<T>;
}

// The operator's routes of the accounts service's admin API, on this page's own host: the proxy routes
// /accounts/admin/operator/* there behind its session check for the admin channel, so the session
// cookie rides along and the check signs who this is. Every request carries the admin client's header,
// which a page on any other origin could not send without a preflight nothing answers. The dashboard's
// own client of the API: the chat client's admin area has its own (ADR 0008).
public sealed class HouseholdsAdminService(HttpClient http)
{
    private const string NotAllowedMessage = "Only the deployment's operator manages households.";

    public Task<HouseholdsAnswer<IReadOnlyList<HouseholdListEntry>>> ListAsync() =>
        SendAsync<IReadOnlyList<HouseholdListEntry>>(
            new HttpRequestMessage(HttpMethod.Get, AdminProtocol.Households),
            async content => (await content.ReadFromJsonAsync<List<HouseholdListEntry>>())!);

    public Task<HouseholdsAnswer<OwnerLink>> CreateAsync(NewHousehold household) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, AdminProtocol.Households) { Content = JsonContent.Create(household) },
            async content => (await content.ReadFromJsonAsync<AdminSaved<OwnerLink>>())!.Value);

    public Task<HouseholdsAnswer<OwnerLink>> MintOwnerRecoveryAsync(string householdId, string userId) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, AdminProtocol.OwnerRecovery(householdId, userId)),
            async content => (await content.ReadFromJsonAsync<AdminSaved<OwnerLink>>())!.Value);

    private async Task<HouseholdsAnswer<T>> SendAsync<T>(HttpRequestMessage request, Func<HttpContent, Task<T>> read)
    {
        request.Headers.Add(AdminProtocol.RequestHeader, AdminProtocol.RequestHeaderValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new HouseholdsAnswer<T>.Answered(await read(response.Content)),
                HttpStatusCode.Unauthorized => new HouseholdsAnswer<T>.SignedOut(),
                HttpStatusCode.Forbidden => new HouseholdsAnswer<T>.NotAllowed(
                    (await RefusalAsync(response))?.Message ?? NotAllowedMessage),
                HttpStatusCode.BadRequest or HttpStatusCode.NotFound when await RefusalAsync(response) is { } refusal =>
                    new HouseholdsAnswer<T>.Refused(refusal),
                _ => new HouseholdsAnswer<T>.Unreachable()
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new HouseholdsAnswer<T>.Unreachable();
        }
    }

    private static async Task<AdminRefusal?> RefusalAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<AdminRefusal>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}