using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Sugary;
using Domain.Tools.FileSystem;
using Domain.Tools.Sugary;
using Domain.Tools.Sugary.Vfs;
using DuckDB.NET.Data;
using Infrastructure.Agents;
using McpServerSugary.Tables;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// Her tables as files: Parquet that any reader of Parquet reads, refused to the text read with what
// works instead. The bytes are what a sandbox command gets through the bridge, which reads a mount's
// bytes first.
public class SugaryParquetFilesTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
    private static readonly Guid _paella = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly InMemorySugaryLog _log = new();
    private readonly FileSystemBackendBase _mount;
    private readonly VirtualFileSystemRegistry _registry = new();

    public SugaryParquetFilesTests()
    {
        _log.Meals.AddRange(
        [
            new LoggedMeal(_paella, DateTimeOffset.Parse("2026-08-09T12:30:00Z"), new CarbRange(42, 70, 112), null,
                MealOutcome.High, "=ate out",
                [new LoggedItem(0, "paella", null, 300, 23, new CarbRange(42, 70, 112), ItemSource.Estimate, null, false, null)]),
            new LoggedMeal(Guid.NewGuid(), DateTimeOffset.Parse("2026-07-12T12:30:00Z"), new CarbRange(20, 30, 40), null,
                MealOutcome.InRange, null,
                [new LoggedItem(0, "toast", null, null, null, new CarbRange(20, 30, 40), ItemSource.Label, null, false, null)])
        ]);
        _log.Doses.Add(new LoggedDose(Guid.NewGuid(), DateTimeOffset.Parse("2026-08-09T12:25:00Z"), 7, DoseKind.Rapid,
            [_paella], null));
        _mount = new SugaryFileSystem(_log, _madrid, new DuckDbTableEngine()).For(InMemorySugaryLog.Her);
        _registry.Mount(new FileSystemMount(SugaryFileSystem.Name, "/sugary", "her log"), _mount);
    }

    [Fact]
    public async Task HerTables_AreListedAsParquet_MonthByMonth()
    {
        var glob = await new VfsGlobFilesTool(_registry).RunAsync("/sugary", "**");

        glob["entries"]!.AsArray().Select(e => e!.GetValue<string>()).ShouldBe([
            "/sugary/README.md", "/sugary/day-notes.parquet", "/sugary/dose-meals.parquet", "/sugary/doses/", "/sugary/doses/2026-08.parquet",
            "/sugary/exercise/", "/sugary/foods.parquet", "/sugary/items/", "/sugary/items/2026-07.parquet",
            "/sugary/items/2026-08.parquet", "/sugary/meals/", "/sugary/meals/2026-07.parquet",
            "/sugary/meals/2026-08.parquet", "/sugary/periods.parquet"
        ]);
    }

    [Fact]
    public async Task ReadingATableAsText_IsRefused_NamingQueryAndTheSandbox()
    {
        var read = await new VfsFileReadTool(_registry).RunAsync("/sugary/meals/2026-08.parquet");

        read["ok"]!.GetValue<bool>().ShouldBeFalse();
        read["errorCode"]!.GetValue<string>().ShouldBe("unsupported_operation");
        var hint = read["hint"]!.GetValue<string>();
        hint.ShouldContain("query");
        hint.ShouldContain("/sugary/meals/2026-08.parquet");
    }

    [Fact]
    public async Task AMonthWithNothingLogged_IsNotThere()
    {
        var read = await new VfsFileReadTool(_registry).RunAsync("/sugary/meals/2026-04.parquet");
        var info = await new VfsFileInfoTool(_registry).RunAsync("/sugary/meals/2026-04.parquet");

        read["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        info["exists"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task TheReadme_IsStillText()
    {
        var read = await new VfsFileReadTool(_registry).RunAsync("/sugary/README.md");

        read["content"]!.GetValue<string>().ShouldStartWith("# Sugary");
        Encoding.UTF8.GetString(await BytesAsync("/README.md")).ShouldBe(SugaryReadme.For(_madrid));
    }

    [Fact]
    public async Task AMonthsFile_IsParquetOfThatMonthsRows_WithTheTypesAQueryWouldSee()
    {
        var bytes = await BytesAsync("/meals/2026-08.parquet");

        var (columns, rows) = await ReadParquetAsync(bytes,
            "select meal_id, eaten_at_local::varchar, carbs_g, notes from t");

        columns["meal_id"].ShouldBe("VARCHAR");
        columns["eaten_at"].ShouldBe("TIMESTAMP WITH TIME ZONE");
        columns["eaten_at_local"].ShouldBe("TIMESTAMP");
        columns["carbs_g"].ShouldBe("DOUBLE");
        rows.Single().ShouldBe([_paella.ToString(), "2026-08-09 14:30:00", 70d, "=ate out"]);
    }

    [Fact]
    public async Task ATablesSize_IsTheLengthItsBytesAre_AndTheyAreTheSameEveryTime()
    {
        var first = await BytesAsync("/items/2026-08.parquet");
        var second = await BytesAsync("/items/2026-08.parquet");
        var info = await new VfsFileInfoTool(_registry).RunAsync("/sugary/items/2026-08.parquet");

        second.ShouldBe(first);
        info["size"]!.GetValue<long>().ShouldBe(first.Length);
    }

    [Fact]
    public async Task ATableNothingWasRecordedIn_IsStillThere_WithItsColumns()
    {
        var (columns, rows) = await ReadParquetAsync(await BytesAsync("/day-notes.parquet"), "select * from t");

        columns.Keys.ShouldBe(["date", "note"]);
        columns["date"].ShouldBe("DATE");
        rows.ShouldBeEmpty();
    }

    private async Task<byte[]> BytesAsync(string path)
    {
        using var bytes = new MemoryStream();
        await foreach (var chunk in _mount.ReadChunksAsync(path, CancellationToken.None))
        {
            bytes.Write(chunk.Span);
        }

        return bytes.ToArray();
    }

    // Read back by a reader that is not the writer's own path: the file goes to disk and DuckDB opens
    // it as any Parquet file, the way a command in the sandbox would.
    private static async Task<(Dictionary<string, string> Columns, List<object?[]> Rows)> ReadParquetAsync(
        byte[] bytes, string select)
    {
        var file = Path.Combine(Path.GetTempPath(), $"sugary-{Guid.NewGuid():N}.parquet");
        await File.WriteAllBytesAsync(file, bytes);
        try
        {
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();
            await using (var view = connection.CreateCommand())
            {
                view.CommandText = $"create view t as select * from read_parquet('{file}')";
                await view.ExecuteNonQueryAsync();
            }

            var columns = new Dictionary<string, string>();
            await using (var describe = connection.CreateCommand())
            {
                describe.CommandText = "describe t";
                await using var reader = await describe.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    columns[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var rows = new List<object?[]>();
            await using (var query = connection.CreateCommand())
            {
                query.CommandText = select;
                await using var reader = await query.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i))]);
                }
            }

            return (columns, rows);
        }
        finally
        {
            File.Delete(file);
        }
    }
}