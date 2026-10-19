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
}