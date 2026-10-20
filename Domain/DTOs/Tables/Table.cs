namespace Domain.DTOs.Tables;

// What a column holds, which decides how every encoding of the table writes it. A cell's value is
// the CLR type each one names, or null for an empty cell.
public enum ColumnType
{
    Text, // string
    Integer, // int
    Decimal, // double
    Boolean, // bool
    Instant, // DateTimeOffset, offset on the clock the table is read on
    Date // DateOnly, a calendar day with no clock
}

public sealed record TableColumn(string Name, ColumnType Type);

// One table, typed once, so a CSV, a Parquet file and a SQL table of it are the same table.
public sealed record Table(string Name, IReadOnlyList<TableColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows)
{
    public IReadOnlyList<string> ColumnNames => [.. Columns.Select(column => column.Name)];
}