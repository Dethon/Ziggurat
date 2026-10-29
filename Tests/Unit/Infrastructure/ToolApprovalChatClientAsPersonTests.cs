using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Unit.Infrastructure.Helpers;
using static Tests.Unit.Infrastructure.Helpers.ToolApprovalResponseFactory;

namespace Tests.Unit.Infrastructure;

// A call carrying asMe acts as the person on a site, and a page's own text can ask for one — so
// the person is asked the first time a conversation browses a site as them, whatever the
// whitelist says, and only approve-and-remember covers that site for the rest of it.
public class ToolApprovalChatClientAsPersonTests
{
    private const string Browse = "mcp__mcp-websearch__web_browse";
    private static readonly string[] _shippedWhitelist = ["mcp__mcp-websearch*"];

    private readonly List<string> _browsed = [];

    private AIFunction BrowseFunction() => AIFunctionFactory.Create((string url, bool asMe = false) =>
    {
        lock (_browsed)
        {
            _browsed.Add(url);
        }

        return "page";
    }, Browse);

    private static Dictionary<string, object?> Args(string url, bool? asMe = true) =>
        asMe is { } flag ? new() { ["url"] = url, ["asMe"] = flag } : new() { ["url"] = url };

    private async Task CallAsync(
        ToolApprovalChatClient client, FakeChatClient model, Dictionary<string, object?> arguments,
        ConversationContext? context = null)
    {
        model.SetNextResponse(CreateToolCallResponse(Browse, $"call-{Guid.NewGuid():N}", arguments));
        var options = new ChatOptions { Tools = [BrowseFunction()] };
        if (context is not null)
        {
            options.AdditionalProperties = new AdditionalPropertiesDictionary { ["ConversationContext"] = context };
        }

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);
    }

    [Fact]
    public async Task AWhitelistedBrowseAsThePerson_IsStillAsked_SayingWhy()
    {
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var model = new FakeChatClient();
        var client = new ToolApprovalChatClient(model, handler, "conv", _shippedWhitelist);

        await CallAsync(client, model, Args("https://shop.com/orders"));

        var asked = handler.RequestedApprovals.ShouldHaveSingleItem().ShouldHaveSingleItem();
        asked.ToolName.ShouldBe(Browse);
        asked.Screen.ShouldBe([ExecScreenCodes.AsPerson]);
        handler.AutoApprovedNotifications.ShouldBeEmpty();
        _browsed.ShouldBe(["https://shop.com/orders"]);
    }

    [Fact]
    public async Task ABrowseAsThePerson_TheyRefuse_IsNotRun()
    {
        var model = new FakeChatClient();
        var client = new ToolApprovalChatClient(
            model, new TestApprovalHandler(ToolApprovalResult.Rejected), "conv", _shippedWhitelist);

        await CallAsync(client, model, Args("https://mail.google.com/"));

        _browsed.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARememberedSite_RunsUnasked_AndIsStillNotified()
    {
        var handler = new TestApprovalHandler(ToolApprovalResult.ApprovedAndRemember);
        var model = new FakeChatClient();
        var client = new ToolApprovalChatClient(model, handler, "conv", _shippedWhitelist);

        await CallAsync(client, model, Args("https://shop.com/orders"));
        await CallAsync(client, model, Args("https://www.shop.com/account"));

        handler.RequestedApprovals.Count.ShouldBe(1);
        handler.AutoApprovedNotifications.ShouldHaveSingleItem().ShouldHaveSingleItem().ToolName.ShouldBe(Browse);
        _browsed.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RememberingOneSite_DoesNotCoverAnother()
    {
        var handler = new TestApprovalHandler(ToolApprovalResult.ApprovedAndRemember);
        var model = new FakeChatClient();
        var client = new ToolApprovalChatClient(model, handler, "conv", _shippedWhitelist);

        await CallAsync(client, model, Args("https://shop.com/orders"));
        await CallAsync(client, model, Args("https://mail.google.com/mail/u/0"));

        handler.RequestedApprovals.Count.ShouldBe(2);
        handler.AutoApprovedNotifications.ShouldBeEmpty();
    }

    // Remembering the tool for one site must not remember the tool itself: a later call as the
    // person on another site, or with a tool-level remember from an unflagged call, still asks.
    [Fact]
    public async Task ARememberedToolWithoutAsMe_DoesNotCoverACallAsThePerson()
    {
        var handler = new TestApprovalHandler(ToolApprovalResult.ApprovedAndRemember);
        var model = new FakeChatClient();
        var client = new ToolApprovalChatClient(model, handler, "conv");

        await CallAsync(client, model, Args("https://news.example/", asMe: false));
        await CallAsync(client, model, Args("https://shop.com/orders"));

        handler.RequestedApprovals.Count.ShouldBe(2);
        handler.RequestedApprovals[1].ShouldHaveSingleItem().Screen.ShouldBe([ExecScreenCodes.AsPerson]);
    }

    [Fact]
    public async Task ARememberedSite_DoesNotRememberTheToolForOrdinaryCalls()
    {
        var handler = new TestApprovalHandler(ToolApprovalResult.ApprovedAndRemember);
        var model = new FakeChatClient();
        var client = new ToolApprovalChatClient(model, handler, "conv");

        await CallAsync(client, model, Args("https://shop.com/orders"));
        await CallAsync(client, model, Args("https://news.example/", asMe: false));

        handler.RequestedApprovals.Count.ShouldBe(2);
        handler.RequestedApprovals[1].ShouldHaveSingleItem().Screen.ShouldBeNull();
    }

    // No site to scope a remembered approval to, so it is asked about every time — a remember on
    // one site never covers it either.
    [Fact]
    public async Task ACallAsThePersonWithNoUrl_IsAskedEveryTime()
    {
        var handler = new TestApprovalHandler(ToolApprovalResult.ApprovedAndRemember);
        var model = new FakeChatClient();
        var client = new ToolApprovalChatClient(model, handler, "conv", _shippedWhitelist);

        await CallAsync(client, model, Args("https://shop.com/orders"));
        await CallAsync(client, model, Args("not a url"));
        await CallAsync(client, model, Args("not a url"));

        handler.RequestedApprovals.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task AWhitelistedBrowseNotAsThePerson_RunsUnaskedAsBefore(bool? asMe)
    {
        var handler = new TestApprovalHandler(ToolApprovalResult.Rejected);
        var model = new FakeChatClient();
        var client = new ToolApprovalChatClient(model, handler, "conv", _shippedWhitelist);

        await CallAsync(client, model, Args("https://shop.com/orders", asMe));

        handler.RequestedApprovals.ShouldBeEmpty();
        handler.AutoApprovedNotifications.ShouldHaveSingleItem().ShouldHaveSingleItem().Screen.ShouldBeNull();
        _browsed.ShouldBe(["https://shop.com/orders"]);
    }

    // A schedule's or a watch's turn has nobody present: its approvals go to the channel it
    // delivers to, so asking there would post a card every morning in a fresh conversation. Those
    // fires stay as the whitelist leaves them; what bounds them is the browse server's own rule.
    [Theory]
    [InlineData(MessageOriginKind.Schedule)]
    [InlineData(MessageOriginKind.Watch)]
    [InlineData(MessageOriginKind.Download)]
    public async Task AnUnpromptedFiresBrowseAsThePerson_RunsAsTheWhitelistSays(MessageOriginKind kind)
    {
        var handler = new TestApprovalHandler(ToolApprovalResult.Rejected);
        var model = new FakeChatClient();
        var client = new ToolApprovalChatClient(model, handler, "conv", _shippedWhitelist);

        await CallAsync(client, model, Args("https://shop.com/orders"), Fire(kind));

        handler.RequestedApprovals.ShouldBeEmpty();
        _browsed.ShouldBe(["https://shop.com/orders"]);
    }

    // The wake after Done is in the person's own conversation and its approvals reach them on
    // their own channel, so it is asked like their own turn.
    [Fact]
    public async Task ALoginWakesBrowseAsThePerson_IsAsked()
    {
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var model = new FakeChatClient();
        var client = new ToolApprovalChatClient(model, handler, "conv", _shippedWhitelist);

        await CallAsync(client, model, Args("https://shop.com/orders"), Fire(MessageOriginKind.Login));

        handler.RequestedApprovals.ShouldHaveSingleItem();
    }

    private static ConversationContext Fire(MessageOriginKind kind) =>
        new("jonas", "conv", "fran_tg", new ReplyTarget("telegram", "conv"), OriginKind: kind);
}