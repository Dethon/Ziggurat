using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.DTOs.Sugary;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.Sugary.Watches;
using Infrastructure.Agents.Mcp;
using McpServerSugary.Glucose;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A glucose watch's prompt acts for a person only because that person's turn wrote the watch: the agent's
// filesystem writes it through the real Sugary server, signing a delegation for the write as the turn's
// verified person; Sugary keeps it with the watch; a fire puts the watch's prompt on Sugary's channel with
// the delegation and what the watch does now; and the agent's own check of the message its channel
// connection reads says who the turn acts for.
public class GlucoseWatchDelegationTests
{
    private const string Prompt = "Tell her she is low and to take sugar.";

    private static readonly string _watch = $$"""
        {"name":"Low","triggers":[{"trigger":"glucose","below":70}],
         "effects":[{"kind":"prompt","prompt":"{{Prompt}}"}],"deliverTo":["telegram"]}
        """;

    private static readonly HouseholdSugaryPeople _people = new([
        new SugaryPerson(SugaryServer.Her.UserId, "Her", false),
        new SugaryPerson(SugaryServer.Him.UserId, "Him", false)
    ]);

    private static readonly DateTimeOffset _at = DateTimeOffset.UtcNow;

    private static readonly GlucoseTriggerFire _low = new(0, new GlucoseLevelTrigger(GlucoseBound.Below, 70), _at,
        new SensorReading(_at, 65, GlucoseTrend.Falling), null, null, _at, null);

    [Fact]
    public async Task AWatchAPersonWrote_FiresAsThem_AskingItsPrompt_WithWhatFiredAsItsReport()
    {
        await using var server = await StartAsync();
        await WriteAsync(server, TurnOf(SugaryServer.Her), b => b.CreateAsync("low/watch.json", _watch, false, true, CancellationToken.None));

        var fire = await FireAsync(server, "low");

        fire.Content.ShouldBe(Prompt);
        fire.FireReport.ShouldNotBeNull().ShouldContain("65 mg/dL");
        fire.Origin.ShouldNotBeNull().WatchId.ShouldBe("low");
        fire.HouseholdId.ShouldBe(SugaryServer.Her.TenantId);
        (await AdmitAsync(fire)).ShouldBeOfType<FireAdmission.Delegated>().Identity.Account.ShouldBe(SugaryServer.Her);
    }

    // An edit is signed over the watch as the agent read it a moment before, with the edit on top.
    [Fact]
    public async Task AWatchAPersonEdited_FiresAsThem_AskingTheEditedPrompt()
    {
        await using var server = await StartAsync();
        await WriteAsync(server, TurnOf(SugaryServer.Her), b => b.CreateAsync("low/watch.json", _watch, false, true, CancellationToken.None));
        await WriteAsync(server, TurnOf(SugaryServer.Her), b => b.EditAsync("low/watch.json",
            [new TextEdit("take sugar", "take two tablets")], CancellationToken.None));

        var fire = await FireAsync(server, "low");

        fire.Content.ShouldBe("Tell her she is low and to take two tablets.");
        (await AdmitAsync(fire)).ShouldBeOfType<FireAdmission.Delegated>().Identity.Account.ShouldBe(SugaryServer.Her);
    }

    // A replace naming no delivery keeps the watch's own, and the agent, reading the watch before it
    // writes, signs for that delivery too.
    [Fact]
    public async Task AWatchReplacedNamingNoDelivery_KeepsItsDelivery_AndFiresAsItsWriter()
    {
        await using var server = await StartAsync();
        await WriteAsync(server, TurnOf(SugaryServer.Her), b => b.CreateAsync("low/watch.json", _watch, false, true, CancellationToken.None));
        await WriteAsync(server, TurnOf(SugaryServer.Her), b => b.CreateAsync("low/watch.json",
            _watch.Replace(""","deliverTo":["telegram"]""", ""), true, true, CancellationToken.None));

        var fire = await FireAsync(server, "low");

        fire.ReplyTo.ShouldBe([new ReplyTarget("telegram", null)]);
        (await AdmitAsync(fire)).ShouldBeOfType<FireAdmission.Delegated>().Identity.Account.ShouldBe(SugaryServer.Her);
    }

    // Nobody signed in writes no glucose watch at all; a write the agent signed nothing for — one that
    // did not come through its filesystem — leaves the watch nobody's, and its fire acts for nobody.
    [Fact]
    public async Task AWatchRewrittenWithNoDelegation_FiresForNobody()
    {
        await using var server = await StartAsync();
        await WriteAsync(server, TurnOf(SugaryServer.Her), b => b.CreateAsync("low/watch.json", _watch, false, true, CancellationToken.None));
        await WriteAsync(server, NobodysTurn(), b => b.CreateAsync("other/watch.json", _watch, false, true, CancellationToken.None),
            ok: false);
        var unsigned = await server.CallAsync("fs_edit", new Dictionary<string, object?>
        {
            ["filesystem"] = SugaryWatchFileSystem.Name,
            ["path"] = "low/watch.json",
            ["edits"] = new[] { new Dictionary<string, object?> { ["oldString"] = "take sugar", ["newString"] = "rest" } }
        });
        unsigned["ok"]?.GetValue<bool>().ShouldNotBe(false, unsigned.ToJsonString());

        (await StoredAsync(server, "low")).Delegation.ShouldBeNull();
        var fire = await FireAsync(server, "low");
        fire.Delegation.ShouldBeNull();
        (await AdmitAsync(fire)).ShouldBe(new FireAdmission.Undelegated(null));
    }

    [Fact]
    public async Task AWatchWhoseDelegationTheAgentDidNotSign_IsRefused()
    {
        await using var server = await StartAsync();
        await WriteAsync(server, TurnOf(SugaryServer.Her), b => b.CreateAsync("low/watch.json", _watch, false, true, CancellationToken.None));
        await SetDelegationAsync(server, "low",
            TestDelegations.Forged(SugaryServer.Her, ChannelIds.SignalR, GlucoseWatchDelegation.Of(await StoredAsync(server, "low"))));

        (await AdmitAsync(await FireAsync(server, "low"))).ShouldBe(new FireAdmission.Undelegated(nameof(TokenRefusal.UnknownKey)));
    }

    // The agent's key was lost: the watch's delegation verifies no more until the operator confirms it
    // through redelegate, which Sugary answers by replacing the stored token and nothing else.
    [Fact]
    public async Task AWatchWhoseKeyWasLost_FiresAsItsWriterAgain_OnceRedelegated()
    {
        await using var server = await StartAsync();
        await WriteAsync(server, TurnOf(SugaryServer.Her), b => b.CreateAsync("low/watch.json", _watch, false, true, CancellationToken.None));
        var lost = TestDelegations.Forged(SugaryServer.Her, ChannelIds.SignalR, GlucoseWatchDelegation.Of(await StoredAsync(server, "low")));
        await SetDelegationAsync(server, "low", lost);
        var keys = TestSigningKeys.Store(TestSigningKeys.Agent);
        var output = new StringWriter();

        var exit = await new Redelegation(keys, new CallTokens(keys, TimeProvider.System), TimeProvider.System,
                new TokenVerificationSettings(), new StringReader("y\n"), output)
            .RedelegateAsync(
                [new Redelegation.Server("sugary", ct => McpClient.CreateAsync(McpTestSecret.Transport(server.Endpoint), cancellationToken: ct))],
                assumeYes: false, CancellationToken.None);

        exit.ShouldBe(0, output.ToString());
        output.ToString().ShouldContain("watch low");
        var stored = await StoredAsync(server, "low");
        stored.Delegation.ShouldNotBeNull().ShouldNotBe(lost);
        (await AdmitAsync(await FireAsync(server, "low"))).ShouldBeOfType<FireAdmission.Delegated>()
            .Identity.Account.ShouldBe(SugaryServer.Her);
    }

    // A one-shot is spent by a fire that reached somebody: with no agent polling Sugary's channel its
    // prompt reached nobody and the watch stays armed for the next crossing; with one, it is spent.
    [Fact]
    public async Task AOnceWatch_IsSpentOnlyByAFireAnAgentTook()
    {
        await using var server = await StartAsync();
        await WriteAsync(server, TurnOf(SugaryServer.Her), b => b.CreateAsync("once/watch.json",
            _watch.Replace("\"deliverTo\"", "\"once\":true,\"deliverTo\""), false, true, CancellationToken.None));
        var engine = server.Services.GetRequiredService<GlucoseWatchEngine>();
        var start = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);

        await PollAsync(server, engine, start, 100);
        await PollAsync(server, engine, start + TimeSpan.FromMinutes(1), 60);
        var armed = await StoredAsync(server, "once");
        (armed.Spent, armed.Enabled).ShouldBe((false, true));
        armed.LastFiredAt.ShouldBeNull();

        var agent = await server.AgentAsync();
        await PollAsync(server, engine, start + TimeSpan.FromMinutes(2), 100);
        await PollAsync(server, engine, start + TimeSpan.FromMinutes(3), 60);

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        (await agent.Messages.FirstAsync(wait.Token)).Content.ShouldBe(Prompt);
        var spent = await StoredAsync(server, "once");
        (spent.Spent, spent.Enabled).ShouldBe((true, false));
    }

    private static Task<SugaryServer> StartAsync() =>
        SugaryServer.StartAsync(sugaryPeople: _people, configure: s => s with { Delivery = TestDelegations.Delivery });

    // A person's turn as the agent's conversation group leaves it: the identity it verified.
    private static ConversationContext TurnOf(Account person) =>
        new("jonas", "conv-1", person.UserId, new ReplyTarget(ChannelIds.SignalR, "conv-1"),
            Principal: new Principal(ChannelIds.SignalR, person.UserId, person),
            Identity: IdentityClaims.Person(person.UserId, person.TenantId, ChannelIds.SignalR, ["member"]));

    private static ConversationContext NobodysTurn() =>
        new("jonas", "sched-x", "scheduler", new ReplyTarget("scheduling", "sched-x"), OriginKind: MessageOriginKind.Schedule);

    private static async Task WriteAsync<T>(
        SugaryServer server, ConversationContext turn, Func<IFileSystemBackend, Task<FsResult<T>>> write, bool ok = true)
        where T : class
    {
        await using var client = await McpClient.CreateAsync(McpTestSecret.Transport(server.Endpoint));
        var result = await write(new McpFileSystemBackend(client, SugaryWatchFileSystem.Name, null).As(turn));
        if (ok)
        {
            result.ShouldBeOfType<FsResult<T>.Ok>(result is FsResult<T>.Err e ? e.Error.Message : null);
        }
        else
        {
            result.ShouldBeOfType<FsResult<T>.Err>();
        }
    }

    private static async Task<StoredGlucoseWatch> StoredAsync(SugaryServer server, string id) =>
        (await server.Services.GetRequiredService<IGlucoseWatches>().FindAsync(SugaryServer.Her, id, CancellationToken.None))
        .ShouldNotBeNull();

    private static async Task SetDelegationAsync(SugaryServer server, string id, string token)
    {
        await using var db = await server.Services.GetRequiredService<IDbContextFactory<SugaryDbContext>>().CreateDbContextAsync();
        await db.GlucoseWatches.IgnoreQueryFilters()
            .Where(w => w.TenantId == SugaryServer.Her.TenantId && w.Id == id)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.Delegation, token));
    }

    // The watch as it is kept now, fired through the server's own effects, and the message the agent's
    // channel connection reads off Sugary's channel for it.
    private static async Task<ChannelMessage> FireAsync(SugaryServer server, string id)
    {
        var agent = await server.AgentAsync();
        var stored = await StoredAsync(server, id);
        var effects = server.Services.GetRequiredService<IGlucoseWatchEffects>();

        var outcome = await effects.FireAsync(
            new FiredGlucoseWatch(SugaryServer.Her, stored, GlucoseWatchSpec.Parse(stored.Spec)), _low, CancellationToken.None);

        outcome.Delivered.ShouldBe(1);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await agent.Messages.FirstAsync(wait.Token);
    }

    private static Task<FireAdmission> AdmitAsync(ChannelMessage fire) =>
        TestDelegations.Verifier().VerifyFireAsync(fire, CancellationToken.None);

    // A poll that answered with a reading measured then, kept as the poller keeps one.
    private static async Task PollAsync(SugaryServer server, GlucoseWatchEngine engine, DateTimeOffset at, double mgPerDl)
    {
        await server.SaveAsync(new GlucoseReading
        {
            UserId = SugaryServer.Her.UserId, TenantId = SugaryServer.Her.TenantId, At = at, MgPerDl = mgPerDl,
            Source = GlucoseSource.Current
        });
        var reading = new SensorReading(at, mgPerDl, GlucoseTrend.Steady);
        await engine.PolledAsync(
            new GlucosePoll(SugaryServer.Her, [reading], new GlucoseLinkState(at - TimeSpan.FromDays(1), at, null, null, null), reading),
            CancellationToken.None);
    }
}