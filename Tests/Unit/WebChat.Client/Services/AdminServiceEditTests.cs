using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.Services;

namespace Tests.Unit.WebChat.Client.Services;

// An admin page saving a whole section says what it loaded, so the admin API keeps another owner's
// change since then; and the API's refusal of a field both changed reaches the page as a refusal it
// shows, not as the API being unreachable.
public class AdminServiceEditTests
{
    private sealed class Recording(HttpStatusCode status, string answer) : HttpMessageHandler
    {
        public string? Sent { get; private set; }
        public HttpRequestMessage? Request { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Sent = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private static AdminService Service(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://assistants.example/") });

    [Fact]
    public async Task SavingSpaces_SendsTheSpacesThePageLoaded()
    {
        var handler = new Recording(HttpStatusCode.OK, """{"value":{"spaces":[],"agents":[]}}""");
        var loaded = new SpacesSection { Spaces = [HouseholdSpace.Default] };

        await Service(handler).SaveSpacesAsync(loaded with { Spaces = [HouseholdSpace.Default with { Name = "Home" }] }, loaded);

        var body = JsonNode.Parse(handler.Sent!)!;
        ((string)body["spaces"]![0]!["name"]!).ShouldBe("Home");
        ((string)body[AdminEdit.Loaded]!["spaces"]![0]!["name"]!).ShouldBe("Main");
    }

    [Fact]
    public async Task SavingSettingsAndFeatures_SendWhatThePageLoaded()
    {
        var settings = new Recording(HttpStatusCode.OK, """{"value":{"name":"Casa","timeZone":"Europe/Madrid"}}""");
        var features = new Recording(HttpStatusCode.OK, """{"value":{}}""");
        var loadedSettings = new HouseholdSettings { Name = "Casa", TimeZone = "Europe/Madrid" };

        await Service(settings).SaveSettingsAsync(loadedSettings with { Name = "Casa Kapoor" }, loadedSettings);
        await Service(features).SaveFeaturesAsync(new FeaturesSection { Memory = false }, new FeaturesSection());

        ((string)JsonNode.Parse(settings.Sent!)![AdminEdit.Loaded]!["name"]!).ShouldBe("Casa");
        ((bool)JsonNode.Parse(features.Sent!)![AdminEdit.Loaded]!["memory"]!).ShouldBeTrue();
    }

    [Fact]
    public async Task AFieldAnotherOwnerChangedToo_IsARefusalThePageShows()
    {
        var handler = new Recording(HttpStatusCode.Conflict,
            """{"message":"The spaces were not saved. Someone else changed space attic since this page was opened, and so did you: reload to see their change, then make yours again.","problems":["space attic"]}""");

        var answer = await Service(handler).SaveSpacesAsync(new SpacesSection(), new SpacesSection());

        answer.ShouldBeOfType<AdminAnswer<AdminSpaces>.Refused>().Refusal.Message.ShouldContain("space attic");
    }

    // The household's own Lemonade box is saved with its address and key, and removed by name alone.
    [Fact]
    public async Task ALemonadeBox_IsPutWithItsAddressAndKey_AndRemovedWithADelete()
    {
        var keys = """{"value":{"paysWithDeploymentKey":false,"telegramBots":[]}}""";
        var save = new Recording(HttpStatusCode.OK, keys);
        var remove = new Recording(HttpStatusCode.OK, keys);

        await Service(save).SaveLemonadeBoxAsync(new LemonadeBox("https://box.example.org/api/v1", "box-key"));
        await Service(remove).RemoveLemonadeBoxAsync();

        save.Request!.Method.ShouldBe(HttpMethod.Put);
        save.Request.RequestUri!.AbsolutePath.ShouldBe(AdminProtocol.LemonadeBox);
        ((string)JsonNode.Parse(save.Sent!)!["apiUrl"]!).ShouldBe("https://box.example.org/api/v1");
        remove.Request!.Method.ShouldBe(HttpMethod.Delete);
        remove.Request.RequestUri!.AbsolutePath.ShouldBe(AdminProtocol.LemonadeBox);
    }

    // A key is checked again from what is stored: the page posts to the key's check route and sends no key.
    [Fact]
    public async Task CheckingAKey_PostsToItsCheckRoute_WithNoBody()
    {
        var handler = new Recording(HttpStatusCode.OK, """{"value":{"paysWithDeploymentKey":false,"telegramBots":[]}}""");

        var answer = await Service(handler).CheckKeyAsync(AdminProtocol.TelegramBotCheck("jonas"));

        answer.ShouldBeOfType<AdminAnswer<HouseholdKeys>.Answered>();
        handler.Request!.Method.ShouldBe(HttpMethod.Post);
        handler.Request.RequestUri!.AbsolutePath.ShouldBe("/accounts/admin/household/keys/telegram/jonas/check");
        handler.Sent.ShouldBeNull();
    }
}