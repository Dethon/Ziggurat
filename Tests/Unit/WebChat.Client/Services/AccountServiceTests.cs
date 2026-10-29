using System.Net;
using System.Text;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services;

namespace Tests.Unit.WebChat.Client.Services;

// The client learns its person from the accounts service, same-origin through the proxy, and a
// signed-out answer is what sends the page to sign in and back.
public sealed class AccountServiceTests
{
    private readonly FakeNavigationManager _navigation = new();

    [Fact]
    public async Task GetSignedInAsync_ASession_IsThePerson()
    {
        var service = Service(HttpStatusCode.OK,
            """
            { "userId": "0123abcd", "tenantId": "home", "displayName": "Laura",
              "avatarUrl": "https://example/laura.png", "roles": ["member"] }
            """);

        var person = (await service.GetSignedInAsync()).ShouldNotBeNull();

        (person.UserId, person.DisplayName, person.AvatarUrl, person.TenantId)
            .ShouldBe(("0123abcd", "Laura", "https://example/laura.png", "home"));
        person.Viewer.ShouldBe(new TopicViewer("0123abcd", "home"));
    }

    // Who sees every topic of the household is a role the session carries, so the client keeps a
    // row the same rule keeps on the server.
    [Fact]
    public async Task GetSignedInAsync_SomeoneWhoSeesAll_IsAViewerOfEveryTopic()
    {
        var service = Service(HttpStatusCode.OK,
            """
            { "userId": "0123abcd", "tenantId": "home", "displayName": "Olga",
              "avatarUrl": null, "roles": ["member", "sees-all"] }
            """);

        var person = (await service.GetSignedInAsync()).ShouldNotBeNull();

        person.Viewer.ShouldBe(new TopicViewer("0123abcd", "home", SeesAll: true));
    }

    [Fact]
    public async Task GetSignedInAsync_NoSession_IsNobody()
    {
        var service = Service(HttpStatusCode.Unauthorized, "");

        (await service.GetSignedInAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task GetSignedInAsync_AsksTheAccountsService()
    {
        var handler = new AnsweringHandler(HttpStatusCode.Unauthorized, "");
        var service = new AccountService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, _navigation);

        await service.GetSignedInAsync();

        handler.Asked.ShouldBe(["/accounts/me"]);
    }

    [Fact]
    public void SendToSignIn_GoesToSignInAndBackToThisPage()
    {
        var navigation = new FakeNavigationManager("http://localhost/x?topic=7");
        var service = new AccountService(new HttpClient(), navigation);

        service.SendToSignIn();

        navigation.NavigatedTo.ShouldHaveSingleItem().ShouldBe(
            "/accounts/sign-in?returnUrl=" + Uri.EscapeDataString("http://localhost/x?topic=7"));
    }

    private AccountService Service(HttpStatusCode status, string body) =>
        new(new HttpClient(new AnsweringHandler(status, body)) { BaseAddress = new Uri("http://localhost/") }, _navigation);

    private sealed class AnsweringHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}