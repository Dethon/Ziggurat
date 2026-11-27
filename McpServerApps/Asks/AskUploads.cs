using Mcp.Hosting;
using McpServerApps.Store;

namespace McpServerApps.Asks;

// The upload store an ask's images wait in until the agent fetches them (fetch_attachment on this server's
// channel), on the apps volume beside the households: never a setting of its own, so it moves with the volume.
public sealed record AskUploads(string StoragePath) : IUploadStoreSettings
{
    public static AskUploads On(string volume) => new(BuiltAppStore.UploadsIn(volume));
}