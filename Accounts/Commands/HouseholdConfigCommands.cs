using System.Text.Json;
using System.Text.Json.Nodes;
using Accounts.Configuration;
using Domain.DTOs.Household;
using Domain.Security.Sealing;

namespace Accounts.Commands;

// The operator's way to read and set a household's configuration until the admin area exists. A
// section is printed at its current shape with every secret as {"sealed": true}, never its value; a
// file is saved migrated forward from whatever version it names, with each secret written in clear
// sealed on the way in and each {"sealed": true} keeping the one stored, so printing, editing and
// saving again keeps keys nobody retyped. Exit codes as the tunnel verbs': 1 for a household that
// does not exist or readers not told, 2 for a section or a document that is not one.
internal static class HouseholdConfigCommands
{
    public static async Task<int> GetAsync(
        HouseholdConfigStore store, string household, string section, TextWriter output, CancellationToken ct)
    {
        if (HouseholdSections.Named(section) is not { } schema)
        {
            await output.WriteLineAsync(UnknownSection(section));
            return 2;
        }

        try
        {
            if (await store.ReadAsync(household, schema, ct) is not { } document)
            {
                await output.WriteLineAsync($"nothing was read: there is no household {household}.");
                return 1;
            }

            await output.WriteLineAsync(SealedFields.Mask(document, schema.SecretPaths).ToJsonString(SectionSchema.Json));
            return 0;
        }
        catch (Exception ex) when (ex is HouseholdSecretsUnavailableException or SectionShapeException)
        {
            await output.WriteLineAsync($"the {section} section could not be read: {ex.Message}.");
            return 1;
        }
    }

    public static async Task<int> SetAsync(
        HouseholdConfigStore store, string household, string section, string file, TextReader input, TextWriter output,
        CancellationToken ct)
    {
        if (HouseholdSections.Named(section) is not { } schema)
        {
            await output.WriteLineAsync(UnknownSection(section));
            return 2;
        }

        JsonObject document;
        try
        {
            var text = file == "-" ? await input.ReadToEndAsync(ct) : await File.ReadAllTextAsync(file, ct);
            document = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("not an object", null, 0, 0);
        }
        catch (JsonException ex)
        {
            // Where, never the parser's message, which quotes what it read — and that may be a secret.
            await output.WriteLineAsync(
                $"nothing was saved: {(file == "-" ? "the input" : file)} is not a JSON object (line {ex.LineNumber + 1}).");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"nothing was saved: {ex.Message}");
            return 2;
        }

        SectionSave saved;
        try
        {
            saved = await store.SaveAsync(household, schema, document, ct);
        }
        catch (HouseholdSecretsUnavailableException ex)
        {
            await output.WriteLineAsync($"nothing was saved: {ex.Message}.");
            return 1;
        }

        switch (saved)
        {
            case SectionSave.NoHousehold:
                await output.WriteLineAsync($"nothing was saved: there is no household {household}.");
                return 1;
            case SectionSave.Invalid invalid:
                await output.WriteLineAsync($"nothing was saved: the {section} section is not one:");
                await output.WriteLineAsync(string.Join(Environment.NewLine, invalid.Problems.Select(p => $"  - {p}")));
                return 2;
            case SectionSave.Saved { NotTold: { } why }:
                await output.WriteLineAsync(
                    $"Saved household {household}'s {section} section at schema version {SavedVersion(saved)}. Its readers were "
                    + $"not told: {why}. They read it once what they cached runs out; run {AccountsCommand.SetHouseholdConfig} again to tell them.");
                return 1;
            default:
                await output.WriteLineAsync(
                    $"Saved household {household}'s {section} section at schema version {SavedVersion(saved)}. Its readers were told.");
                return 0;
        }
    }

    // The previous master key comes on the input, never on the command line, which the shell keeps
    // and the process table shows; the service's own (HouseholdConfig:MasterKey) is the one the data
    // keys are wrapped under from now on.
    public static async Task<int> RewrapAsync(HouseholdConfigStore store, TextReader input, TextWriter output, CancellationToken ct)
    {
        SealingKey? previous;
        try
        {
            previous = SealingKey.Parse((await input.ReadToEndAsync(ct)).Trim(), "the previous master key on the input");
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync($"nothing was rewrapped: {ex.Message}");
            return 2;
        }

        if (previous is null)
        {
            await output.WriteLineAsync("nothing was rewrapped: give the previous master key on the input.");
            return 2;
        }

        try
        {
            var (failures, rewrapped, opened) = await store.RewrapAsync(previous, ct);
            if (failures.Count > 0)
            {
                await output.WriteLineAsync("The household keys could not all be rewrapped or opened, and nothing was changed where they were not:");
                await output.WriteLineAsync(string.Join(Environment.NewLine, failures.Select(f => $"  - {f}")));
                return 1;
            }

            await output.WriteLineAsync(
                $"Rewrapped {rewrapped} household data key(s) under the current master key; every one of the {opened} sealed "
                + "secret(s) opens. The previous master key opens nothing now.");
            return 0;
        }
        catch (HouseholdSecretsUnavailableException ex)
        {
            await output.WriteLineAsync($"nothing was rewrapped: {ex.Message}.");
            return 1;
        }
    }

    private static int SavedVersion(SectionSave saved) => ((SectionSave.Saved)saved).SchemaVersion;

    private static string UnknownSection(string section) =>
        $"there is no section {section}: it is one of {string.Join(", ", HouseholdSections.All.Select(s => s.Name))}.";
}