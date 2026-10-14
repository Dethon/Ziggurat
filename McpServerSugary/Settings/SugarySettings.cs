using Domain.Security;
using Mcp.Hosting;

namespace McpServerSugary.Settings;

public record SugarySettings : IMcpHostSettings
{
    // The deployment secret this server's /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    // The SQLite file, in the server's own volume. Its directory is created on first start.
    public string DatabasePath { get; init; } = "/data/sugary.db";

    // The zone every time in the tables is written in, as an IANA id. Empty means the container's
    // own zone, which compose sets from the same TZ anchor the Home Assistant container runs on —
    // the clock the /ha mount stamps glucose history in, so a meal lines up with the readings after
    // it. An id the container does not know fails startup rather than writing times on a wrong clock.
    public string HomeZone { get; init; } = "";

    // The app's own gate, apart from /mcp's: the browser routes are called by her phone, which never
    // holds the deployment secret.
    public SugaryAppSettings SugaryApp { get; init; } = new();

    // Where a barcode none of her saved foods carries is looked up.
    public string OpenFoodFactsUrl { get; init; } = "https://world.openfoodfacts.org/";
}

public record SugaryAppSettings
{
    // The code typed once per phone to get in (SUGARYAPP__ACCESSCODE, a deployment secret). Unset
    // refuses every sign-in, as an unset shared secret refuses every call.
    public string AccessCode { get; init; } = "";
}