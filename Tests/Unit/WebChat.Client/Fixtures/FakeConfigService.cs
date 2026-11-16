using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeConfigService(CallRecorder? recorder = null) : IConfigService
{
    public AppConfig Config { get; set; } = new(null);

    public int ConfigCalls { get; private set; }

    public Exception? ThrowOnGetConfig { get; set; }

    // Not recorded: the app config is read from the detached push path as well, so its
    // position in the call log would depend on when that task happens to run.
    public Task<AppConfig> GetConfigAsync()
    {
        ConfigCalls++;
        return ThrowOnGetConfig is null
            ? Task.FromResult(Config)
            : Task.FromException<AppConfig>(ThrowOnGetConfig);
    }
}