using McpServerSugary.Settings;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace McpServerSugary.Import;

// The server's one verb besides serving: `import <directory>` reads the standalone app's three
// downloads into the database the server uses, then exits. A verb rather than something the server
// does on start, because an import that ran on every start would bring back a meal she has since
// deleted for as long as the files stayed in the volume.
//
//   docker exec mcp-sugary dotnet McpServerSugary.dll import /data/import
public static class SugaryCommand
{
    public const string Import = "import";

    public static bool IsCommand(IReadOnlyList<string> args) => args is [Import, ..];

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, SugarySettings settings, TextWriter output, CancellationToken ct)
    {
        if (args is not [Import, var directory])
        {
            await output.WriteLineAsync(
                $"usage: {Import} <directory holding {SugaryImport.MealsFile}, {SugaryImport.ItemsFile} "
                + $"and {SugaryImport.FoodsFile}>");
            return 2;
        }

        await using var services = new ServiceCollection().AddSugaryStore(settings).BuildServiceProvider();
        try
        {
            var report = await SugaryImport.RunAsync(
                services.GetRequiredService<IDbContextFactory<SugaryDbContext>>(), directory, TimeProvider.System, ct);
            await output.WriteLineAsync(
                $"imported {report.MealsAdded} meals ({report.ItemsAdded} items) and {report.FoodsAdded} foods "
                + $"into {settings.DatabasePath}; {report.MealsPresent} meals and {report.FoodsPresent} foods "
                + "were already there and were left as they are.");
            return 0;
        }
        catch (ImportException ex)
        {
            await output.WriteLineAsync($"nothing was imported: {ex.Message}");
            return 1;
        }
    }
}