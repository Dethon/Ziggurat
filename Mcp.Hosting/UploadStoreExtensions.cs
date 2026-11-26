using Domain.DTOs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Mcp.Hosting;

// A channel server that sends the agent files keeps them in one upload store, sweeps it, and hands
// them over by fetch_attachment: the three go together, or a file is kept that nothing hands over, or
// handed over from a store nothing sweeps.
public static class UploadStoreExtensions
{
    public static IMcpServerBuilder AddUploadStore(
        this IMcpServerBuilder builder, IUploadStoreSettings storage, RetentionSettings retention)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton(storage);
        builder.Services.TryAddSingleton(retention);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services
            .AddSingleton<UploadStore>()
            .AddHostedService<UploadSweeper>();
        return builder.WithTools<UploadStoreFetchTool>();
    }
}