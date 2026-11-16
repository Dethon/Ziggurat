using Domain.DTOs.Channel;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.Store;

// The log's database opened for one person: their own rows and their household's kitchen, and
// nothing of anyone else's. Every app request and every read of the mount opens it this way, for the
// person it acts for; what needs the whole table — the pen reminders, the import — opens the
// factory's context instead and says so with IgnoreQueryFilters.
public sealed class SugaryStore(DbContextOptions<SugaryDbContext> options)
{
    public SugaryDbContext For(Account owner) => new(options) { Owner = owner };
}