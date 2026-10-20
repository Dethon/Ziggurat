using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Tables;

namespace Domain.Contracts;

// Answers one SQL statement over a mount's tables. The statement is the model's and is trusted with
// nothing: it sees these tables and nothing else, can change nothing that outlives the call, and is
// stopped at a time bound. Instants are read on the given clock — the one the tables' offsets are
// on — so a date in a WHERE and a time in an answer mean what they mean at home.
public interface ITableEngine
{
    Task<FsResult<FsQueryResult>> QueryAsync(
        IReadOnlyList<Table> tables, TimeZoneInfo clock, string sql, int maxRows, CancellationToken ct);

    // One table as a Parquet file, with the columns and types a statement sees, so a command reading
    // the file and a query reading the table read one table. The same table gives the same bytes
    // every time: a file's size is asked for and its bytes read in two calls.
    Task<byte[]> ParquetAsync(Table table, CancellationToken ct);
}