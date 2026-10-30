using System.Security.Cryptography;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Agents.Mcp;
using McpServerScheduling.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// The agent's redelegate and retire-signing-keys against the real scheduling server's delegation
// store: a schedule whose delegation the agent can no longer believe — its key lost, or written before
// delegations — fires for nobody until the operator confirms it again, and a rotation's earlier key is
// replaced without asking and refuses to be retired until it has been.
public class RedelegationTests(McpSchedulingServerFixture fixture) : IClassFixture<McpSchedulingServerFixture>, IDisposable
{
    private const string Household = "4f1c9a7e2b6d4e0a9c3b8d5e7f1a2c4b";

    private static readonly Account _fran = new(Household, "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f");

    private readonly string _keyDirectory = Directory.CreateTempSubdirectory("agent-keys-").FullName;
    private readonly StringWriter _output = new();

    private IScheduleStore Store => fixture.Services.GetRequiredService<IScheduleStore>();

    public void Dispose() => Directory.Delete(_keyDirectory, recursive: true);

    // The agent's key was lost and it signs with a new one: what it signed before verifies no more,
    // and the operator re-delegates it as it claims to be.
    [Fact]
    public async Task AScheduleWhoseKeyWasLost_FiresAsItsWriterAgain_OnceTheOperatorConfirmsIt()
    {
        var schedule = await SeedAsync(s => s with
        {
            Delegation = TestDelegations.Forged(_fran, ChannelIds.SignalR, ScheduleDelegation.Of(s))
        });
        (await AdmitAsync(schedule.Id)).ShouldBe(new FireAdmission.Undelegated(nameof(TokenRefusal.UnknownKey)));

        var exit = await Redelegation(AgentKeys(), "y").RedelegateAsync(Servers(), assumeYes: false, CancellationToken.None);

        exit.ShouldBe(0);
        _output.ToString().ShouldContain($"schedule {schedule.Id}");
        _output.ToString().ShouldContain(_fran.UserId);
        (await AdmitAsync(schedule.Id)).ShouldBeOfType<FireAdmission.Delegated>().Identity.Account.ShouldBe(_fran);
    }

    [Fact]
    public async Task AScheduleWhoseKeyWasLost_StaysNobodys_WhenTheOperatorSaysNo()
    {
        var schedule = await SeedAsync(s => s with
        {
            Delegation = TestDelegations.Forged(_fran, ChannelIds.SignalR, ScheduleDelegation.Of(s))
        });

        await Redelegation(AgentKeys(), "n").RedelegateAsync(Servers(), assumeYes: false, CancellationToken.None);

        (await AdmitAsync(schedule.Id)).ShouldBe(new FireAdmission.Undelegated(nameof(TokenRefusal.UnknownKey)));
    }

    // Written before delegations: its server recorded who wrote it, which the household migration
    // stamped, and that is what the operator is offered.
    [Fact]
    public async Task AScheduleWrittenBeforeDelegations_IsOfferedAsItsRecordedWriter()
    {
        var schedule = await SeedAsync(s => s with { CreatedBy = new Principal(ChannelIds.Telegram, "fran_tg", _fran) });
        (await AdmitAsync(schedule.Id)).ShouldBe(new FireAdmission.Undelegated(null));

        await Redelegation(AgentKeys(), "y").RedelegateAsync(Servers(), assumeYes: false, CancellationToken.None);

        var identity = (await AdmitAsync(schedule.Id)).ShouldBeOfType<FireAdmission.Delegated>().Identity;
        identity.Account.ShouldBe(_fran);
        identity.ChannelId.ShouldBe(ChannelIds.Telegram);
    }

    // After a rotation the earlier key still verifies, so its delegations are signed again without a
    // question — and until they are, retiring that key is refused.
    [Fact]
    public async Task ARotatedKeysDelegations_AreReSignedWithoutAsking_AndRetiringWaitsForThem()
    {
        var earlier = Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKey());
        var keys = AgentKeys(earlier);
        var schedule = await SeedAsync(s => s with
        {
            Delegation = new TokenSigner(TestSigningKeys.Store(earlier)).Sign((Delegation)new Delegation(
                    IdentityClaims.Person(_fran.UserId, _fran.TenantId, ChannelIds.SignalR, ["member"]), ScheduleDelegation.Of(s))
                .Stamped(TokenIssuers.Agent, TokenAudiences.Delegation, DateTimeOffset.UtcNow, Delegation.ObjectLifetime))
        });

        (await Redelegation(keys, "").StrandedByRetiringAsync(Servers(), CancellationToken.None)).ShouldNotBeNull().ShouldBeGreaterThan(0);

        // Nothing to answer: stdin is empty, and an unanswered question would read as a no.
        await Redelegation(keys, "").RedelegateAsync(Servers(), assumeYes: false, CancellationToken.None);

        (await Redelegation(keys, "").StrandedByRetiringAsync(Servers(), CancellationToken.None)).ShouldBe(0);
        keys.Retire();
        (await AdmitAsync(schedule.Id, keys)).ShouldBeOfType<FireAdmission.Delegated>().Identity.Account.ShouldBe(_fran);
    }

    private async Task<Schedule> SeedAsync(Func<Schedule, Schedule> stored)
    {
        var schedule = stored(new Schedule
        {
            Id = $"redeleg-{Guid.NewGuid():N}"[..20],
            AgentId = "jonas",
            Prompt = "summarize my unread mail",
            CronExpression = "0 8 * * *",
            CreatedAt = DateTime.UtcNow,
            NextRunAt = DateTime.UtcNow.AddDays(1)
        });
        await Store.CreateAsync(schedule, CancellationToken.None);
        return schedule;
    }

    // The agent's key volume: the suite's agent key signing, which the server's gate trusts, with an
    // earlier key published before it when a test rotated.
    private SigningKeyStore AgentKeys(params string[] earlier)
    {
        File.WriteAllText(Path.Combine(_keyDirectory, SigningKeyStore.FileName),
            JsonSerializer.Serialize(new { keys = (string[])[.. earlier, TestSigningKeys.Agent] }));
        return SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory });
    }

    private Redelegation Redelegation(SigningKeyStore keys, string answers) =>
        new(keys, new CallTokens(keys, TimeProvider.System), new StringReader(string.Concat(Enumerable.Repeat($"{answers}\n", 100))), _output);

    private IReadOnlyList<Redelegation.Server> Servers() =>
        [new("scheduling", ct => McpClient.CreateAsync(McpTestSecret.Transport(fixture.McpEndpoint), cancellationToken: ct))];

    private async Task<FireAdmission> AdmitAsync(string id, SigningKeyStore? agentKeys = null)
    {
        var schedule = (await Store.GetAsync(id, CancellationToken.None)).ShouldNotBeNull();
        var fire = ScheduleFirePlanner.Plan(schedule, TestDelegations.Delivery.DefaultDeliverTo, null, DateTimeOffset.UtcNow).Payload;
        return agentKeys is null
            ? await TestDelegations.AdmitAsync(fire, "scheduling")
            : await new DelegationVerifier(agentKeys, TimeProvider.System, TestDelegations.Delivery).VerifyFireAsync(
                new ChannelMessage
                {
                    ConversationId = fire.ConversationId,
                    Content = fire.Content,
                    Sender = fire.Sender,
                    ChannelId = "scheduling",
                    AgentId = fire.AgentId,
                    Delegation = fire.Delegation,
                    ReplyTo = fire.ReplyTo,
                    Origin = fire.Origin
                }, CancellationToken.None);
    }
}