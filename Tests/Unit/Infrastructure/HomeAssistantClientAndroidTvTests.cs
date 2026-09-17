using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

// The apps an Android TV Remote entry is configured with live in the entry's options, and no API
// serves an entry's options — the one way to see them is the entry's options flow, whose first
// form lists them in an `apps` select. The client opens that flow, reads the form, and deletes the
// flow unanswered, so the home keeps no half-open flow and reloads nothing.
public class HomeAssistantClientAndroidTvTests : IDisposable
{
    private const string FlowForm = """
        {"type":"form","flow_id":"flow-1","handler":"entry-1","step_id":"init",
         "data_schema":[
           {"name":"apps","optional":true,"selector":{"select":{"mode":"dropdown","options":[
             {"value":"add_new","label":"Add new"},
             {"value":"crunchyroll://","label":"Crunchyroll (crunchyroll://)"},
             {"value":"https://www.netflix.com/title","label":"Netflix (https://www.netflix.com/title)"},
             {"value":"com.unnamed","label":"com.unnamed"},
             {"value":"plex://","label":"Plex (plex://)"}]}}},
           {"name":"enable_ime","type":"boolean","required":true,"default":true}]}
        """;

    private readonly WireMockServer _server;
    private readonly HomeAssistantClient _client;

    public HomeAssistantClientAndroidTvTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _client = new HomeAssistantClient(http, "test-token");
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ListAndroidTvAppsAsync_OpensTheEntrysOptionsFlow_ReadsTheAppNames_AndDeletesTheFlow()
    {
        _server.Given(Request.Create().WithPath("/api/config/config_entries/options/flow")
                .WithHeader("Authorization", "Bearer test-token").UsingPost()
                .WithBody(new JsonPartialMatcher("""{"handler":"entry-1"}""")))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(FlowForm));
        _server.Given(Request.Create().WithPath("/api/config/config_entries/options/flow/flow-1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"type":"abort"}"""));

        var apps = await _client.ListAndroidTvAppsAsync("entry-1");

        // The app that was given no name is no activity: the remote's own list says "" for it.
        apps.ShouldBe(["Crunchyroll", "Netflix", "Plex"]);
        _server.LogEntries.Select(e => $"{e.RequestMessage.Method} {e.RequestMessage.Path}")
            .ShouldBe(["POST /api/config/config_entries/options/flow", "DELETE /api/config/config_entries/options/flow/flow-1"]);
    }

    // An entry whose flow opens on something other than the form (an abort, an entry that is not
    // loaded) is configured with nothing this client can read; the flow, if one was opened, is
    // still closed.
    [Fact]
    public async Task ListAndroidTvAppsAsync_AFlowThatIsNotTheForm_IsNoApps_AndIsStillDeleted()
    {
        _server.Given(Request.Create().WithPath("/api/config/config_entries/options/flow").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"type":"abort","flow_id":"flow-2","handler":"entry-1","reason":"not_loaded"}"""));
        _server.Given(Request.Create().WithPath("/api/config/config_entries/options/flow/flow-2").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(404));

        var apps = await _client.ListAndroidTvAppsAsync("entry-1");

        apps.ShouldBeEmpty();
        _server.LogEntries.Count(e => e.RequestMessage.Method == "DELETE").ShouldBe(1);
    }

    [Fact]
    public async Task ListAndroidTvAppsAsync_AnEntryTheHomeRefuses_Throws()
    {
        _server.Given(Request.Create().WithPath("/api/config/config_entries/options/flow").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"message":"Invalid handler specified"}"""));

        await Should.ThrowAsync<HomeAssistantException>(() => _client.ListAndroidTvAppsAsync("gone"));
    }
}