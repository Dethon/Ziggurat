using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Accounts.Store;

// Plain tables with opaque string keys, and nothing a provider has to be asked for by name: no
// generated integer identities, no raw SQL, no JSON columns. Moving to a hosted database is
// swapping UseSqlite and regenerating the migrations' column types.
public class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    private const int IdLength = 64;
    private const int ChannelLength = 32;

    // A credential id is at most 1023 bytes, which is 1364 characters of base64url.
    private const int CredentialIdLength = 1400;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Credential> Credentials => Set<Credential>();
    public DbSet<Link> Links => Set<Link>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<LinkedIdentity> LinkedIdentities => Set<LinkedIdentity>();
    public DbSet<BoundChat> BoundChats => Set<BoundChat>();

    // An instant is kept as UTC, because not every provider has an offset type and SQLite cannot
    // order or compare the text of one.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var tenant = modelBuilder.Entity<Tenant>();
        tenant.Property(t => t.Id).HasMaxLength(IdLength);
        tenant.Property(t => t.Name).HasMaxLength(200);

        var user = modelBuilder.Entity<User>();
        user.Property(u => u.Id).HasMaxLength(IdLength);
        user.Property(u => u.TenantId).HasMaxLength(IdLength);
        user.Property(u => u.DisplayName).HasMaxLength(200);
        user.Property(u => u.AvatarUrl).HasMaxLength(2000);
        user.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
        user.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Restrict);

        var credential = modelBuilder.Entity<Credential>();
        credential.Property(c => c.Id).HasMaxLength(IdLength);
        credential.Property(c => c.TenantId).HasMaxLength(IdLength);
        credential.Property(c => c.UserId).HasMaxLength(IdLength);
        credential.Property(c => c.CredentialId).HasMaxLength(CredentialIdLength);
        credential.HasIndex(c => c.CredentialId).IsUnique();
        credential.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);

        var link = modelBuilder.Entity<Link>();
        link.Property(l => l.Id).HasMaxLength(IdLength);
        link.Property(l => l.TokenHash).HasMaxLength(IdLength);
        link.HasIndex(l => l.TokenHash).IsUnique();
        link.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
        link.Property(l => l.TenantId).HasMaxLength(IdLength);
        link.Property(l => l.UserId).HasMaxLength(IdLength);
        link.Property(l => l.DisplayName).HasMaxLength(200);
        link.Property(l => l.Role).HasConversion<string>().HasMaxLength(16);
        link.Property(l => l.CreatedBy).HasMaxLength(IdLength);
        link.HasOne<Tenant>().WithMany().HasForeignKey(l => l.TenantId).OnDelete(DeleteBehavior.Restrict);
        link.HasOne<User>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Restrict);

        var session = modelBuilder.Entity<Session>();
        session.Property(s => s.Id).HasMaxLength(IdLength);
        session.Property(s => s.TokenHash).HasMaxLength(IdLength);
        session.HasIndex(s => s.TokenHash).IsUnique();
        session.Property(s => s.TenantId).HasMaxLength(IdLength);
        session.Property(s => s.UserId).HasMaxLength(IdLength);
        session.Property(s => s.CredentialId).HasMaxLength(IdLength);
        session.Property(s => s.Device).HasMaxLength(500);
        session.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        session.HasOne<Credential>().WithMany().HasForeignKey(s => s.CredentialId).OnDelete(DeleteBehavior.Restrict);

        var identity = modelBuilder.Entity<LinkedIdentity>();
        identity.Property(i => i.Id).HasMaxLength(IdLength);
        identity.Property(i => i.TenantId).HasMaxLength(IdLength);
        identity.Property(i => i.UserId).HasMaxLength(IdLength);
        identity.Property(i => i.Channel).HasMaxLength(ChannelLength);
        identity.Property(i => i.Identity).HasMaxLength(IdLength);
        // An identity is one person's, and a person has at most one on each channel: the indexes are
        // what holds two redemptions racing on one Telegram id to one winner.
        identity.HasIndex(i => new { i.Channel, i.Identity }).IsUnique();
        identity.HasIndex(i => new { i.UserId, i.Channel }).IsUnique();
        identity.HasOne<User>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Restrict);

        var chat = modelBuilder.Entity<BoundChat>();
        chat.Property(c => c.Id).HasMaxLength(IdLength);
        chat.Property(c => c.TenantId).HasMaxLength(IdLength);
        chat.Property(c => c.Channel).HasMaxLength(ChannelLength);
        chat.Property(c => c.ChatId).HasMaxLength(IdLength);
        chat.Property(c => c.BoundBy).HasMaxLength(IdLength);
        chat.HasIndex(c => new { c.Channel, c.ChatId }).IsUnique();
        chat.HasOne<Tenant>().WithMany().HasForeignKey(c => c.TenantId).OnDelete(DeleteBehavior.Restrict);
    }

    private sealed class UtcConverter() : ValueConverter<DateTimeOffset, DateTime>(
        value => value.UtcDateTime,
        stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
}

// What `dotnet ef migrations add` builds the model from, so the tool never has to boot the service.
public sealed class AccountsDesignTimeContextFactory : IDesignTimeDbContextFactory<AccountsDbContext>
{
    public AccountsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AccountsDbContext>().UseSqlite("Data Source=accounts.db").Options);
}