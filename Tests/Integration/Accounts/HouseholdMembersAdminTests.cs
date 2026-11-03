using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Accounts.Links;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Channel;
using Domain.Security;
using Infrastructure.Agents;
using Infrastructure.Clients.Accounts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// The household's people through the admin API (seam 1 of Tenants 4c): an owner invites somebody by a
// passkey link or a Telegram code, changes a member's role and sees-all, disables and re-enables a
// member and mints one a recovery link. A disabled member is nobody to every channel — no session, no
// sign-in, no link, no Telegram, no voice — and the agent's own reader of the household's view says so,
// which is what refuses their turns and their fires (ChatMonitorAssertionTests, ChatMonitorDelegationTests);
// re-enabled, all of it comes back.
public sealed class HouseholdMembersAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";
    private const string SignalRCredential = "signalr-credential";
    private const string TelegramCredential = "telegram-credential";
    private const string VoiceCredential = "voice-credential";
    private const long LauraTelegram = 5_550_101;

    [Fact]
    public async Task AnOwner_ListsTheHouseholdsPeople_WithRoleSeesAllAndWhetherDisabled()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");

        var members = await MembersAsync(server.Admin(await server.MintSessionAsync(fran.UserId)));

        members.Members.ShouldBe(
        [
            new AdminMember(fran.UserId, "Fran", AdminMember.Owner, IsOperator: true, SeesAll: false, Disabled: false),
            new AdminMember(laura, "Laura", AdminMember.Member, IsOperator: false, SeesAll: false, Disabled: false)
        ]);
    }

    [Fact]
    public async Task AMember_IsRefusedTheMembersRoutes()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var asLaura = server.Admin(await server.MintSessionAsync(laura));

        (await asLaura.GetAsync(AdminProtocol.Members)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(asLaura, AdminProtocol.DisableMember(fran.UserId))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // The invite is shown in the answer that minted it and nowhere else: the passkey link, and the same
    // token as the deployment's bot takes it, for somebody who only uses Telegram.
    [Fact]
    public async Task AnInvite_JoinsThePersonToTheHousehold_ByPasskeyLinkOrTelegram()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var minted = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.MemberInvites)
        {
            Content = JsonContent.Create(new MemberInvite("Laura", AdminMember.Member))
        });

        minted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var link = (await minted.Content.ReadFromJsonAsync<AdminSaved<MemberLink>>())!.Value;
        var token = new Uri(link.Url).Segments[^1];
        link.TelegramStart.ShouldBe($"/start {token}");
        var page = await server.Client().GetAsync(link.Url);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("Laura");

        var started = await PostInternalAsync(server, "/accounts/internal/telegram/start",
            new { telegramId = LauraTelegram, code = token }, TelegramCredential);

        started.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MembersAsync(admin)).Members.ShouldContain(m => m.DisplayName == "Laura" && m.Role == AdminMember.Member);
        var changes = await ChangesAsync(admin);
        changes.Select(c => c.Summary).ShouldBe(["Invited somebody as a member"]);
        changes.ShouldAllBe(c => !c.Summary.Contains(token) && !c.Route.Contains(token));
    }

    // A Telegram group is bound to the household from the admin area too, now that it is the one
    // members page: the code is shown once, as the command to send in the group, binds that group to
    // the household, and is in the change log without its characters.
    [Fact]
    public async Task AGroupCode_BindsATelegramGroupToTheHousehold_AndIsShownOnce()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var minted = await PostAsync(admin, AdminProtocol.MemberTelegramGroups);

        minted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var code = (await minted.Content.ReadFromJsonAsync<AdminSaved<TelegramGroupCode>>())!.Value;
        code.Bind.ShouldStartWith("/bind ");
        var token = code.Bind["/bind ".Length..];
        (await PostInternalAsync(server, "/accounts/internal/telegram/bind", new { chatId = -100_555L, code = token }, TelegramCredential))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var changes = await ChangesAsync(admin);
        changes.Select(c => c.Summary).ShouldBe(["Minted a Telegram group code"]);
        changes.ShouldAllBe(c => !c.Summary.Contains(token) && !c.Route.Contains(token));
    }

    [Fact]
    public async Task AMember_MintsNoGroupCode()
    {
        await using var server = await StartAsync();
        await server.MintOperatorAsync("Fran");
        var asLaura = server.Admin(await server.MintSessionAsync(await server.SeedMemberAsync("Laura")));

        (await PostAsync(asLaura, AdminProtocol.MemberTelegramGroups)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnInvite_NeedsANameAndARole()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var refused = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.MemberInvites)
        {
            Content = JsonContent.Create(new MemberInvite(" ", "boss"))
        });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ADisabledMember_IsNobodyToEveryChannel_AndTheAgentIsTold()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await LinkedMemberAsync(server, fran.TenantId, "Laura");
        var lauras = await server.MintSessionAsync(laura);
        var lauraSession = await SessionIdAsync(server, lauras);
        var recovery = ((RecoveryMint.Minted)await server.Service<LinkStore>().MintRecoveryAsync(laura, CancellationToken.None)).Link;
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        await using var reader = Reader(server);
        var standing = new HouseholdAccountStanding(reader);
        var account = new Account(fran.TenantId, laura);
        (await standing.StandingAsync(account, CancellationToken.None)).ShouldBe(AccountStanding.Active);

        var disabled = await PostAsync(admin, AdminProtocol.DisableMember(laura));

        disabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await disabled.Content.ReadFromJsonAsync<AdminSaved<AdminMembers>>())!;
        answer.Warning.ShouldBeNull();
        answer.Value.Members.Single(m => m.Id == laura).Disabled.ShouldBeTrue();
        (await server.VerifyAsync(lauras, accept: "application/json")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(server, laura, lauraSession)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ResolveAsync(server, ChannelIds.Voice, laura, VoiceCredential)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ResolveAsync(server, ChannelIds.Telegram, $"{LauraTelegram}", TelegramCredential)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await PostInternalAsync(server, "/accounts/internal/telegram/web-link", new { telegramId = LauraTelegram }, TelegramCredential))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await server.Client().GetAsync(recovery.Url)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        var (exit, _) = await server.RunAsync("mint-session", "--user", laura);
        exit.ShouldNotBe(0, "a disabled member is signed in by nothing");
        await Eventually.Until(async () => await standing.StandingAsync(account, CancellationToken.None) == AccountStanding.Disabled,
            "the announcement to reach the agent's reader");
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe([$"Disabled member {laura}"]);
    }

    // Their sessions stay ended — they sign in again with the passkeys they kept — and everything else
    // that was theirs answers for them again.
    [Fact]
    public async Task AReEnabledMember_IsSomebodyAgain()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await LinkedMemberAsync(server, fran.TenantId, "Laura");
        var lauras = await server.MintSessionAsync(laura);
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        await using var reader = Reader(server);
        var standing = new HouseholdAccountStanding(reader);
        var account = new Account(fran.TenantId, laura);
        await PostAsync(admin, AdminProtocol.DisableMember(laura));
        await Eventually.Until(async () => await standing.StandingAsync(account, CancellationToken.None) == AccountStanding.Disabled,
            "the disabling to reach the agent's reader");
        server.Clock.Advance(TimeSpan.FromMinutes(1));

        var enabled = await PostAsync(admin, AdminProtocol.EnableMember(laura));

        enabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await enabled.Content.ReadFromJsonAsync<AdminSaved<AdminMembers>>())!.Value.Members.Single(m => m.Id == laura).Disabled
            .ShouldBeFalse();
        (await ResolveAsync(server, ChannelIds.Voice, laura, VoiceCredential)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ResolveAsync(server, ChannelIds.Telegram, $"{LauraTelegram}", TelegramCredential)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await server.VerifyAsync(await server.MintSessionAsync(laura))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await server.VerifyAsync(lauras, accept: "application/json")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await Eventually.Until(async () => await standing.StandingAsync(account, CancellationToken.None) == AccountStanding.Active,
            "the re-enabling to reach the agent's reader");
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe([$"Re-enabled member {laura}", $"Disabled member {laura}"]);
    }

    // The household would be one nobody manages: a disabled owner manages nothing either.
    [Fact]
    public async Task TheLastOwner_CannotBeDisabledOrDemoted()
    {
        await using var server = await StartAsync();
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(ana));

        var disabling = await PostAsync(admin, AdminProtocol.DisableMember(ana));
        var demoting = await admin.PutAsync(AdminProtocol.Member(ana), new MemberEdit(Role: AdminMember.Member));

        disabling.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        demoting.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ChangesAsync(admin)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOwner_IsTheLastOneWhenTheOtherIsDisabled()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        (await admin.PutAsync(AdminProtocol.Member(laura), new MemberEdit(Role: AdminMember.Owner))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(admin, AdminProtocol.DisableMember(laura))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await admin.PutAsync(AdminProtocol.Member(fran.UserId), new MemberEdit(Role: AdminMember.Member))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    // The operator's account is the deployment's way onto its dashboard: an owner who could disable
    // it could lock the deployment out, as one who could mint it a recovery link could take it.
    [Fact]
    public async Task AnotherOwner_CannotDisableTheOperatorOrMintThemARecoveryLink()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        await server.Admin(await server.MintSessionAsync(fran.UserId)).PutAsync(AdminProtocol.Member(laura),
            new MemberEdit(Role: AdminMember.Owner));
        var asLaura = server.Admin(await server.MintSessionAsync(laura));

        (await PostAsync(asLaura, AdminProtocol.DisableMember(fran.UserId))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(asLaura, AdminProtocol.MemberRecovery(fran.UserId))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task APersonOfAnotherHousehold_IsNobodyHere()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (_, ana) = await server.SeedHouseholdAsync("Casa", "Ana");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        (await PostAsync(admin, AdminProtocol.DisableMember(ana))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.PutAsync(AdminProtocol.Member(ana), new MemberEdit(SeesAll: true))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync(admin, AdminProtocol.MemberRecovery(ana))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The link is in the answer alone: the change log says one was minted for whom, never any of it.
    [Fact]
    public async Task ARecoveryLink_IsShownOnce_AndNeverInTheChangeLog()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));

        var minted = await PostAsync(admin, AdminProtocol.MemberRecovery(laura));

        minted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var link = (await minted.Content.ReadFromJsonAsync<AdminSaved<MemberLink>>())!.Value;
        link.TelegramLink.ShouldBeNull();
        link.TelegramStart.ShouldBeNull();
        (await (await server.Client().GetAsync(link.Url)).Content.ReadAsStringAsync()).ShouldContain("Laura");
        var token = new Uri(link.Url).Segments[^1];
        var changes = await ChangesAsync(admin);
        changes.Select(c => c.Summary).ShouldBe([$"Minted a recovery link for member {laura}"]);
        changes.ShouldAllBe(c => !c.Summary.Contains(token) && !c.Route.Contains(token));
    }

    // A link for somebody who cannot use it would be one that silently opens nothing.
    [Fact]
    public async Task ADisabledMember_IsMintedNoRecoveryLink()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        await PostAsync(admin, AdminProtocol.DisableMember(laura));

        (await PostAsync(admin, AdminProtocol.MemberRecovery(laura))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // The chat hub asks for a fresh assertion for every turn, signed with the member's roles as they
    // are then: a change is in the very next turn the member sends.
    [Fact]
    public async Task ARoleOrSeesAllChange_IsInTheMembersNextTurn()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var laura = await server.SeedMemberAsync("Laura");
        var lauraSession = await SessionIdAsync(server, await server.MintSessionAsync(laura));
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        (await RolesOfNextTurnAsync(server, laura, lauraSession)).ShouldBe(["member"]);

        var changed = await admin.PutAsync(AdminProtocol.Member(laura), new MemberEdit(AdminMember.Owner, SeesAll: true));

        changed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await changed.Content.ReadFromJsonAsync<AdminSaved<AdminMembers>>())!.Value.Members.Single(m => m.Id == laura)
            .ShouldSatisfyAllConditions(m => m.Role.ShouldBe(AdminMember.Owner), m => m.SeesAll.ShouldBeTrue());
        (await RolesOfNextTurnAsync(server, laura, lauraSession)).ShouldBe(["owner", "sees-all"]);
        (await ChangesAsync(admin)).Select(c => c.Summary).ShouldBe([$"Changed member {laura}'s role, sees all conversations"]);
    }

    private Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["agent"] = AgentCredential,
                ["mcp-channel-signalr"] = SignalRCredential,
                ["mcp-channel-telegram"] = TelegramCredential,
                ["mcp-channel-voice"] = VoiceCredential
            },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });

    // The agent's own reader of the household's configuration, keeping what it read for longer than
    // any test runs: only an announcement makes it read again.
    private HouseholdConfigurationClient Reader(AccountsServer server) =>
        new(server.Client(), AgentCredential, HouseholdConfigurationClient.AgentToken(McpTestSecret.CallTokens),
            redis.Connection, TimeProvider.System, TimeSpan.FromMinutes(10));

    // A member of the home household who linked their Telegram, as the bot redeems their code.
    private static async Task<string> LinkedMemberAsync(AccountsServer server, string home, string name)
    {
        var member = await server.SeedMemberAsync(name);
        var code = await server.Service<LinkStore>().MintTelegramCodeAsync(home, member, CancellationToken.None);
        (await PostInternalAsync(server, "/accounts/internal/telegram/start", new { telegramId = LauraTelegram, code = code.Code },
            TelegramCredential)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return member;
    }

    // The session's id, as the assertion the session check signs names it.
    private static async Task<string> SessionIdAsync(AccountsServer server, string session)
    {
        var check = await server.VerifyAsync(session);
        var assertion = await server.VerifyAssertionAsync(check.Headers.GetValues(ForwardedAccount.AssertionHeader).Single());
        return assertion.Claims.SessionId!;
    }

    private static async Task<IReadOnlyList<string>> RolesOfNextTurnAsync(AccountsServer server, string userId, string sessionId)
    {
        var refreshed = await RefreshAsync(server, userId, sessionId);
        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await refreshed.Content.ReadFromJsonAsync<JsonObject>())!;
        return (await server.VerifyAssertionAsync(answer["assertion"]!.GetValue<string>())).Claims.Roles;
    }

    private static Task<HttpResponseMessage> RefreshAsync(AccountsServer server, string userId, string sessionId) =>
        PostInternalAsync(server, "/accounts/internal/session", new { userId, sessionId }, SignalRCredential);

    private static Task<HttpResponseMessage> ResolveAsync(AccountsServer server, string channel, string identity, string credential) =>
        PostInternalAsync(server, "/accounts/internal/resolve", new { channel, identity }, credential);

    private static Task<HttpResponseMessage> PostInternalAsync(AccountsServer server, string path, object body, string credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        return server.Client().SendAsync(request);
    }

    [Fact]
    public async Task AnInvite_IsRefusedOnceItsWeekIsUp()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var invite = await InviteAsync(server.Admin(await server.MintSessionAsync(fran.UserId)), "Laura", AdminMember.Member);

        server.Clock.Advance(TimeSpan.FromDays(7));

        (await server.Client().GetAsync(invite)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await server.Client().PostAsJsonAsync("/accounts/api/enroll/options", new { token = invite.Segments[^1] }))
            .StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    // Redeemed the way Telegram's start parameter redeems it, without a passkey: the person is created as
    // the invite named them, and the link opens nothing after.
    [Fact]
    public async Task AnInviteRedeemedOnce_CreatesThePersonAndThenRefuses()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        var invite = await InviteAsync(admin, "Laura", AdminMember.Owner);

        (await PostInternalAsync(server, "/accounts/internal/telegram/start",
            new { telegramId = LauraTelegram, code = invite.Segments[^1] }, TelegramCredential)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await server.Client().GetAsync(invite)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await MembersAsync(admin)).Members.ShouldContain(m => m.DisplayName == "Laura" && m.Role == AdminMember.Owner);
    }

    [Fact]
    public async Task ChangingARole_IsWhatTheSessionCheckThenSays()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var admin = server.Admin(await server.MintSessionAsync(fran.UserId));
        var laura = await server.SeedMemberAsync("Laura");
        var lauras = await server.MintSessionAsync(laura);

        (await EditAsync(admin, laura, new MemberEdit(Role: AdminMember.Owner))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await server.VerifyAsync(lauras)).Headers.GetValues("X-Ziggurat-Roles").ShouldBe(["owner"]);
    }

    [Fact]
    public async Task WithASecondOwner_AnOwnerCanStepDown()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var session = await server.MintSessionAsync(fran.UserId);
        var admin = server.Admin(session);
        var laura = await server.SeedMemberAsync("Laura");
        await EditAsync(admin, laura, new MemberEdit(Role: AdminMember.Owner));

        (await EditAsync(admin, fran.UserId, new MemberEdit(Role: AdminMember.Member))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await server.VerifyAsync(session)).Headers.GetValues("X-Ziggurat-Roles").ShouldBe(["member,operator"]);
    }

    // The admin area's members page is the household's one: the accounts service's own, which a link
    // from before may still name, sends there.
    [Fact]
    public async Task TheOldMembersPage_SendsToTheAdminAreas()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var owner = server.SignedIn(await server.MintSessionAsync(fran.UserId));

        var old = await owner.GetAsync("/accounts/members");

        old.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        old.Headers.Location!.ToString().ShouldBe("/admin/members");
        (await owner.PageAsync("/accounts/account")).ShouldContain("href=\"/admin/members\"");
    }

    private static async Task<Uri> InviteAsync(AccountsServer.AdminClient admin, string name, string role)
    {
        var minted = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.MemberInvites)
        {
            Content = JsonContent.Create(new MemberInvite(name, role))
        });
        minted.StatusCode.ShouldBe(HttpStatusCode.OK);
        return new Uri((await minted.Content.ReadFromJsonAsync<AdminSaved<MemberLink>>())!.Value.Url);
    }

    private static Task<HttpResponseMessage> EditAsync(AccountsServer.AdminClient admin, string userId, MemberEdit edit) =>
        admin.SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Member(userId)) { Content = JsonContent.Create(edit) });

    private static Task<HttpResponseMessage> PostAsync(AccountsServer.AdminClient admin, string path) =>
        admin.SendAsync(new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { }) });

    private static async Task<AdminMembers> MembersAsync(AccountsServer.AdminClient admin)
    {
        var response = await admin.GetAsync(AdminProtocol.Members);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AdminMembers>())!;
    }

    private static async Task<IReadOnlyList<AdminChangeEntry>> ChangesAsync(AccountsServer.AdminClient admin) =>
        (await (await admin.GetAsync(AdminProtocol.HouseholdChanges)).Content.ReadFromJsonAsync<List<AdminChangeEntry>>())!;
}