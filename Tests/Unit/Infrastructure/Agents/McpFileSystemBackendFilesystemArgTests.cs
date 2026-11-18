using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Infrastructure.Agents.Mcp;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class McpFileSystemBackendFilesystemArgTests
{
    private sealed class CapturingBackend() : McpFileSystemBackend(null!, "downloads", advertisedOperations: null)
    {
        public List<(string Tool, Dictionary<string, object?> Args)> Calls { get; } = [];

        protected internal override Task<JsonNode> CallToolAsync(
            string toolName, Dictionary<string, object?> args, CancellationToken ct)
        {
            Calls.Add((toolName, args));
            JsonNode payload = toolName switch
            {
                "fs_blob_read" => new JsonObject { ["contentBase64"] = "", ["eof"] = true },
                _ => new JsonObject
                {
                    ["entries"] = new JsonArray(),
                    ["truncated"] = false,
                    ["total"] = 0
                }
            };
            return Task.FromResult(payload);
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> EmptyChunks()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Fact]
    public async Task EveryCall_CarriesTheFilesystemName()
    {
        var backend = new CapturingBackend();

        await backend.GlobAsync("/", "*", CancellationToken.None);
        await backend.GlobAsync("/sub", "*.json", CancellationToken.None);

        backend.Calls.ShouldAllBe(c => Equals(c.Args["filesystem"], "downloads"));
    }

    [Fact]
    public async Task ReadChunks_CarriesTheFilesystemName()
    {
        var backend = new CapturingBackend();

        await foreach (var _ in backend.ReadChunksAsync("42/x", CancellationToken.None))
        {
        }

        backend.Calls.ShouldContain(c => c.Tool == "fs_blob_read");
        backend.Calls
            .Where(c => c.Tool == "fs_blob_read")
            .ShouldAllBe(c => Equals(c.Args["filesystem"], "downloads"));
    }

    [Fact]
    public async Task WriteChunks_EmptySource_CarriesTheFilesystemName()
    {
        var backend = new CapturingBackend();

        await backend.WriteChunksAsync("42/x", EmptyChunks(), true, true, CancellationToken.None);

        backend.Calls.ShouldContain(c => c.Tool == "fs_blob_write");
        backend.Calls
            .Where(c => c.Tool == "fs_blob_write")
            .ShouldAllBe(c => Equals(c.Args["filesystem"], "downloads"));
    }

    // A server may serve several filesystems behind one set of tools, so every operation says which
    // one it is for — not only the byte streams. Each refused, so no payload has to be shaped.
    private sealed class RefusedEverywhereBackend()
        : McpFileSystemBackend(null!, "watches", FileSystemOperations.ToolNames)
    {
        public List<(string Tool, Dictionary<string, object?> Args)> Calls { get; } = [];

        protected internal override Task<JsonNode> CallToolAsync(
            string toolName, Dictionary<string, object?> args, CancellationToken ct)
        {
            Calls.Add((toolName, args));
            return Task.FromResult<JsonNode>(ToolError.Create(ToolError.Codes.NotFound, "nothing here"));
        }
    }

    [Fact]
    public async Task EveryOperation_CarriesTheFilesystemName()
    {
        var backend = new RefusedEverywhereBackend();
        var ct = CancellationToken.None;

        await backend.ReadAsync("a", null, null, ct);
        await backend.CreateAsync("a", "c", false, true, ct);
        await backend.EditAsync("a", [new TextEdit("x", "y")], ct);
        await backend.GlobAsync("", "*", ct);
        await backend.SearchAsync("q", false, null, null, null, 5, 1, VfsTextSearchOutputMode.Content, ct);
        await backend.MoveAsync("a", "b", ct);
        await backend.CopyAsync("a", "b", false, true, ct);
        await backend.DeleteAsync("a", ct);
        await backend.InfoAsync("a", ct);
        await backend.ExecAsync("a", "ls", null, ct);
        await backend.QueryAsync("a", "select 1", 5, ct);
        await backend.MoveOutCheckAsync("a", ct);
        await Should.ThrowAsync<FileSystemOperationException>(async () =>
        {
            await foreach (var _ in backend.ReadChunksAsync("a", ct))
            {
            }
        });
        await Should.ThrowAsync<FileSystemOperationException>(() =>
            backend.WriteChunksAsync("a", EmptyChunks(), true, true, ct));

        backend.Calls.Select(c => c.Tool).Distinct().ShouldBe(FileSystemOperations.ToolNames, ignoreOrder: true);
        backend.Calls.ShouldAllBe(c => Equals(c.Args["filesystem"], "watches"));
    }
}