using System.Globalization;
using Domain.DTOs.Channel;
using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSugary.Import;

// `import-glucose`: one person's glucose carried over from Home Assistant (GlucoseImport), run by the
// operator in the container, which already reaches the home:
//
//   docker exec mcp-sugary dotnet McpServerSugary.dll import-glucose --ha http://<home>:8123 \
//     --token-file /data/ha-token --entity sensor.<glucose> --tenant <household id> --user <user id>
//
// Home Assistant is asked through the deployment's own client (Infrastructure, which this server
// already references), so the history and statistics reads are the ones the HA mount runs. The
// long-lived token opens the whole home, so it is read from a file and never taken as an argument —
// a shell's history and the process list keep those — and no line this prints carries an argument's
// value but the entity's, the URL's and the token file's path.
public static class GlucoseImportCommand
{
    public const string Verb = "import-glucose";
    public const int DefaultDays = 90;

    private static readonly string _usage =
        $"usage: {Verb} --ha <url> --token-file <path> --entity <sensor id> --tenant <household id> "
        + $"--user <user id> [--days N (default {DefaultDays})]";

    private static readonly string[] _flags = ["--ha", "--token-file", "--entity", "--tenant", "--user", "--days"];

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, SugarySettings settings, TextWriter output, TimeProvider time, CancellationToken ct)
    {
        if (args.Contains("--token"))
        {
            await output.WriteLineAsync(
                "a token is not taken on the command line, where a shell's history and the process list keep it: "
                + "write it to a file only you can read and pass --token-file <path>.");
            return 2;
        }

        var flags = Parse(args.Skip(1).ToList());
        var problem = flags is null ? "" : Problem(flags);
        if (flags is null || problem is not null)
        {
            await output.WriteLineAsync(problem is { Length: > 0 } ? $"{problem}\n{_usage}" : _usage);
            return 2;
        }

        var person = new Account(flags["--tenant"], flags["--user"]);
        var entity = flags["--entity"];
        var days = flags.TryGetValue("--days", out var given) ? int.Parse(given, CultureInfo.InvariantCulture) : DefaultDays;
        try
        {
            var token = ReadToken(flags["--token-file"]);
            using var http = new HttpClient { BaseAddress = new Uri(flags["--ha"].TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(2) };
            var home = new HomeAssistantClient(http, token, webSocketTimeout: TimeSpan.FromMinutes(2));
            await using var services = new ServiceCollection().AddSugaryStore(settings).BuildServiceProvider();

            var report = await GlucoseImport.RunAsync(
                services.GetRequiredService<IDbContextFactory<SugaryDbContext>>(), home,
                new GlucoseImportRequest(person, entity, days), time, ct);
            await output.WriteLineAsync(
                $"imported {report.Imported} readings and {report.Hourly} hourly means of {entity} for user "
                + $"{person.UserId}; skipped {report.Skipped} already there (within 2.5 minutes of a reading of "
                + $"theirs, or an hour they have readings in) and {report.NotReadings} that were not readings "
                + "(unavailable, unknown or not a positive number).");
            return 0;
        }
        catch (Exception ex) when (ex is ImportException or HomeAssistantException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            await output.WriteLineAsync($"nothing was imported: {ex.Message}");
            return 1;
        }
    }

    // Each flag once, each with a value; anything else is no command this verb knows.
    private static Dictionary<string, string>? Parse(IReadOnlyList<string> rest)
    {
        if (rest.Count % 2 != 0)
        {
            return null;
        }

        var pairs = Enumerable.Range(0, rest.Count / 2).Select(i => (Flag: rest[2 * i], Value: rest[2 * i + 1])).ToList();
        return pairs.All(p => _flags.Contains(p.Flag)) && pairs.DistinctBy(p => p.Flag).Count() == pairs.Count
            ? pairs.ToDictionary(p => p.Flag, p => p.Value)
            : null;
    }

    private static string? Problem(IReadOnlyDictionary<string, string> flags)
    {
        var missing = _flags.Where(f => f != "--days" && (!flags.TryGetValue(f, out var value) || string.IsNullOrWhiteSpace(value))).ToList();
        if (missing.Count > 0)
        {
            return $"missing {string.Join(", ", missing)}.";
        }
        if (!Uri.TryCreate(flags["--ha"], UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return "--ha is not an http(s) address.";
        }
        if (flags["--tenant"] == Owners.Unassigned || flags["--user"] == Owners.Unassigned)
        {
            return $"'{Owners.Unassigned}' is nobody: name the person's own ids.";
        }
        return flags.TryGetValue("--days", out var days)
               && (!int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            ? "--days is not a whole number of days, one or more."
            : null;
    }

    private static string ReadToken(string path)
    {
        string token;
        try
        {
            token = File.ReadAllText(path).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportException($"the token file {path} cannot be read ({ex.GetType().Name}).");
        }

        return token.Length > 0 ? token : throw new ImportException($"the token file {path} is empty.");
    }
}