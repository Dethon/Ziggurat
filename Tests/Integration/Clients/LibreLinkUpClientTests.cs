using System.Net;
using System.Text.Json.Nodes;
using Domain.DTOs.Sugary;
using McpServerSugary.Glucose;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The real client against LibreLinkUp's wire as FakeLibreLinkUp serves it: nothing here reaches Abbott.
public class LibreLinkUpClientTests
{
    private const string Email = "follower@example.com";
    private const string Password = "s3cret!";
    private const string UserId = "6edac496-d24e-11ec-8e5d-0242ac110002";

    private static readonly DateTimeOffset _now = new(2026, 11, 18, 13, 38, 50, TimeSpan.Zero);
    private static readonly LibreLinkUpAccount _account = new(Email, Password, "eu");

    private static LibreLinkUpClient ClientOf(FakeLibreLinkUp fake, TimeProvider clock) =>
        new(LibreLinkUpClient.CreateHttp(), fake.Regions, clock, NullLogger<LibreLinkUpClient>.Instance);

    private static (FakeLibreLinkUp.Account Account, FakeLibreLinkUp.Connection Connection) Seed(
        FakeLibreLinkUp fake, string region = "eu")
    {
        var account = fake.AddAccount(Email, Password, region, UserId);
        var connection = account.Follow("patient-1", "Eva", "García");
        connection.TargetLow = 75;
        connection.TargetHigh = 160;
        var sensor = new FakeLibreLinkUp.Sensor("0M00ABCD12", _now - TimeSpan.FromDays(3));
        connection.Current = sensor;
        connection.Sensors = [sensor, new FakeLibreLinkUp.Sensor("0M00OLD999", _now - TimeSpan.FromDays(17))];
        connection.Latest = (new FakeLibreLinkUp.Point(_now - TimeSpan.FromMinutes(1), 91), 4);
        connection.Graph =
        [
            .. Enumerable.Range(1, 6)
                .Select(i => new FakeLibreLinkUp.Point(_now - TimeSpan.FromMinutes(5 * i), 100 + i))
        ];
        return (account, connection);
    }

    private static T Ok<T>(LibreLinkUpAnswer<T> answer) =>
        answer.ShouldBeOfType<LibreLinkUpAnswer<T>.Ok>().Value;

    private static LibreLinkUpFailure Failure<T>(LibreLinkUpAnswer<T> answer) =>
        answer.ShouldBeOfType<LibreLinkUpAnswer<T>.Failed>().Failure;

    [Fact]
    public async Task ConnectionsAsync_LogsInAsTheAppAndReadsEachConnection()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);

        var connections = Ok(await ClientOf(fake, clock).ConnectionsAsync(_account, CancellationToken.None));

        var login = fake.Logins.ShouldHaveSingleItem();
        login.Region.ShouldBe("eu");
        login.Header("product").ShouldBe("llu.android");
        login.Header("version").ShouldBe("4.16.0");
        login.Header("content-type")!.ShouldStartWith("application/json");
        login.Header("accept-encoding")!.ShouldContain("gzip");
        login.Header("cache-control").ShouldBe("no-cache");
        login.Header("user-agent")!.ShouldNotBeNullOrWhiteSpace();
        var body = JsonNode.Parse(login.Body)!;
        body["email"]!.GetValue<string>().ShouldBe(Email);
        body["password"]!.GetValue<string>().ShouldBe(Password);

        var call = fake.Requests.Single(r => r.Path == "/eu/llu/connections");
        call.Header("authorization")!.ShouldStartWith("Bearer ");
        call.Header("account-id").ShouldBe("82ec4b02b4ec2dec9d148400544feb511f9fe8c98f849dc6173738d1948ae064");

        var connection = connections.ShouldHaveSingleItem();
        connection.PatientId.ShouldBe("patient-1");
        connection.FirstName.ShouldBe("Eva");
        connection.LastName.ShouldBe("García");
        connection.TargetLow.ShouldBe(75);
        connection.TargetHigh.ShouldBe(160);
        connection.Sensor.ShouldBe(new LibreLinkUpSensor("0M00ABCD12", _now - TimeSpan.FromDays(3), 4));
        connection.Current.ShouldBe(new SensorReading(_now - TimeSpan.FromMinutes(1), 91, GlucoseTrend.Rising));
        fake.Unhandled.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConnectionsAsync_NoConnection_IsAnEmptyList()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        fake.AddAccount(Email, Password, "eu", UserId);

        Ok(await ClientOf(fake, clock).ConnectionsAsync(_account, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ConnectionsAsync_ASensorWithNoReadingYet_HasNoCurrentReading()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        var (_, connection) = Seed(fake);
        connection.Latest = null;
        connection.Current = null;

        var read = Ok(await ClientOf(fake, clock).ConnectionsAsync(_account, CancellationToken.None)).Single();

        read.Current.ShouldBeNull();
        read.Sensor.ShouldBeNull();
    }

    [Fact]
    public async Task ConnectionsAsync_AccountInAnotherRegion_LogsInAgainThereAndSaysWhere()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake, region: "de");
        var client = ClientOf(fake, clock);

        var answer = await client.ConnectionsAsync(_account with { Region = null }, CancellationToken.None);

        answer.ShouldBeOfType<LibreLinkUpAnswer<IReadOnlyList<LibreLinkUpConnection>>.Ok>().Region.ShouldBe("de");
        Ok(answer).ShouldHaveSingleItem().PatientId.ShouldBe("patient-1");
        fake.Logins.Select(r => r.Region).ShouldBe(["global", "de"]);
        fake.Requests.Where(r => r.Path.EndsWith("/connections")).Select(r => r.Region).ShouldBe(["de"]);
    }

    [Fact]
    public async Task GraphAsync_ReadsTheCurrentReadingThePointsAndTheSensors()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);

        var graph = Ok(await ClientOf(fake, clock).GraphAsync(_account, "patient-1", CancellationToken.None));

        graph.Connection.PatientId.ShouldBe("patient-1");
        graph.Connection.Current.ShouldBe(new SensorReading(_now - TimeSpan.FromMinutes(1), 91, GlucoseTrend.Rising));
        graph.Points.Count.ShouldBe(6);
        graph.Points.Select(p => p.At).ShouldBeInOrder();
        graph.Points[0].ShouldBe(new SensorReading(_now - TimeSpan.FromMinutes(30), 106, GlucoseTrend.Unknown));
        graph.Points[^1].ShouldBe(new SensorReading(_now - TimeSpan.FromMinutes(5), 101, GlucoseTrend.Unknown));
        graph.Sensors.Select(s => s.Serial).ShouldBe(["0M00ABCD12", "0M00OLD999"]);
        graph.Sensors[1].ActivatedAt.ShouldBe(_now - TimeSpan.FromDays(17));
        fake.Requests.Last().Path.ShouldBe("/eu/llu/connections/patient-1/graph");
    }

    [Fact]
    public async Task ATokenIsReusedAcrossCalls()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        var client = ClientOf(fake, clock);

        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));
        Ok(await client.GraphAsync(_account, "patient-1", CancellationToken.None));
        Ok(await client.GraphAsync(_account, "patient-1", CancellationToken.None));

        fake.Logins.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CallsAtOnce_LogInOnce()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        var client = ClientOf(fake, clock);

        var answers = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => client.GraphAsync(_account, "patient-1", CancellationToken.None)));

        answers.ShouldAllBe(a => a is LibreLinkUpAnswer<LibreLinkUpGraph>.Ok);
        fake.Logins.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ARevokedToken_IsReplacedByOneLoginAndTheCallAnswered()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        var client = ClientOf(fake, clock);
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        fake.RevokeTokens();
        var graph = await client.GraphAsync(_account, "patient-1", CancellationToken.None);

        Ok(graph).Points.Count.ShouldBe(6);
        fake.Logins.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ATokenRefusedEvenFresh_IsReloggedOnceAndFailsAsRetryable()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        var client = ClientOf(fake, clock);
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        fake.RefuseEveryToken = true;
        var answer = await client.GraphAsync(_account, "patient-1", CancellationToken.None);

        // Retyping the password would not fix it, so it must not stop the link as refused credentials.
        Failure(answer).ShouldBeOfType<LibreLinkUpFailure.Unreachable>();
        fake.Logins.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AnExpiredToken_IsReplacedBeforeTheCallWithoutA401()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        fake.TokenLifetime = TimeSpan.FromDays(1);
        Seed(fake);
        var client = ClientOf(fake, clock);
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        fake.Logins.Count.ShouldBe(2);
        // A refused call would have been retried: two calls in all means the stale token was never sent.
        fake.Requests.Count(r => r.Path.EndsWith("/connections")).ShouldBe(2);
    }

    [Fact]
    public async Task WrongCredentials_AreRefused()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);

        var answer = await ClientOf(fake, clock)
            .ConnectionsAsync(_account with { Password = "wrong" }, CancellationToken.None);

        Failure(answer).ShouldBeOfType<LibreLinkUpFailure.CredentialsRefused>();
    }

    [Fact]
    public async Task WrongCredentials_AreNotAnsweredFromTheRightOnesToken()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        var client = ClientOf(fake, clock);
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        var answer = await client.ConnectionsAsync(_account with { Password = "wrong" }, CancellationToken.None);

        Failure(answer).ShouldBeOfType<LibreLinkUpFailure.CredentialsRefused>();
    }

    [Fact]
    public async Task AStepToAccept_IsNamed()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake).Account.Step = "AcceptToU";

        var answer = await ClientOf(fake, clock).ConnectionsAsync(_account, CancellationToken.None);

        Failure(answer).ShouldBe(new LibreLinkUpFailure.StepRequired("AcceptToU"));
    }

    [Fact]
    public async Task AVersionTooOld_SaysTheMinimum()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        fake.MinimumVersion = "4.17.0";

        var answer = await ClientOf(fake, clock).ConnectionsAsync(_account, CancellationToken.None);

        Failure(answer).ShouldBe(new LibreLinkUpFailure.VersionTooOld("4.17.0"));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(430)]
    public async Task ARateLimit_SurfacesItsRetryAfter(int status)
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        var client = ClientOf(fake, clock);
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        fake.InterruptNext((HttpStatusCode)status, null, ("Retry-After", "120"));
        var answer = await client.GraphAsync(_account, "patient-1", CancellationToken.None);

        Failure(answer).ShouldBe(new LibreLinkUpFailure.RateLimited(TimeSpan.FromMinutes(2)));
        fake.Logins.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ARateLimitOnLogin_SurfacesARetryAfterGivenAsADate()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);

        fake.InterruptNext(HttpStatusCode.TooManyRequests, null, ("Retry-After", (_now + TimeSpan.FromMinutes(5)).ToString("R")));
        var answer = await ClientOf(fake, clock).ConnectionsAsync(_account, CancellationToken.None);

        Failure(answer).ShouldBe(new LibreLinkUpFailure.RateLimited(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task ARateLimitWithoutRetryAfter_SaysNone()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);

        fake.InterruptNext(HttpStatusCode.TooManyRequests);
        var answer = await ClientOf(fake, clock).ConnectionsAsync(_account, CancellationToken.None);

        Failure(answer).ShouldBe(new LibreLinkUpFailure.RateLimited(null));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.BadGateway, "<html><body>Bad gateway</body></html>")]
    [InlineData(HttpStatusCode.OK, "<html><body>Maintenance</body></html>")]
    [InlineData(HttpStatusCode.OK, """{"status":7,"data":{}}""")]
    public async Task AnAnswerThatIsNoAnswer_IsUnreachable(HttpStatusCode status, string? body)
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        var client = ClientOf(fake, clock);
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        fake.InterruptNext(status, body);
        var answer = await client.GraphAsync(_account, "patient-1", CancellationToken.None);

        Failure(answer).ShouldBeOfType<LibreLinkUpFailure.Unreachable>();
    }

    // The service is not ours: an envelope whose data is the wrong shape is no answer, never an exception
    // out of a poll.
    [Theory]
    [InlineData("""{"status":0,"data":"oops"}""")]
    [InlineData("""{"status":0,"data":[1]}""")]
    [InlineData("""{"status":0,"data":{"connection":"none"}}""")]
    [InlineData("""{"status":4,"data":"x"}""")]
    [InlineData("""{"status":920,"data":[1]}""")]
    [InlineData("""[1,2]""")]
    public async Task AnAnswerOfTheWrongShape_FailsTyped(string body)
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        var client = ClientOf(fake, clock);
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        fake.AnswerNext(body);
        var connections = await client.ConnectionsAsync(_account, CancellationToken.None);
        fake.AnswerNext(body);
        var graph = await client.GraphAsync(_account, "patient-1", CancellationToken.None);

        connections.ShouldBeOfType<LibreLinkUpAnswer<IReadOnlyList<LibreLinkUpConnection>>.Failed>();
        graph.ShouldBeOfType<LibreLinkUpAnswer<LibreLinkUpGraph>.Failed>();
    }

    [Fact]
    public async Task AConnectionsFieldsOfTheWrongShape_AreAbsent()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        var client = ClientOf(fake, clock);
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        fake.AnswerNext("""
            {"status":0,"data":[
              {"patientId":"a","sensor":"none","glucoseMeasurement":5},
              {"patientId":"b","sensor":{"sn":"0M00MS","a":1652400270000},"glucoseMeasurement":[1]}
            ]}
            """);
        var connections = Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        connections.Select(c => c.PatientId).ShouldBe(["a", "b"]);
        connections.ShouldAllBe(c => c.Sensor == null && c.Current == null);
    }

    [Theory]
    [InlineData(1e15, null)]
    [InlineData(null, null)]
    [InlineData("soon", "later")]
    public async Task ALoginWhoseExpiryIsUnreadable_KeepsItsTokenUntilRefused(object? expires, object? duration)
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        fake.AlterTicket = ticket =>
        {
            ticket["expires"] = JsonValue.Create(expires);
            ticket["duration"] = JsonValue.Create(duration);
        };
        var client = ClientOf(fake, clock);

        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));
        clock.Advance(TimeSpan.FromDays(30));
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        fake.Logins.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AForbiddenCall_IsReloggedOnceAndAnswered()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        var client = ClientOf(fake, clock);
        Ok(await client.ConnectionsAsync(_account, CancellationToken.None));

        fake.InterruptNext(HttpStatusCode.Forbidden);
        Ok(await client.GraphAsync(_account, "patient-1", CancellationToken.None)).Points.Count.ShouldBe(6);

        fake.Logins.Count.ShouldBe(2);
    }

    [Fact]
    public async Task NothingListening_IsUnreachable()
    {
        var clock = new FakeTimeProvider(_now);
        var port = TestPort.GetAvailable();
        try
        {
            var client = new LibreLinkUpClient(
                LibreLinkUpClient.CreateHttp(),
                new LibreLinkUpRegions($"http://127.0.0.1:{port}/global/", $"http://127.0.0.1:{port}/{{region}}/"),
                clock,
                NullLogger<LibreLinkUpClient>.Instance);

            var answer = await client.ConnectionsAsync(_account, CancellationToken.None);

            Failure(answer).ShouldBeOfType<LibreLinkUpFailure.Unreachable>();
        }
        finally
        {
            TestPort.Release(port);
        }
    }

    [Fact]
    public async Task ACancelledCall_IsCancelledNotUnreachable()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => ClientOf(fake, clock).ConnectionsAsync(_account, cancelled.Token));
    }

    [Fact]
    public async Task TheRegisteredClient_KeepsItsTokenAcrossResolutions()
    {
        var clock = new FakeTimeProvider(_now);
        await using var fake = await FakeLibreLinkUp.StartAsync(clock);
        Seed(fake);
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(clock)
            .AddLibreLinkUp(fake.Regions)
            .BuildServiceProvider();

        Ok(await services.GetRequiredService<LibreLinkUpClient>().ConnectionsAsync(_account, CancellationToken.None));
        using (var scope = services.CreateScope())
        {
            Ok(await scope.ServiceProvider.GetRequiredService<LibreLinkUpClient>()
                .GraphAsync(_account, "patient-1", CancellationToken.None));
        }

        fake.Logins.Count.ShouldBe(1);
    }
}