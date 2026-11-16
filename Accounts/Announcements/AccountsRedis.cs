using Accounts.Settings;
using StackExchange.Redis;

namespace Accounts.Announcements;

// The deployment's Redis, opened when first needed rather than at start, so a service or a command that
// never touches it never connects. Shared by what announces (RedisAnnouncer) and what reads another
// service's keys (HouseholdSchedules). With no connection string configured there is none.
public sealed class AccountsRedis(AccountsSettings settings) : IAsyncDisposable
{
    private readonly SemaphoreSlim _opening = new(1, 1);
    private ConnectionMultiplexer? _redis;

    public bool Configured => settings.Redis.ConnectionString.Length > 0;

    // Not aborting on a failed first connect keeps the multiplexer retrying in the background, so a
    // Redis that was down when the service first asked is used once it is back.
    public async Task<IConnectionMultiplexer> OpenAsync(CancellationToken ct)
    {
        await _opening.WaitAsync(ct);
        try
        {
            if (_redis is null)
            {
                var options = ConfigurationOptions.Parse(settings.Redis.ConnectionString);
                options.AbortOnConnectFail = false;
                _redis = await ConnectionMultiplexer.ConnectAsync(options);
            }

            return _redis;
        }
        finally
        {
            _opening.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        _opening.Dispose();
    }
}