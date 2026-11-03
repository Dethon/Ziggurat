using System.Text.Json;
using System.Text.RegularExpressions;
using Dashboard.Client.Services;
using Domain.DTOs.Admin;

namespace Dashboard.Client.Metrics;

// The household list as the dashboard names households by, wherever a metric carries only an id: the
// picker and the overview's service health. Asked once per tab; a list that could not be read is
// asked again by the next reader rather than kept.
public sealed class HouseholdDirectory(HouseholdsAdminService admin)
{
    private Task<HouseholdNames>? _names;

    public async Task<HouseholdNames> NamesAsync()
    {
        var names = await (_names ??= LoadAsync());
        if (!names.Answered)
        {
            _names = null;
        }

        return names;
    }

    private async Task<HouseholdNames> LoadAsync()
    {
        try
        {
            return await admin.ListAsync() is HouseholdsAnswer<IReadOnlyList<HouseholdListEntry>>.Answered answered
                ? new HouseholdNames(answered.Value, Answered: true)
                : new HouseholdNames([], Answered: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException)
        {
            return new HouseholdNames([], Answered: false);
        }
    }
}

public sealed partial record HouseholdNames(IReadOnlyList<HouseholdListEntry> Listed, bool Answered)
{
    // Its name where the list has it, else the first eight characters of its id.
    public string Of(string id) => Listed.FirstOrDefault(h => h.Id == id)?.Name ?? id[..Math.Min(8, id.Length)];

    // A household's server reports under its tunnel's address, `<server>.<household>.tunnel`
    // (ServerAddress.Tunnelled); shown as the server and the household's name. Anything else is a
    // deployment service's own name.
    public string ServiceLabel(string service) =>
        TunnelAddress().Match(service) is { Success: true } address
            ? $"{address.Groups["server"].Value} · {Of(address.Groups["household"].Value)}"
            : service;

    [GeneratedRegex(@"^(?<server>[a-z0-9-]+)\.(?<household>[0-9a-f]{32})\.tunnel$")]
    private static partial Regex TunnelAddress();
}