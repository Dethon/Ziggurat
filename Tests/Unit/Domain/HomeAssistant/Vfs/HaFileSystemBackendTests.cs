using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaFileSystemBackendTests
{
    private static HaFileSystem Build()
    {
        var client = new FakeHaClient();
        return new HaFileSystem(new HaCatalogProvider(() => client, new FakeTimeProvider()), () => client);
    }

    [Fact]
    public async Task MutatingOperations_ReturnUnsupported()
    {
        var fs = Build();

        (await fs.CreateAsync("entities/light/x/state.json", "{}", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
        (await fs.EditAsync("entities/light/x/state.json", [], CancellationToken.None))
            .ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
        (await fs.MoveAsync("entities/light/a", "entities/light/b", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
        (await fs.DeleteAsync("entities/light/x", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
        (await fs.CopyAsync("entities/light/a", "entities/light/b", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCopyResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task StreamingChunkApis_ThrowNotSupported()
    {
        var fs = Build();
        Should.Throw<NotSupportedException>(() => { fs.ReadChunksAsync("entities/light/x/state.json", CancellationToken.None); });
        await Should.ThrowAsync<NotSupportedException>(() =>
            fs.WriteChunksAsync("entities/light/x/state.json", AsyncEmpty(), false, true, CancellationToken.None));
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> AsyncEmpty()
    {
        await Task.CompletedTask;
        yield break;
    }
}