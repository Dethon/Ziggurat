using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Tables;
using Domain.Tools;
using DuckDB.NET.Data;
using DuckDB.NET.Native;

namespace McpServerSugary.Tables;

// The tables in a DuckDB of their own, opened for one statement and thrown away after it. The
// configuration is fixed when the database opens and locked there, before a single table is loaded:
// no file the server can see, no extension to install or load, bounded memory and threads, and no
// SET that could take any of it back. A statement that writes changes a copy nobody reads again.
public sealed class DuckDbTableEngine(TimeSpan? timeout = null) : ITableEngine
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public async Task<FsResult<FsQueryResult>> QueryAsync(
        IReadOnlyList<Table> tables, TimeZoneInfo clock, string sql, int maxRows, CancellationToken ct)
    {
        await using var connection = new DuckDBConnection(ConnectionString);
        await connection.OpenAsync(ct);
        Lock(connection, clock);
        foreach (var table in tables)
        {
            Load(connection, table);
        }

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(_timeout);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        // DuckDB stops a statement only when asked: the token is the asking.
        await using var interrupt = timer.Token.Register(command.Cancel);
        try
        {
            return await Task.Run(() => Answer(command, clock, maxRows), CancellationToken.None);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FsError.Fail<FsQueryResult>(ToolError.Codes.Timeout,
                $"The statement ran past {_timeout.TotalSeconds:0.#} s and was stopped.",
                "Narrow it: filter before joining, aggregate rather than listing.");
        }
        catch (DuckDBException ex)
        {
            return FsError.Invalid<FsQueryResult>(ex.Message);
        }
    }

    private const string ConnectionString =
        "DataSource=:memory:;enable_external_access=false;autoinstall_known_extensions=false;"
        + "autoload_known_extensions=false;allow_community_extensions=false;threads=2;memory_limit=256MB";

    // The clock is the one setting that cannot be given at open, so it is set here and the
    // configuration locked straight after, before anything the model wrote is run.
    private static void Lock(DuckDBConnection connection, TimeZoneInfo clock)
    {
        using var settings = connection.CreateCommand();
        settings.CommandText = $"SET TimeZone = '{clock.Id.Replace("'", "''", StringComparison.Ordinal)}'; "
                               + "SET lock_configuration = true;";
        settings.ExecuteNonQuery();
    }

    private static void Load(DuckDBConnection connection, Table table)
    {
        using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE {Quote(table.Name)} ("
                                 + string.Join(", ", table.Columns.SelectMany(Declare)) + ")";
            create.ExecuteNonQuery();
        }

        using var appender = connection.CreateAppender(table.Name);
        foreach (var row in table.Rows)
        {
            var appending = appender.CreateRow();
            foreach (var (column, value) in table.Columns.Zip(row))
            {
                appending = Append(appending, column.Type, value);
            }

            appending.EndRow();
        }
    }

    // Every instant has a sibling with its wall time on the clock it is held on, so "dinner on the
    // ninth" is a plain filter and a row still lines up with anything stamped on the same clock.
    private static IEnumerable<string> Declare(TableColumn column) => column.Type switch
    {
        ColumnType.Text => [$"{Quote(column.Name)} VARCHAR"],
        ColumnType.Integer => [$"{Quote(column.Name)} INTEGER"],
        ColumnType.Decimal => [$"{Quote(column.Name)} DOUBLE"],
        ColumnType.Boolean => [$"{Quote(column.Name)} BOOLEAN"],
        ColumnType.Instant => [$"{Quote(column.Name)} TIMESTAMPTZ", $"{Quote(column.Name + "_local")} TIMESTAMP"],
        ColumnType.Date => [$"{Quote(column.Name)} DATE"],
        _ => throw new ArgumentOutOfRangeException(nameof(column), column.Type, "No SQL type for this column.")
    };

    private static IDuckDBAppenderRow Append(IDuckDBAppenderRow row, ColumnType type, object? value) => type switch
    {
        ColumnType.Text => row.AppendValue((string?)value),
        ColumnType.Integer => row.AppendValue((int?)value),
        ColumnType.Decimal => row.AppendValue((double?)value),
        ColumnType.Boolean => row.AppendValue((bool?)value),
        ColumnType.Instant => row.AppendValue((DateTimeOffset?)value)
            .AppendValue(((DateTimeOffset?)value)?.DateTime),
        ColumnType.Date => row.AppendValue(value is DateOnly day ? DuckDBDateOnly.FromDateOnly(day) : (DuckDBDateOnly?)null),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No SQL type for this column.")
    };

    private static FsResult<FsQueryResult> Answer(DuckDBCommand command, TimeZoneInfo clock, int maxRows)
    {
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => new FsQueryColumn(reader.GetName(i), reader.GetDataTypeName(i).ToUpperInvariant()))
            .ToList();
        var rows = new List<IReadOnlyList<JsonNode?>>();
        var count = 0;
        // Counted to the end so a cut answer can say how much was cut; only the first maxRows are kept.
        while (reader.Read())
        {
            if (count++ < maxRows)
            {
                rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => Cell(reader, i, clock))]);
            }
        }

        return new FsResult<FsQueryResult>.Ok(new FsQueryResult
        {
            Path = "",
            Columns = columns,
            Rows = rows,
            RowCount = count,
            Truncated = count > rows.Count,
            Suggestion = count > rows.Count
                ? $"{count} rows; only the first {rows.Count} are here. Narrow the statement (WHERE, GROUP BY, LIMIT)."
                : null
        });
    }

    // Each value as its column's type says to write it: an instant on the home clock with its offset
    // (the stamp her CSV and the glucose history carry), a wall time and a day without one, numbers
    // and booleans as JSON's own.
    private static JsonNode? Cell(DuckDBDataReader reader, int i, TimeZoneInfo clock)
    {
        if (reader.IsDBNull(i))
        {
            return null;
        }

        var value = reader.GetValue(i);
        return reader.GetDataTypeName(i) switch
        {
            "TimestampTz" => Stamp(TimeZoneInfo.ConvertTime(
                new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc)), clock)),
            "Timestamp" => ((DateTime)value).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            _ => Plain(value)
        };
    }

    private static JsonNode? Plain(object value) => value switch
    {
        string text => text,
        bool flag => flag,
        int or long or short or sbyte or byte or ushort or uint or ulong => JsonValue.Create(Convert.ToDecimal(value)),
        double number => double.IsFinite(number) ? number : number.ToString(CultureInfo.InvariantCulture),
        float number => float.IsFinite(number) ? number : number.ToString(CultureInfo.InvariantCulture),
        decimal number => number,
        BigInteger number => JsonNode.Parse(number.ToString(CultureInfo.InvariantCulture)),
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime time => time.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        Guid id => id.ToString(),
        _ => JsonSerializer.SerializeToNode(value)
    };

    private static string Stamp(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}