using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Infrastructure.Mcp;

[Collection("MultiFileSystem")]
public class McpFileSystemBackendChunkTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task WriteChunksAsync_LargeFile_WritesAllBytes()
    {
        var bytes = Enumerable.Range(0, 600 * 1024).Select(i => (byte)(i % 256)).ToArray();
        await using var client = await CreateClient(fx.NotesEndpoint);
        var backend = new McpFileSystemBackend(client, "notes", advertisedOperations: null);

        var written = await backend.WriteChunksAsync("written.bin", SingleChunk(bytes),
            overwrite: false, createDirectories: true, CancellationToken.None);

        written.ShouldBe(bytes.Length);
        File.ReadAllBytes(Path.Combine(fx.NotesPath, "written.bin")).ShouldBe(bytes);
    }

    [Fact]
    public async Task WriteChunksAsync_EmptyEnumerable_CreatesEmptyFile()
    {
        await using var client = await CreateClient(fx.NotesEndpoint);
        var backend = new McpFileSystemBackend(client, "notes", advertisedOperations: null);

        var written = await backend.WriteChunksAsync("empty.bin", Empty(),
            overwrite: false, createDirectories: true, CancellationToken.None);

        written.ShouldBe(0L);
        var path = Path.Combine(fx.NotesPath, "empty.bin");
        File.Exists(path).ShouldBeTrue();
        File.ReadAllBytes(path).Length.ShouldBe(0);
    }

    [Fact]
    public async Task ReadChunksAsync_MultiMegabyteFile_RoundTripsByteForByte()
    {
        // ~8 MB — exercises ~32 fs_blob_read calls against the real transport.
        var bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(fx.LibraryPath, "huge.bin"), bytes);

        await using var readClient = await CreateClient(fx.LibraryEndpoint);
        await using var writeClient = await CreateClient(fx.NotesEndpoint);
        var src = new McpFileSystemBackend(readClient, "library", advertisedOperations: null);
        var dst = new McpFileSystemBackend(writeClient, "notes", advertisedOperations: null);

        var written = await dst.WriteChunksAsync("huge.bin",
            src.ReadChunksAsync("huge.bin", CancellationToken.None),
            overwrite: false, createDirectories: true, CancellationToken.None);

        written.ShouldBe(bytes.Length);
        File.ReadAllBytes(Path.Combine(fx.NotesPath, "huge.bin")).ShouldBe(bytes);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleChunk(byte[] bytes)
    {
        await Task.Yield();
        yield return bytes;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task<McpClient> CreateClient(string endpoint)
    {
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
    }
}