using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dashboard.Client.Effects;
using Dashboard.Client.Services;
using Dashboard.Client.State.Households;
using Domain.DTOs.Admin;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.Effects;

// The household list's apps switch: the operator's choice is put to the admin API, and the row then
// says what the household is now, read again from the list; a refusal is said as a notice.
public sealed class HouseholdsEffectTests : IDisposable
{
    private const string Casa = "casa-id";

    private readonly AdminApi _api = new();
    private readonly HouseholdsStore _store = new();
    private readonly HouseholdsEffect _effect;

    public HouseholdsEffectTests() =>
        _effect = new HouseholdsEffect(
            new HouseholdsAdminService(new HttpClient(_api) { BaseAddress = new Uri("http://localhost") }), _store);

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task TurningAppsOn_PutsItToTheHousehold_AndTheRowSaysSo()
    {
        await _effect.SetAppsFeatureAsync(Casa, true);

        _api.Put.ShouldBe((AdminProtocol.AppsFeature(Casa), new HouseholdAppsFeature(true)));
        _store.State.Households.Single(h => h.Id == Casa).Apps.ShouldBeTrue();
        _store.State.Notice.ShouldBeNull();
        _store.State.Busy.ShouldBeFalse();
    }

    [Fact]
    public async Task AChangeTheAgentWasNotToldOf_IsSaidAsANotice()
    {
        _api.Warning = "Saved, but the household's agents could not be told just now.";

        await _effect.SetAppsFeatureAsync(Casa, true);

        _store.State.Notice!.Message.ShouldBe(_api.Warning);
        _store.State.Households.Single(h => h.Id == Casa).Apps.ShouldBeTrue();
    }

    [Fact]
    public async Task ARefusedChange_IsSaidAsANotice_AndTheRowIsLeftAsItWas()
    {
        _api.Refusal = new AdminRefusal("There is no household by that id.", []);

        await _effect.SetAppsFeatureAsync(Casa, true);

        _store.State.Notice!.Message.ShouldBe("There is no household by that id.");
        _store.State.Households.Single(h => h.Id == Casa).Apps.ShouldBeFalse();
    }

    // The operator's routes as the accounts service answers them: the list, and the apps feature put.
    private sealed class AdminApi : HttpMessageHandler
    {
        private bool _apps;

        public (string Path, HouseholdAppsFeature? Body)? Put { get; private set; }

        public string? Warning { get; set; }

        public AdminRefusal? Refusal { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put && path == AdminProtocol.AppsFeature(Casa))
            {
                var body = await request.Content!.ReadFromJsonAsync<HouseholdAppsFeature>(ct);
                Put = (path, body);
                if (Refusal is not null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = JsonContent.Create(Refusal) };
                }

                _apps = body!.On;
                return Json(new AdminSaved<HouseholdAppsFeature>(body, Warning));
            }

            if (request.Method == HttpMethod.Get && path == AdminProtocol.Households)
            {
                return Json(new List<HouseholdListEntry>
                {
                    new(Casa, "Casa", false, DateTimeOffset.UnixEpoch, [], [], [], null, null, _apps)
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: JsonSerializerOptions.Web) };
    }
}